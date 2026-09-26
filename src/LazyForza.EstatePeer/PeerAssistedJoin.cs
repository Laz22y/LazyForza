using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;

namespace LazyForza.EstatePeer;

/// <summary>One manually exchanged receipt; race UDP and reverse TCP without external signalling.</summary>
[SupportedOSPlatform("windows")]
public sealed class PeerAssistedJoin : IAsyncDisposable
{
    private readonly PeerQuicJoin? udp;
    private readonly PeerReverseJoin? reverse;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connecting = new(1, 1);
    private int disposed;
    public PeerReceipt Receipt { get; }

    public static bool IsSupported(PeerInvitation invitation) => invitation.SupportsAssistedConnection ||
        invitation.SupportsUdp && PeerQuicHost.IsSupported;

    public PeerAssistedJoin(PeerInvitation invitation)
    {
        try
        {
            if (invitation.SupportsUdp && PeerQuicHost.IsSupported)
            {
                try { udp = new(invitation); }
                catch (SocketException) when (invitation.SupportsAssistedConnection) { }
            }
            if (invitation.SupportsAssistedConnection)
            {
                try { reverse = new(invitation, udp?.Receipt); }
                catch (SocketException) when (udp is not null) { } // The UDP port can already be occupied by another TCP app.
            }
            Receipt = reverse?.Receipt ?? udp?.Receipt ?? throw new IOException("没有可用的回执连接方式，请更新房主组件或检查网络接口。");
        }
        catch
        {
            udp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            reverse?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            lifetime.Dispose(); connecting.Dispose(); throw;
        }
    }

    public async Task<PeerConnection> ConnectAsync(CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await connecting.WaitAsync(attempt.Token).ConfigureAwait(false);
        var tasks = new List<Task<PeerConnection>>();
        try
        {
            if (reverse is not null) tasks.Add(reverse.ConnectAsync(attempt.Token));
            if (udp is not null) tasks.Add(udp.ConnectAsync(attempt.Token));
            var errors = new List<Exception>();
            while (tasks.Count > 0)
            {
                var task = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(task);
                try
                {
                    var result = await task.ConfigureAwait(false);
                    return new(result.Invitation, new(IPAddress.Parse(result.Origin.Host.Trim('[', ']')), result.Origin.Port), this);
                }
                catch (Exception error) when (IsAttemptError(error))
                { errors.Add(error); }
            }
            token.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            throw new IOException(string.Join(Environment.NewLine, errors.Select(error => error.Message).Distinct()),
                new AggregateException(errors));
        }
        finally
        {
            await attempt.CancelAsync().ConfigureAwait(false);
            foreach (var task in tasks)
                try { await task.ConfigureAwait(false); }
                catch (Exception error) when (IsAttemptError(error)) { }
            connecting.Release();
        }
    }

    private static bool IsAttemptError(Exception error) => error is IOException or SocketException or HttpRequestException or
        OperationCanceledException or AuthenticationException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await connecting.WaitAsync().ConfigureAwait(false);
        try
        {
            if (udp is not null) await udp.DisposeAsync().ConfigureAwait(false);
            if (reverse is not null) await reverse.DisposeAsync().ConfigureAwait(false);
        }
        finally { connecting.Release(); connecting.Dispose(); lifetime.Dispose(); }
    }
}
