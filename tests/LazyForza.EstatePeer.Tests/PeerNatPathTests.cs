using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using LazyForza.EstatePeer.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.EstatePeer.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class PeerNatPathTests
{
    [TestMethod]
    [TestCategory("Extended")]
    [DataRow(700)]
    [DataRow(1500)]
    public async Task QuicCrossesRewrittenSourcePortsAndRecoversAfterMappingChanges(int maximumDatagram)
    {
        if (!PeerQuicHost.IsSupported) { Assert.Inconclusive("QUIC unavailable."); return; }
        using var files = new PeerTests.TestDirectory();
        var settings = PeerTests.Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        await using var nat = new NatPath(room.Port, maximumDatagram);
        var invitation = new PeerInvitation
        {
            RoomId = room.Invitation.RoomId, Generation = room.Invitation.Generation, ExpiresAt = room.Invitation.ExpiresAt,
            PublicKeySha256 = room.Invitation.PublicKeySha256, SupportsUdp = true, SupportsAssistedConnection = true,
            Candidates = [nat.HostEndpoint]
        };
        await using var pending = new PeerQuicJoin(invitation);
        var observedReceipt = pending.Receipt with { Candidates = [nat.UnreachableReceiptEndpoint] };
        Assert.IsTrue(room.Control(new("acceptReceipt", observedReceipt.Encode())).Success);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connection = await pending.ConnectAsync(deadline.Token);
        using var http = connection.CreateHttpClient(settings.Password);
        var expected = File.ReadAllBytes(settings.TrackPackagePath);
        CollectionAssert.AreEqual(expected, await http.GetByteArrayAsync("peer/track", deadline.Token));
        if (maximumDatagram == 700) await nat.LargeProbeDropped.Task.WaitAsync(deadline.Token);
        nat.Rebind();
        await nat.ReboundReply.Task.WaitAsync(deadline.Token);
        CollectionAssert.AreEqual(expected, await http.GetByteArrayAsync("peer/track", deadline.Token));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReceiptNegotiationFallsBackToReverseTcpWhenUdpCannotReachTheHost(bool offerUdp)
    {
        using var files = new PeerTests.TestDirectory();
        var settings = PeerTests.Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        using var blackhole = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var unreachable = PeerAddresses.Collect(((IPEndPoint)blackhole.Client.LocalEndPoint!).Port);
        var invitation = new PeerInvitation
        {
            RoomId = room.Invitation.RoomId, Generation = room.Invitation.Generation, ExpiresAt = room.Invitation.ExpiresAt,
            PublicKeySha256 = room.Invitation.PublicKeySha256, SupportsUdp = offerUdp && room.Invitation.SupportsUdp,
            SupportsAssistedConnection = true, Candidates = unreachable
        };
        await using var pending = new PeerAssistedJoin(invitation);
        Assert.IsTrue(room.Control(new("acceptReceipt", pending.Receipt.Encode())).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connection = await pending.ConnectAsync(timeout.Token);
        using var http = connection.CreateHttpClient(settings.Password);
        CollectionAssert.AreEqual(File.ReadAllBytes(settings.TrackPackagePath), await http.GetByteArrayAsync("peer/track", timeout.Token));
    }

    // A local deterministic fixture, not a production relay. The peers only see their own test endpoints.
    private sealed class NatPath : IAsyncDisposable
    {
        private readonly UdpClient front = Bind();
        private readonly UdpClient[] mappings = [Bind(), Bind()];
        private readonly UdpClient blackhole = Bind();
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task[] pumps;
        private readonly IPEndPoint host;
        private readonly int maximumDatagram;
        private IPEndPoint? client;
        private volatile int active;
        internal PeerEndpoint HostEndpoint { get; }
        internal PeerEndpoint UnreachableReceiptEndpoint { get; }
        internal TaskCompletionSource LargeProbeDropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReboundReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal NatPath(int roomPort, int maximumDatagram)
        {
            this.maximumDatagram = maximumDatagram;
            HostEndpoint = PeerAddresses.Collect(((IPEndPoint)front.Client.LocalEndPoint!).Port).First();
            UnreachableReceiptEndpoint = new(HostEndpoint.Address, ((IPEndPoint)blackhole.Client.LocalEndPoint!).Port);
            host = new(HostEndpoint.Address, roomPort);
            pumps = [FrontAsync(), BackAsync(0), BackAsync(1)];
        }

        internal void Rebind() => active = 1;
        private bool Drop(byte[] bytes)
        {
            if (bytes.Length <= maximumDatagram) return false;
            LargeProbeDropped.TrySetResult(); return true;
        }

        private async Task FrontAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var packet = await front.ReceiveAsync(lifetime.Token);
                    client = packet.RemoteEndPoint;
                    if (!Drop(packet.Buffer)) await mappings[active].SendAsync(packet.Buffer, host, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        private async Task BackAsync(int index)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var packet = await mappings[index].ReceiveAsync(lifetime.Token);
                    if (index != active || client is null || Drop(packet.Buffer)) continue;
                    if (index == 1 && packet.Buffer[4] is 1 or 2 or PeerUdpSession.Challenge) ReboundReply.TrySetResult();
                    await front.SendAsync(packet.Buffer, client, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        private static UdpClient Bind()
        {
            var udp = new UdpClient(AddressFamily.InterNetworkV6);
            udp.Client.DualMode = true; udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            return udp;
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); await Task.WhenAll(pumps);
            front.Dispose(); foreach (var mapping in mappings) mapping.Dispose(); blackhole.Dispose(); lifetime.Dispose();
        }
    }
}
