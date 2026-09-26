using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace LazyForza.EstatePeer;

internal static class PeerReverseHandshake
{
    internal static async Task AuthenticateAsync(Stream stream, PeerReceipt receipt, bool host, CancellationToken token)
    {
        var identifier = PeerPathAuthentication.Identifier(receipt);
        var key = PeerPathAuthentication.Key(receipt, "reverse-host");
        var replyKey = PeerPathAuthentication.Key(receipt, "reverse-join");
        var challenge = new byte[64];
        var proof = new byte[32];
        if (host)
        {
            RandomNumberGenerator.Fill(challenge.AsSpan(0, 32));
            await stream.WriteAsync(identifier, token).ConfigureAwait(false);
            await stream.WriteAsync(challenge.AsMemory(0, 32), token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(challenge.AsMemory(32), token).ConfigureAwait(false);
            await stream.WriteAsync(HMACSHA256.HashData(key, challenge), token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(proof, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(replyKey, challenge)))
                throw new IOException("反向连接身份验证失败。");
        }
        else
        {
            var received = new byte[16];
            await stream.ReadExactlyAsync(received, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(received, identifier)) throw new IOException("反向连接回执不匹配。");
            await stream.ReadExactlyAsync(challenge.AsMemory(0, 32), token).ConfigureAwait(false);
            RandomNumberGenerator.Fill(challenge.AsSpan(32));
            await stream.WriteAsync(challenge.AsMemory(32), token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(proof, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(key, challenge)))
                throw new IOException("反向连接身份验证失败。");
            await stream.WriteAsync(HMACSHA256.HashData(replyKey, challenge), token).ConfigureAwait(false);
        }
    }
}

/// <summary>The room owner dials admitted players. Only the fixed local player-facing TLS port is forwarded.</summary>
public sealed class PeerReverseHost(int roomPort) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Dictionary<string, Task> tickets = [];

    public void Admit(PeerReceipt receipt)
    {
        if (!receipt.Authenticated || !receipt.Transports.HasFlag(PeerReceiptTransport.ReverseTcp))
            throw new IOException("此回执不支持反向连接。");
        lock (sync)
        {
            foreach (var key in tickets.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray()) tickets.Remove(key);
            if (tickets.ContainsKey(receipt.Nonce)) throw new IOException("此回执已添加，无需重复导入。");
            if (tickets.Count >= 32) throw new IOException("反向连接数量已达上限。");
            var state = new TicketState();
            tickets.Add(receipt.Nonce, Task.WhenAll(Enumerable.Range(0, 4).Select(index => RunAsync(receipt, state, index))));
        }
    }

    private async Task RunAsync(PeerReceipt receipt, TicketState state, int slot)
    {
        var candidate = slot;
        try
        {
            while (!lifetime.IsCancellationRequested && (receipt.ExpiresAt > DateTimeOffset.UtcNow ||
                Volatile.Read(ref state.Active) > 0 || Environment.TickCount64 - Interlocked.Read(ref state.LastUsed) < 180_000))
            {
                var endpoint = state.Selected ?? receipt.Candidates[candidate++ % receipt.Candidates.Count];
                using var remote = new TcpClient { NoDelay = true };
                var activated = false;
                try
                {
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(5));
                        await remote.ConnectAsync(endpoint.Address, endpoint.Port, connect.Token).ConfigureAwait(false);
                        await PeerReverseHandshake.AuthenticateAsync(remote.GetStream(), receipt, host: true, connect.Token).ConfigureAwait(false);
                    }
                    state.Selected = endpoint;
                    // Idle pool sockets do not consume Kestrel connections or open any local service.
                    var start = new byte[1];
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(30));
                        await remote.GetStream().ReadExactlyAsync(start, idle.Token).ConfigureAwait(false);
                    }
                    if (start[0] != 0xa5) throw new IOException("反向连接请求无效。");
                    Interlocked.Exchange(ref state.LastUsed, Environment.TickCount64);
                    Interlocked.Increment(ref state.Active); activated = true;
                    using var local = new TcpClient { NoDelay = true };
                    await local.ConnectAsync(IPAddress.Loopback, roomPort, lifetime.Token).ConfigureAwait(false);
                    await PeerTunnelCopy.RunAsync(remote.GetStream(), local.GetStream(), lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error))
                { if (ReferenceEquals(state.Selected, endpoint)) state.Selected = null; }
                finally
                {
                    if (activated) { Interlocked.Decrement(ref state.Active); Interlocked.Exchange(ref state.LastUsed, Environment.TickCount64); }
                }
                await Task.Delay(500, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (sync) pending = tickets.Values.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private sealed class TicketState
    {
        internal volatile PeerEndpoint? Selected;
        internal int Active;
        internal long LastUsed = long.MinValue / 2;
    }
}

