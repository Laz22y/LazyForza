using System.Net.WebSockets;
using System.Threading.Channels;

namespace LazyForza.EstatePeer.Host;

internal sealed class PeerSocket : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly CancellationTokenSource cancellation;
    private readonly Channel<Outgoing> commands = Channel.CreateBounded<Outgoing>(64);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task sending;
    private byte[]? snapshot;

    public PeerSocket(WebSocket socket, CancellationToken token)
    {
        this.socket = socket;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        sending = SendLoopAsync();
    }

    public void Snapshot(byte[] value)
    {
        Interlocked.Exchange(ref snapshot, value);
        Signal();
    }

    public Task SendAsync(byte[] bytes)
    {
        var outgoing = new Outgoing(bytes, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!commands.Writer.TryWrite(outgoing))
        {
            Abort();
            return Task.FromException(new IOException("Reliable send queue is full."));
        }
        Signal();
        return outgoing.Sent.Task;
    }

    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    public void Abort()
    {
        try { cancellation.Cancel(); socket.Abort(); }
        catch (ObjectDisposedException) { }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await wake.WaitAsync(cancellation.Token);
                while (commands.Reader.TryRead(out var outgoing))
                {
                    try { await WriteAsync(outgoing.Bytes); outgoing.Sent.TrySetResult(); }
                    catch { outgoing.Sent.TrySetCanceled(); throw; }
                }
                if (Interlocked.Exchange(ref snapshot, null) is { } latest) await WriteAsync(latest);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { Abort(); }
        finally
        {
            commands.Writer.TryComplete();
            while (commands.Reader.TryRead(out var remaining)) remaining.Sent.TrySetCanceled();
        }
    }

    private async Task WriteAsync(byte[] bytes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        Abort();
        await sending;
        cancellation.Dispose();
        // A broadcaster may have captured this peer before unregistering; its Signal remains safe.
    }

    private sealed record Outgoing(byte[] Bytes, TaskCompletionSource Sent);
}
