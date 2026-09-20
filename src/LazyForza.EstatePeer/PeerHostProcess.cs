using System.Diagnostics;
using System.IO.Pipes;

namespace LazyForza.EstatePeer;

public sealed class PeerHostProcess : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process process;
    private readonly SemaphoreSlim control = new(1, 1);
    private int disposed;
    public PeerInvitation Invitation { get; private set; } = null!;
    public int Port { get; private set; }
    public bool IsRunning => Volatile.Read(ref disposed) == 0 && !process.HasExited;

    private PeerHostProcess(NamedPipeServerStream pipe, Process process) { this.pipe = pipe; this.process = process; }

    public static async Task<PeerHostProcess> StartAsync(string executable, PeerHostStart settings, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var name = "LazyForza.Peer." + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add("--control-pipe");
        start.ArgumentList.Add(name);
        PeerHostProcess? host = null;
        try
        {
            var process = Process.Start(start) ?? throw new IOException("无法启动房主组件。");
            host = new PeerHostProcess(pipe, process);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await PeerPipe.WriteAsync(pipe, settings, timeout.Token).ConfigureAwait(false);
            var reply = await PeerPipe.ReadAsync<PeerHostReply>(pipe, timeout.Token).ConfigureAwait(false);
            if (!reply.Success || reply.Invitation is null) throw new IOException(reply.Error ?? "房主组件未返回邀请代码。");
            host.Invitation = PeerInvitation.Parse(reply.Invitation);
            host.Port = reply.Port;
            return host;
        }
        catch
        {
            if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
            else pipe.Dispose();
            throw;
        }
    }

    public async Task<PeerHostReply> CommandAsync(PeerHostCommand command, CancellationToken token)
    {
        await control.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await PeerPipe.WriteAsync(pipe, command, timeout.Token).ConfigureAwait(false);
            var reply = await PeerPipe.ReadAsync<PeerHostReply>(pipe, timeout.Token).ConfigureAwait(false);
            if (reply.Success && reply.Invitation is not null) Invitation = PeerInvitation.Parse(reply.Invitation);
            return reply;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // A late reply must never be consumed as the response to a different command.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { control.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pipe.Dispose(); // EOF tells the owned helper to checkpoint and close its listeners.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        finally { process.Dispose(); }
    }
}
