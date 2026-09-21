using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LazyForza.EstatePeer;

/// <summary>
/// Keeps the public UDP port alive while MsQuic owns a private loopback socket. Datagrams remain
/// opaque TLS ciphertext. Only manually admitted endpoints can reach the fixed local QUIC target.
/// </summary>
internal sealed class PeerUdpPath : IAsyncDisposable
{
    private const int HeaderSize = 21;
    private const int MaximumQuicDatagram = 1472;
    private const int FragmentBytes = 1150; // Including wrapper + IPv6 + UDP stays below the IPv6 minimum MTU.
    private readonly UdpClient socket;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, Route> routes = new();
    private readonly ConcurrentQueue<Task> retired = new();
    private readonly Task receive;
    private readonly Task probe;
    internal int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

    internal PeerUdpPath(int port)
    {
        socket = new UdpClient(AddressFamily.InterNetworkV6);
        socket.Client.DualMode = true;
        socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        receive = ReceiveAsync();
        probe = ProbeAsync();
    }

    internal IPEndPoint Add(PeerReceipt receipt, IReadOnlyList<PeerEndpoint> destinations, IPEndPoint? quicTarget)
    {
        if (routes.Count >= 32) throw new IOException("UDP 连接数量已达上限，请关闭不再使用的连接。");
        var route = new Route(receipt, destinations, quicTarget);
        if (!routes.TryAdd(receipt.Nonce, route)) { route.Dispose(); throw new IOException("此回执已添加，无需重复导入。"); }
        route.Pump = ForwardLocalAsync(route);
        return route.LocalEndpoint;
    }

    internal async Task PrepareClientAttemptAsync(string nonce, CancellationToken token)
    {
        if (!routes.TryGetValue(nonce, out var route) || route.Closed ||
            (!route.Connected && route.Receipt.ExpiresAt <= DateTimeOffset.UtcNow))
            throw new IOException("连接回执已过期，请重新加入并将新回执交给房主。");
        // A new MsQuic connection uses a new local UDP port. Never pin retries to the
        // failed connection's port; retain the public socket and the admitted receipt.
        route.Target = null;
        route.LastRemote = null;
        try { await route.PeerSeen.Task.WaitAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            throw new IOException("未收到房主的 UDP 响应。请确认房主已添加本次回执并保持房间开启，双方允许 UDP 通信；然后点击继续连接。", error);
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await socket.ReceiveAsync(lifetime.Token).ConfigureAwait(false); }
                catch (SocketException) when (!lifetime.IsCancellationRequested)
                {
                    // Windows can surface ICMP errors from an unreachable candidate here.
                    // Keep the receive pump alive for the remaining candidates and retries.
                    await Task.Delay(100, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                var bytes = packet.Buffer;
                if (bytes.Length < HeaderSize || bytes.Length > HeaderSize + 6 + FragmentBytes ||
                    !bytes.AsSpan(0, 4).SequenceEqual("LFZQ"u8)) continue;
                if (!routes.TryGetValue(Convert.ToHexString(bytes.AsSpan(5, 16)), out var route)) continue;
                var remote = Normalize(packet.RemoteEndPoint);
                if (!route.Destinations.Any(candidate => candidate.Port == remote.Port && candidate.Address.Equals(remote.Address))) continue;
                // Probe packets carry no authority and cannot extend admission indefinitely.
                if (!route.Connected && route.Receipt.ExpiresAt <= DateTimeOffset.UtcNow) continue;
                if (bytes[4] == 0 && bytes.Length == HeaderSize)
                { route.PeerSeen.TrySetResult(); continue; }
                byte[]? payload;
                if (bytes[4] == 1) payload = bytes[HeaderSize..];
                else if (bytes[4] == 2) payload = route.Reassemble(bytes.AsSpan(HeaderSize));
                else continue;
                if (payload is null || payload.Length == 0) continue;
                route.PeerSeen.TrySetResult();
                route.LastActivity = Environment.TickCount64;
                route.LastRemote = remote;
                if (route.Target is { } target)
                {
                    try { await route.Local.SendAsync(payload, target, lifetime.Token).ConfigureAwait(false); }
                    catch (ObjectDisposedException) when (route.Closed) { }
                    catch (SocketException) { }
                }
            }
        }
        catch (Exception error) when (Stopping(error)) { }
    }

