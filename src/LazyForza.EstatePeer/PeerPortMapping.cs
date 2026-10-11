using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LazyForza.EstatePeer;

internal sealed record PeerGateway(IPAddress Local, IPAddress Gateway);
internal interface IPeerMapping : IAsyncDisposable
{
    PeerEndpoint Endpoint { get; }
    DateTimeOffset ExpiresAt { get; }
    Task RenewAsync(CancellationToken token);
}
internal sealed class MappingDeniedException : IOException;

/// <summary>Opt-in leases owned by the listener, never by a window. No public discovery service.</summary>
public sealed class PeerPortMapping : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private readonly int port;
    private readonly bool tcp, udp;
    private readonly Func<IReadOnlyList<PeerGateway>> gateways;
    private readonly Func<PeerGateway, int, bool, CancellationToken, Task<IPeerMapping?>> create;
    private readonly List<IPeerMapping> leases = [];
    private readonly Dictionary<IPeerMapping, bool> transports = [];
    private readonly HashSet<PeerGateway> denied = [];
    private PeerEndpoint[] endpoints = [];
    private int generation, disposed;
    public IReadOnlyList<PeerEndpoint> Endpoints => Volatile.Read(ref endpoints);

    public PeerPortMapping(int port, bool tcp, bool udp) : this(port, tcp, udp, FindGateways, CreateAsync) { }
    internal PeerPortMapping(int port, bool tcp, bool udp, Func<IReadOnlyList<PeerGateway>> gateways,
        Func<PeerGateway, int, bool, CancellationToken, Task<IPeerMapping?>> create)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.port = port; this.tcp = tcp; this.udp = udp; this.gateways = gateways; this.create = create;
        NetworkChange.NetworkAddressChanged += Changed;
        worker = RunAsync();
    }
    private void Changed(object? sender, EventArgs args) => Interlocked.Increment(ref generation);
    private async Task RunAsync()
    {
        var previous = -1; var signature = ""; var retryAt = DateTimeOffset.MinValue;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                var current = generation;
                PeerGateway[] available;
                try { available = gateways().Take(2).ToArray(); }
                catch (NetworkInformationException)
                { PeerDiagnostics.Record(PeerStage.Mapping, PeerOutcome.Unavailable); available = []; }
                var nextSignature = string.Join(";", available.Select(g => $"{g.Local}/{g.Gateway}"));
                var changed = previous != current || nextSignature != signature;
                if (changed)
                {
                    Volatile.Write(ref endpoints, []);
                    await ReleaseAsync().ConfigureAwait(false);
                    denied.Clear();
                    previous = current; signature = nextSignature; retryAt = DateTimeOffset.MinValue;
                    PeerDiagnostics.Record(PeerStage.Network, PeerOutcome.Changed);
                }
                if (DateTimeOffset.UtcNow >= retryAt)
                {
                    foreach (var gateway in available)
                    {
                        if (denied.Contains(gateway)) continue;
                        if (leases.Count == (tcp ? 1 : 0) + (udp ? 1 : 0)) break;
                        foreach (var isTcp in new[] { true, false }.Where(t => t ? tcp : udp))
                        {
                            if (transports.Values.Contains(isTcp)) continue;
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(8));
                            try
                            {
                                var lease = await create(gateway, port, isTcp, deadline.Token).ConfigureAwait(false);
                                if (lease is null) continue;
                                if (current != generation || lifetime.IsCancellationRequested) await lease.DisposeAsync().ConfigureAwait(false);
                                else { leases.Add(lease); transports.Add(lease, isTcp); PeerDiagnostics.Record(PeerStage.Mapping, PeerOutcome.Succeeded, address: lease.Endpoint.Address); }
                            }
                            catch (MappingDeniedException) { denied.Add(gateway); PeerDiagnostics.Record(PeerStage.Mapping, PeerOutcome.Rejected); break; }
                            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or HttpRequestException)
                            { PeerDiagnostics.Record(PeerStage.Mapping, PeerDiagnostics.Failure(e, lifetime.IsCancellationRequested)); }
                        }
                    }
                    retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                foreach (var lease in leases.ToArray())
                {
                    if (lease.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await lease.RenewAsync(deadline.Token).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or HttpRequestException)
                    {
                        leases.Remove(lease); transports.Remove(lease); await lease.DisposeAsync().ConfigureAwait(false);
                        PeerDiagnostics.Record(PeerStage.Mapping, PeerOutcome.Unavailable);
                    }
                }
                Volatile.Write(ref endpoints, current == generation ? leases.Where(l => l.ExpiresAt > DateTimeOffset.UtcNow)
                    .Select(l => l.Endpoint).Distinct().ToArray() : []);
            } while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (NetworkInformationException) { PeerDiagnostics.Record(PeerStage.Mapping, PeerOutcome.Unavailable); }
        finally { Volatile.Write(ref endpoints, []); await ReleaseAsync().ConfigureAwait(false); }
    }
    private async Task ReleaseAsync()
    {
        foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
        leases.Clear();
        transports.Clear();
    }
    private static IReadOnlyList<PeerGateway> FindGateways() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .SelectMany(n =>
        {
            var p = n.GetIPProperties();
            return p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && PeerInvitation.IsAllowedAddress(a.Address))
                .SelectMany(a => p.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    PeerDiagnostics.Classify(g.Address) is "private" or "shared").Select(g => new PeerGateway(a.Address, g.Address)));
        }).Distinct().Take(2).ToArray();
    private static async Task<IPeerMapping?> CreateAsync(PeerGateway gateway, int port, bool tcp, CancellationToken token)
    {
        foreach (var pcp in new[] { true, false })
        {
            var mapping = new GatewayMapping(gateway, port, tcp, pcp);
            try { await mapping.RenewAsync(token).ConfigureAwait(false); return mapping; }
            catch (MappingDeniedException) { await mapping.DisposeAsync(); throw; }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            { await mapping.DisposeAsync(); token.ThrowIfCancellationRequested(); }
        }
        return await PeerUpnpMapping.CreateAsync(gateway, port, tcp, token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        NetworkChange.NetworkAddressChanged -= Changed;
        await lifetime.CancelAsync().ConfigureAwait(false); await worker.ConfigureAwait(false); lifetime.Dispose();
    }
}

