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
        try
        {
            socket.Client.DualMode = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        catch { socket.Dispose(); lifetime.Dispose(); throw; }
        receive = ReceiveAsync();
        probe = ProbeAsync();
    }

    internal IPEndPoint Add(PeerReceipt receipt, IReadOnlyList<PeerEndpoint> destinations, IPEndPoint? quicTarget)
    {
        if (routes.Count >= 32) throw new IOException("UDP 连接数量已达上限，请关闭不再使用的连接。");
        var route = new Route(receipt, destinations, quicTarget);
        if (!routes.TryAdd(route.Identifier, route)) { route.Dispose(); throw new IOException("此回执已添加，无需重复导入。"); }
        route.Pump = ForwardLocalAsync(route);
        return route.LocalEndpoint;
    }

    internal async Task PrepareClientAttemptAsync(string nonce, CancellationToken token)
    {
        var route = routes.Values.FirstOrDefault(item => item.Receipt.Nonce == nonce);
        if (route is null || route.Closed ||
            (!route.Connected && route.Receipt.ExpiresAt <= DateTimeOffset.UtcNow))
            throw new IOException("连接回执已过期，请重新加入并将新回执交给房主。");
        // A new MsQuic connection uses a new local UDP port. Never pin retries to the
        // failed connection's port; retain the public socket and the admitted receipt.
        route.Target = null;
        route.LastRemote = null;
        route.PeerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = route.Security?.BeginAttempt() ?? route.PeerSeen.Task;
        await ProbeRouteAsync(route).ConfigureAwait(false);
        try { await ready.WaitAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false); }
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
                if (bytes.Length < HeaderSize) continue;
                var authenticated = bytes.AsSpan(0, 4).SequenceEqual("LFZS"u8);
                if (authenticated ? bytes.Length < PeerUdpSession.HeaderBytes + PeerUdpSession.TagBytes :
                    bytes.Length > HeaderSize + 6 + FragmentBytes || !bytes.AsSpan(0, 4).SequenceEqual("LFZQ"u8)) continue;
                if (!routes.TryGetValue(Convert.ToHexString(bytes.AsSpan(authenticated ? 6 : 5, 16)), out var route)) continue;
                var remote = Normalize(packet.RemoteEndPoint);
                // Probe packets carry no authority and cannot extend admission indefinitely.
                if (!route.Connected && route.Receipt.ExpiresAt <= DateTimeOffset.UtcNow) continue;
                byte kind;
                ReadOnlyMemory<byte> body;
                if (route.Security is { } security)
                {
                    if (!authenticated) continue;
                    var decoded = security.Receive(bytes, remote, out var responses);
                    foreach (var response in responses) await SendAsync(response.Bytes, response.Remote).ConfigureAwait(false);
                    if (decoded is null) continue;
                    kind = decoded.Value.Kind; body = decoded.Value.Payload;
                }
                else
                {
                    if (authenticated || !route.Destinations.Any(candidate => candidate.Port == remote.Port && candidate.Address.Equals(remote.Address))) continue;
                    if (bytes[4] == 0 && bytes.Length == HeaderSize)
                    { route.PeerSeen.TrySetResult(); continue; }
                    kind = bytes[4]; body = bytes.AsMemory(HeaderSize);
                }
                byte[]? payload;
                if (kind == 1) payload = body.ToArray();
                else if (kind == 2) payload = route.Reassemble(body.Span);
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
                    if ((route.Security?.Selected ?? route.LastRemote) is { } selected)
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
                        if (routes.TryRemove(route.Identifier, out _)) { route.Dispose(); retired.Enqueue(route.Pump); }
                        continue;
                    }
                    await ProbeRouteAsync(route).ConfigureAwait(false);
                }
                while (retired.TryPeek(out var finished) && finished.IsCompletedSuccessfully) retired.TryDequeue(out _);
            } while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (Stopping(error)) { }
    }

    internal void Authenticate(string nonce)
    {
        var route = routes.Values.FirstOrDefault(item => item.Receipt.Nonce == nonce);
        if (route is not null) route.Connected = true;
    }

    internal void Revoke(string nonce)
    {
        var route = routes.Values.FirstOrDefault(item => item.Receipt.Nonce == nonce);
        if (route is not null && routes.TryRemove(route.Identifier, out _)) { route.Dispose(); retired.Enqueue(route.Pump); }
    }

    private async Task ProbeRouteAsync(Route route)
    {
        if (route.Security is { } security)
        {
            foreach (var probe in security.Probe()) await SendAsync(probe.Bytes, probe.Remote).ConfigureAwait(false);
        }
        else
        {
            var bytes = Envelope(route, 0, []);
            foreach (var destination in route.Destinations)
                await SendAsync(bytes, new(destination.Address, destination.Port)).ConfigureAwait(false);
        }
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
        if (route.Security is { } security) return security.Encode(kind, payload);
        var bytes = new byte[HeaderSize + payload.Length];
        "LFZQ"u8.CopyTo(bytes); bytes[4] = kind;
        Convert.FromHexString(route.Receipt.Nonce).CopyTo(bytes, 5);
        payload.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    private static IEnumerable<byte[]> Envelopes(Route route, byte[] payload)
    {
        var fragmentBytes = route.Security?.FragmentBytes ?? FragmentBytes;
        if (payload.Length <= fragmentBytes) { yield return Envelope(route, 1, payload); yield break; }
        var sequence = unchecked(++route.Sequence);
        var count = (payload.Length + fragmentBytes - 1) / fragmentBytes;
        for (var index = 0; index < count; index++)
        {
            var offset = index * fragmentBytes;
            var fragment = new byte[6 + Math.Min(fragmentBytes, payload.Length - offset)];
            BinaryPrimitives.WriteUInt32LittleEndian(fragment, sequence);
            fragment[4] = (byte)index; fragment[5] = (byte)count;
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
        internal readonly PeerUdpSession? Security;
        internal string Identifier => Security?.Identifier ?? Receipt.Nonce;
        internal readonly IReadOnlyList<PeerEndpoint> Destinations;
        internal readonly UdpClient Local = new(new IPEndPoint(IPAddress.Loopback, 0));
        internal readonly IPEndPoint LocalEndpoint;
        internal volatile IPEndPoint? Target;
        internal volatile IPEndPoint? LastRemote;
        internal volatile TaskCompletionSource PeerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Connected;
        internal volatile bool Closed;
        internal long LastActivity = Environment.TickCount64;
        internal Task Pump = Task.CompletedTask;
        internal uint Sequence;
        private readonly Dictionary<uint, (long Created, byte[]?[] Parts)> fragments = [];
        internal byte[]? Reassemble(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length <= 6 || bytes.Length > 6 + FragmentBytes || bytes[5] < 2 ||
                bytes[5] > (Security is null ? 2 : 3) || bytes[4] >= bytes[5]) return null;
            var now = Environment.TickCount64;
            foreach (var key in fragments.Where(item => now - item.Value.Created > 2000).Select(item => item.Key).ToArray()) fragments.Remove(key);
            var keyValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (!fragments.TryGetValue(keyValue, out var entry))
            {
                if (fragments.Count >= 16) return null;
                entry = (now, new byte[bytes[5]][]);
            }
            if (entry.Parts.Length != bytes[5]) return null;
            entry.Parts[bytes[4]] = bytes[6..].ToArray();
            if (entry.Parts.Any(part => part is null)) { fragments[keyValue] = entry; return null; }
            fragments.Remove(keyValue);
            var width = entry.Parts[0]!.Length;
            if ((Security is null && width != FragmentBytes) || entry.Parts.Sum(part => part!.Length) > MaximumQuicDatagram ||
                entry.Parts.Take(entry.Parts.Length - 1).Any(part => part!.Length != width) || entry.Parts[^1]!.Length > width) return null;
            return entry.Parts.SelectMany(part => part!).ToArray();
        }
        internal Route(PeerReceipt receipt, IReadOnlyList<PeerEndpoint> destinations, IPEndPoint? target)
        {
            Receipt = receipt; Destinations = destinations; Target = target; LocalEndpoint = (IPEndPoint)Local.Client.LocalEndPoint!;
            if (receipt.Authenticated) Security = new(receipt, destinations, host: target is not null);
        }
        public void Dispose() { Closed = true; Local.Dispose(); }
    }
}