    private async Task ForwardLocalAsync(Route route)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await route.Local.ReceiveAsync(lifetime.Token).ConfigureAwait(false); }
                catch (SocketException) when (!route.Closed && !lifetime.IsCancellationRequested)
                {
                    await Task.Delay(100, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || packet.Buffer.Length > MaximumQuicDatagram) continue;
                if (route.Target is null) route.Target = packet.RemoteEndPoint;
                if (!packet.RemoteEndPoint.Equals(route.Target)) continue;
                route.LastActivity = Environment.TickCount64;
                foreach (var bytes in Envelopes(route, packet.Buffer))
                {
                    if (route.LastRemote is { } selected)
                        await SendAsync(bytes, selected).ConfigureAwait(false);
                    else
                        foreach (var destination in route.Destinations)
                            await SendAsync(bytes, new(destination.Address, destination.Port)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (Stopping(error) || route.Closed && error is ObjectDisposedException or SocketException) { }
    }

    private async Task ProbeAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                foreach (var route in routes.Values)
                {
                    if ((!route.Connected && route.Receipt.ExpiresAt <= DateTimeOffset.UtcNow) ||
                        (route.Connected && Environment.TickCount64 - route.LastActivity > 180_000))
                    {
                        if (routes.TryRemove(route.Receipt.Nonce, out _)) { route.Dispose(); retired.Enqueue(route.Pump); }
                        continue;
                    }
                    var bytes = Envelope(route, 0, []);
                    foreach (var destination in route.Destinations)
                        await SendAsync(bytes, new(destination.Address, destination.Port)).ConfigureAwait(false);
                }
                while (retired.TryPeek(out var finished) && finished.IsCompletedSuccessfully) retired.TryDequeue(out _);
            } while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (Stopping(error)) { }
    }

    internal void Authenticate(string nonce)
    {
        if (routes.TryGetValue(nonce, out var route)) route.Connected = true;
    }

    internal bool Authenticate(IPEndPoint local)
    {
        foreach (var route in routes.Values)
            if (!route.Closed && route.LocalEndpoint.Equals(local))
            { route.Connected = true; return true; }
        return false;
    }

    private async Task SendAsync(byte[] bytes, IPEndPoint endpoint)
    {
        try { await socket.SendAsync(bytes, endpoint, lifetime.Token).ConfigureAwait(false); }
        catch (SocketException) { } // One unreachable candidate must not terminate the other paths.
    }

    private static byte[] Envelope(Route route, byte kind, byte[] payload)
    {
        var bytes = new byte[HeaderSize + payload.Length];
        "LFZQ"u8.CopyTo(bytes); bytes[4] = kind;
        Convert.FromHexString(route.Receipt.Nonce).CopyTo(bytes, 5);
        payload.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    private static IEnumerable<byte[]> Envelopes(Route route, byte[] payload)
    {
        if (payload.Length <= FragmentBytes) { yield return Envelope(route, 1, payload); yield break; }
        var sequence = unchecked(++route.Sequence);
        for (var index = 0; index < 2; index++)
        {
            var offset = index * FragmentBytes;
            var fragment = new byte[6 + Math.Min(FragmentBytes, payload.Length - offset)];
            BinaryPrimitives.WriteUInt32LittleEndian(fragment, sequence);
            fragment[4] = (byte)index; fragment[5] = 2;
            payload.AsSpan(offset, fragment.Length - 6).CopyTo(fragment.AsSpan(6));
            yield return Envelope(route, 2, fragment);
        }
    }

    private bool Stopping(Exception error) => lifetime.IsCancellationRequested &&
        error is OperationCanceledException or ObjectDisposedException or SocketException;
    private static IPEndPoint Normalize(IPEndPoint endpoint) => endpoint.Address.IsIPv4MappedToIPv6
        ? new(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        socket.Dispose();
        foreach (var route in routes.Values) route.Dispose();
        await Task.WhenAll(routes.Values.Select(route => route.Pump).Concat(retired).Append(receive).Append(probe)).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private sealed class Route : IDisposable
    {
        internal readonly PeerReceipt Receipt;
        internal readonly IReadOnlyList<PeerEndpoint> Destinations;
        internal readonly UdpClient Local = new(new IPEndPoint(IPAddress.Loopback, 0));
        internal readonly IPEndPoint LocalEndpoint;
        internal volatile IPEndPoint? Target;
        internal volatile IPEndPoint? LastRemote;
        internal readonly TaskCompletionSource PeerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Connected;
        internal volatile bool Closed;
        internal long LastActivity = Environment.TickCount64;
        internal Task Pump = Task.CompletedTask;
        internal uint Sequence;
        private readonly Dictionary<uint, (long Created, byte[]? First, byte[]? Second)> fragments = [];
        internal byte[]? Reassemble(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length <= 6 || bytes[4] > 1 || bytes[5] != 2) return null;
            var now = Environment.TickCount64;
            foreach (var key in fragments.Where(item => now - item.Value.Created > 2000).Select(item => item.Key).ToArray()) fragments.Remove(key);
            var keyValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (!fragments.TryGetValue(keyValue, out var entry))
            {
                if (fragments.Count >= 16) return null;
                entry = (now, null, null);
            }
            if (bytes[4] == 0) entry.First = bytes[6..].ToArray(); else entry.Second = bytes[6..].ToArray();
            if (entry.First is null || entry.Second is null) { fragments[keyValue] = entry; return null; }
            fragments.Remove(keyValue);
            if (entry.First.Length != FragmentBytes || entry.First.Length + entry.Second.Length > MaximumQuicDatagram) return null;
            return [.. entry.First, .. entry.Second];
        }
        internal Route(PeerReceipt receipt, IReadOnlyList<PeerEndpoint> destinations, IPEndPoint? target)
        { Receipt = receipt; Destinations = destinations; Target = target; LocalEndpoint = (IPEndPoint)Local.Client.LocalEndPoint!; }
        public void Dispose() { Closed = true; Local.Dispose(); }
    }
}