internal sealed class GatewayMapping(PeerGateway gateway, int port, bool tcp, bool pcp, int gatewayPort = 5351) : IPeerMapping
{
    private readonly byte[] nonce = RandomNumberGenerator.GetBytes(12);
    private readonly UdpClient socket = new(new IPEndPoint(gateway.Local, 0));
    private bool owned;
    private uint? lastEpoch;
    public PeerEndpoint Endpoint { get; private set; } = new(gateway.Local, port);
    public DateTimeOffset ExpiresAt { get; private set; }
    public Task RenewAsync(CancellationToken token) => MapAsync(120, token);
    private async Task MapAsync(uint lifetime, CancellationToken token)
    {
        byte[] request;
        IPAddress? external = null;
        if (pcp)
        {
            request = new byte[60]; request[0] = 2; request[1] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), lifetime);
            gateway.Local.MapToIPv6().GetAddressBytes().CopyTo(request, 8);
            nonce.CopyTo(request, 24); request[36] = tcp ? (byte)6 : (byte)17;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(40), (ushort)port);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(42), (ushort)(owned ? Endpoint.Port : port));
            if (owned) Endpoint.Address.MapToIPv6().GetAddressBytes().CopyTo(request, 44);
        }
        else
        {
            var addressReply = await ExchangeAsync([0, 0], 128, token).ConfigureAwait(false);
            if (addressReply.Length != 12) throw new IOException("Invalid gateway response.");
            external = new IPAddress(addressReply.AsSpan(8, 4));
            request = new byte[12]; request[1] = tcp ? (byte)2 : (byte)1;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)port);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)(owned ? Endpoint.Port : port));
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), lifetime);
        }
        var reply = await ExchangeAsync(request, (byte)(request[1] | 128), token).ConfigureAwait(false);
        var valid = TryReadMapping(reply, pcp, tcp, port, nonce, external, out var endpoint, out var granted);
        if (!valid || lifetime > 0 && granted == 0) throw new IOException("Invalid gateway mapping.");
        var epoch = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(pcp ? 8 : 4));
        if (lastEpoch is uint previous && epoch < previous) PeerDiagnostics.Record(PeerStage.Mapping, PeerOutcome.Changed);
        lastEpoch = epoch;
        Endpoint = endpoint!; ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(granted); owned = lifetime > 0;
        // Keep ownership long enough for DisposeAsync to revoke an unexpectedly long lease.
        if (lifetime > 0 && granted > 120) throw new IOException("Gateway did not grant a short lease.");
    }
    internal static bool TryReadMapping(byte[] reply, bool pcp, bool tcp, int port, byte[] nonce,
        IPAddress? external, out PeerEndpoint? endpoint, out uint lifetime)
    {
        endpoint = null; lifetime = 0;
        if (pcp)
        {
            if (reply.Length != 60 || reply[0] != 2 || reply[1] != 129 || reply[3] != 0 ||
                !reply.AsSpan(24, 12).SequenceEqual(nonce) || reply[36] != (tcp ? 6 : 17) ||
                BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(40)) != port) return false;
            external = new IPAddress(reply.AsSpan(44, 16));
            if (external.IsIPv4MappedToIPv6) external = external.MapToIPv4();
            endpoint = new(external, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(42)));
            lifetime = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4));
        }
        else
        {
            if (reply.Length != 16 || reply[0] != 0 || reply[1] != (tcp ? 130 : 129) ||
                BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)) != 0 ||
                BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)) != port || external is null) return false;
            endpoint = new(external, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10)));
            lifetime = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12));
        }
        return endpoint.Port > 0 && PeerInvitation.IsAllowedAddress(endpoint.Address);
    }
    private async Task<byte[]> ExchangeAsync(byte[] request, byte opcode, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(pcp ? 3 : 2));
        var destination = new IPEndPoint(gateway.Gateway, gatewayPort);
        var delay = pcp ? 3000 : 250;
        while (true)
        {
            await socket.SendAsync(request, destination, deadline.Token).ConfigureAwait(false);
            using var round = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            round.CancelAfter(delay);
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(round.Token).ConfigureAwait(false);
                    var b = result.Buffer;
                    if (!result.RemoteEndPoint.Equals(destination) || b.Length < (pcp ? 24 : 8) || b[0] != (pcp ? 2 : 0) || b[1] != opcode) continue;
                    var error = pcp ? b[3] : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(2));
                    if (error == 2) throw new MappingDeniedException();
                    if (error != 0) throw new IOException("Gateway mapping unavailable.");
                    if (pcp && (b.Length != 60 || !b.AsSpan(24, 12).SequenceEqual(request.AsSpan(24, 12)) ||
                        b[36] != request[36] || !b.AsSpan(40, 2).SequenceEqual(request.AsSpan(40, 2)))) continue;
                    if (!pcp && request[1] != 0 && (b.Length != 16 || !b.AsSpan(8, 2).SequenceEqual(request.AsSpan(4, 2)))) continue;
                    return b;
                }
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { delay = Math.Min(delay * 2, 1000); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (owned)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            try { await MapAsync(0, deadline.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        }
        socket.Dispose();
    }
}