/// <summary>Accepts authenticated reverse carriers; the existing HTTPS/WSS still pins the room owner's certificate.</summary>
public sealed class PeerReverseJoin : IAsyncDisposable
{
    private readonly PeerInvitation invitation;
    private readonly TcpListener listener = new(IPAddress.IPv6Any, 0);
    private readonly TcpListener local = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<TcpClient> carriers = Channel.CreateBounded<TcpClient>(4);
    private readonly ConcurrentDictionary<int, Task> workers = new();
    private readonly Task accept, acceptLocal;
    private readonly TaskCompletionSource seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim connecting = new(1, 1);
    private int nextId, disposed;
    private volatile bool connected;
    public PeerReceipt Receipt { get; }
    public PeerConnection Connection { get; }

    public PeerReverseJoin(PeerInvitation invitation, PeerReceipt? udpReceipt = null)
    {
        if (!invitation.SupportsAssistedConnection) throw new IOException("房主组件尚不支持反向连接。");
        this.invitation = invitation;
        try
        {
            if (udpReceipt is not null) listener = new(IPAddress.IPv6Any, udpReceipt.Candidates[0].Port);
            listener.Server.DualMode = true;
            listener.Start(16); local.Start(16);
            Receipt = udpReceipt is null
                ? PeerReceipt.Create(invitation, PeerAddresses.Collect(((IPEndPoint)listener.LocalEndpoint).Port), PeerReceiptTransport.ReverseTcp)
                : udpReceipt with { Transports = udpReceipt.Transports | PeerReceiptTransport.ReverseTcp };
            if (Receipt.Candidates.Count == 0) throw new IOException("未找到可用于回执的网络地址。");
            Connection = new(invitation, new(IPAddress.Loopback, ((IPEndPoint)local.LocalEndpoint).Port), this);
            accept = AcceptAsync(listener, incoming: true);
            acceptLocal = AcceptAsync(local, incoming: false);
        }
        catch { listener.Stop(); local.Stop(); lifetime.Dispose(); connecting.Dispose(); throw; }
    }

    public async Task<PeerConnection> ConnectAsync(CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = attempt.Token;
        await connecting.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try { await seen.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
            catch (TimeoutException error)
            { throw new IOException("未收到房主的反向 TCP 连接。请确认房主已添加回执，并检查本机 TCP 入站路径。", error); }
            using var http = Connection.CreateHttpClient();
            var identity = await http.GetFromJsonAsync<PeerIdentity>("peer/identity", token).ConfigureAwait(false);
            if (identity is null || identity.RoomId != invitation.RoomId || identity.Generation != invitation.Generation || identity.ProtocolVersion != 2)
                throw new IOException("房主身份已变更，请索取新的邀请代码。");
            connected = true;
            return Connection;
        }
        finally { connecting.Release(); }
    }

    private async Task AcceptAsync(TcpListener source, bool incoming)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var tcp = await source.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                if (workers.Count >= 24) { tcp.Dispose(); continue; }
                var id = Interlocked.Increment(ref nextId);
                workers[id] = incoming ? AdmitAsync(tcp) : ForwardAsync(tcp);
                _ = workers[id].ContinueWith(_ => workers.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested && PeerTunnelCopy.IsConnectionError(error)) { }
    }

    private async Task AdmitAsync(TcpClient tcp)
    {
        var queued = false;
        try
        {
            if (!connected && Receipt.ExpiresAt <= DateTimeOffset.UtcNow) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await PeerReverseHandshake.AuthenticateAsync(tcp.GetStream(), Receipt, host: false, timeout.Token).ConfigureAwait(false);
            // Expired idle carriers must not fill the queue after a cancelled join attempt.
            while (carriers.Reader.TryPeek(out var old) && IsClosed(old) && carriers.Reader.TryRead(out old)) old.Dispose();
            queued = carriers.Writer.TryWrite(tcp);
            if (queued) seen.TrySetResult();
        }
        catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
        finally { if (!queued) tcp.Dispose(); }
    }

    private async Task ForwardAsync(TcpClient tcp)
    {
        using (tcp)
        {
            TcpClient? carrier = null;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    do
                    {
                        carrier?.Dispose();
                        carrier = await carriers.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                    } while (IsClosed(carrier));
                    await carrier.GetStream().WriteAsync(new byte[] { 0xa5 }, timeout.Token).ConfigureAwait(false);
                }
                await PeerTunnelCopy.RunAsync(tcp.GetStream(), carrier.GetStream(), lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (PeerTunnelCopy.IsConnectionError(error)) { }
            finally { carrier?.Dispose(); }
        }
    }

    private static bool IsClosed(TcpClient tcp)
    {
        try { return tcp.Client.Poll(0, SelectMode.SelectRead) && tcp.Available == 0; }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { return true; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        listener.Stop(); local.Stop();
        await Task.WhenAll(accept, acceptLocal).ConfigureAwait(false);
        await Task.WhenAll(workers.Values).ConfigureAwait(false);
        while (carriers.Reader.TryRead(out var carrier)) carrier.Dispose();
        connecting.Dispose(); lifetime.Dispose();
    }
}
