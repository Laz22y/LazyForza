using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using LazyForza.EstatePeer.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.EstatePeer.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class PeerRetryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedHandshakeCanRetryWithTheSameReceiptAndPublicPort(bool waitForHandshakeTimeout)
    {
        if (!OperatingSystem.IsWindows() || !PeerQuicHost.IsSupported) { Assert.Inconclusive("QUIC unavailable."); return; }
        using var files = new PeerTests.TestDirectory();
        var settings = PeerTests.Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        await using var relay = new LossyPath(room.Port);
        var invitation = new PeerInvitation
        {
            RoomId = room.Invitation.RoomId, Generation = room.Invitation.Generation,
            ExpiresAt = room.Invitation.ExpiresAt, PublicKeySha256 = room.Invitation.PublicKeySha256,
            SupportsUdp = true, Candidates = [relay.Endpoint]
        };
        await using var pending = new PeerQuicJoin(invitation);
        // The fixture forwards opaque datagrams solely to model a path that initially drops
        // handshake traffic. Production still connects the two clients directly.
        var hostReceipt = pending.Receipt with { Candidates = [relay.Endpoint] };
        Assert.IsTrue(room.Control(new("acceptReceipt", hostReceipt.Encode())).Success);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var firstAttempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var failed = pending.ConnectAsync(firstAttempt.Token);
        await relay.HandshakeSeen.Task.WaitAsync(deadline.Token);
        if (waitForHandshakeTimeout)
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => failed);
            StringAssert.Contains(error.Message, "加密握手超时");
        }
        else
        {
            await firstAttempt.CancelAsync();
            try { await failed; Assert.Fail("The dropped handshake must not connect."); }
            catch (OperationCanceledException) { }
        }
        var receipt = pending.Receipt.Encode();
        relay.DropHandshake = false;
        var connection = await pending.ConnectAsync(deadline.Token);
        Assert.AreEqual(receipt, pending.Receipt.Encode(), "Retry must keep the receipt's public UDP socket alive.");
        using var http = connection.CreateHttpClient(settings.Password);
        CollectionAssert.AreEqual(File.ReadAllBytes(settings.TrackPackagePath), await http.GetByteArrayAsync("peer/track", deadline.Token));
    }

    [TestMethod]
    public async Task MissingAdmissionReportsUdpPathFailureAndCanRetryAfterHostAdmits()
    {
        if (!OperatingSystem.IsWindows() || !PeerQuicHost.IsSupported) { Assert.Inconclusive("QUIC unavailable."); return; }
        using var files = new PeerTests.TestDirectory();
        await using var room = new PeerRoom(PeerTests.Settings(files.Path));
        await room.StartAsync(default);
        await using var pending = new PeerQuicJoin(room.Invitation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => pending.ConnectAsync(deadline.Token));
        StringAssert.Contains(error.Message, "未收到房主的 UDP 响应");
        Assert.IsTrue(room.Control(new("acceptReceipt", pending.Receipt.Encode())).Success);
        Assert.IsNotNull(await pending.ConnectAsync(deadline.Token));
    }

    private sealed class LossyPath : IAsyncDisposable
    {
        private readonly UdpClient socket = new(AddressFamily.InterNetworkV6);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task pump;
        private readonly int hostPort;
        private IPEndPoint? client;
        internal volatile bool DropHandshake = true;
        internal TaskCompletionSource HandshakeSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PeerEndpoint Endpoint { get; }

        internal LossyPath(int hostPort)
        {
            this.hostPort = hostPort;
            socket.Client.DualMode = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            Endpoint = PeerAddresses.Collect(((IPEndPoint)socket.Client.LocalEndPoint!).Port).First();
            pump = PumpAsync();
        }

        private async Task PumpAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(lifetime.Token);
                    var fromHost = packet.RemoteEndPoint.Port == hostPort;
                    if (!fromHost) client = packet.RemoteEndPoint;
                    if (packet.Buffer.Length > 21 && DropHandshake)
                    { HandshakeSeen.TrySetResult(); continue; }
                    var destination = fromHost ? client : new IPEndPoint(Endpoint.Address, hostPort);
                    if (destination is not null) await socket.SendAsync(packet.Buffer, destination, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); await pump; socket.Dispose(); lifetime.Dispose();
        }
    }
}
