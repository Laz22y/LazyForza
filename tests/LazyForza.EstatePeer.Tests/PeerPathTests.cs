using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LazyForza.EstatePeer.Host;
using LazyForza.RaceServer.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.EstatePeer.Tests;

[TestClass]
public sealed class PeerPathTests
{
    [TestMethod]
    public void CandidateBudgetPreservesInterfacesAndAddressFamilies()
    {
        var candidates = Enumerable.Range(1, 20).Select(i => new PeerAddresses.Candidate("first",
            new(IPAddress.Parse($"2001:db8::{i:x}"), 24878), true)).ToList();
        candidates.Add(new("first", new(IPAddress.Parse("192.0.2.1"), 24878), true));
        candidates.Add(new("second", new(IPAddress.Parse("2001:db8:1::1"), 24878), true));
        candidates.Add(new("static-lan", new(IPAddress.Parse("10.0.0.2"), 24878), false));
        var manual = new PeerEndpoint(IPAddress.Parse("203.0.113.1"), 34567);
        var selected = PeerAddresses.Select(candidates, manual);
        Assert.AreEqual(8, selected.Count);
        Assert.AreEqual(manual, selected[0]);
        Assert.IsTrue(candidates.TakeLast(3).All(item => selected.Contains(item.Endpoint)));
        Assert.AreEqual(selected.Count, selected.Distinct().Count());
    }

    [TestMethod]
    public void NewCodesKeepFullIdentityWithoutGrowingTheInvitationAndReadLegacyCodes()
    {
        var old = PeerTests.Invitation();
        var next = Assisted(old);
        Assert.AreEqual(old.Encode().Length, next.Encode().Length);
        var decoded = PeerInvitation.Parse(next.Encode());
        Assert.IsTrue(decoded.SupportsAssistedConnection);
        Assert.AreEqual(old.PublicKeySha256, decoded.PublicKeySha256);
        Assert.IsFalse(PeerInvitation.Parse(old.Encode()).SupportsAssistedConnection);
        var receipt = PeerReceipt.Create(decoded, old.Candidates, PeerReceiptTransport.Udp | PeerReceiptTransport.ReverseTcp);
        var parsed = PeerReceipt.Parse(receipt.Encode(), decoded);
        Assert.IsTrue(parsed.Authenticated);
        Assert.AreEqual(receipt.Transports, parsed.Transports);
        Assert.AreEqual(receipt.Nonce, parsed.Nonce);
        Assert.ThrowsExactly<InvalidDataException>(() => PeerReceipt.Parse(receipt.Encode(), old));
        var legacyReceipt = PeerReceipt.Create(old, old.Candidates);
        Assert.IsFalse(PeerReceipt.Parse(legacyReceipt.Encode(), decoded).Authenticated);
    }

    [TestMethod]
    public void AuthenticatedDiscoveryLearnsMappedEndpointsAndRejectsTamperingReplayAndReflection()
    {
        long now = 0;
        var receipt = PeerReceipt.Create(Assisted(PeerTests.Invitation()), [new(IPAddress.Parse("10.0.0.2"), 10)]);
        var hostAddress = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 24878);
        var mappedClient = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 45000);
        var client = new PeerUdpSession(receipt, [new(hostAddress.Address, hostAddress.Port)], false, () => now);
        var host = new PeerUdpSession(receipt, receipt.Candidates, true, () => now);
        var ready = client.BeginAttempt();
        var probe = client.Probe().First().Bytes;
        var changed = probe.ToArray(); changed[^1] ^= 1;
        Assert.IsNull(host.Receive(changed, mappedClient, out var rejected));
        Assert.AreEqual(0, rejected.Count);
        Assert.IsNull(client.Receive(probe, hostAddress, out rejected), "Reflected local messages must not authenticate as the peer.");
        Assert.AreEqual(0, rejected.Count);
        Pump(client, host, hostAddress, mappedClient, [(hostAddress, probe)]);
        Assert.IsTrue(ready.IsCompletedSuccessfully);
        Assert.AreEqual(mappedClient, host.Selected, "The private receipt address is not the actual source after NAT.");
        var data = client.Encode(1, [1, 2, 3]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, host.Receive(data, mappedClient, out _)!.Value.Payload);
        Assert.IsNull(host.Receive(data, mappedClient, out _), "Authenticated packets still require replay protection.");
        var fresh = client.BeginAttempt();
        Assert.IsFalse(fresh.IsCompleted, "A past successful check cannot satisfy a new attempt.");
        now = 500;
        var rebound = new IPEndPoint(mappedClient.Address, mappedClient.Port + 1);
        Pump(client, host, hostAddress, rebound, client.Probe());
        Assert.IsTrue(fresh.IsCompletedSuccessfully);
        Assert.IsTrue(host.Probe().Any(item => item.Remote.Equals(rebound)), "Keep checking learned paths while the old selected path is still fresh.");
        now = 4001;
        Pump(client, host, hostAddress, rebound, host.Probe(), fromHost: true);
        Assert.AreEqual(rebound, host.Selected);
    }

    [TestMethod]
    public void LargerFragmentsRequireAuthenticatedRoundTripOfLargePackets()
    {
        long now = 0;
        var receipt = PeerReceipt.Create(Assisted(PeerTests.Invitation()), [new(IPAddress.Parse("198.51.100.2"), 42000)]);
        var hostAddress = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 24878);
        var clientAddress = new IPEndPoint(receipt.Candidates[0].Address, receipt.Candidates[0].Port);
        var client = new PeerUdpSession(receipt, [new(hostAddress.Address, hostAddress.Port)], false, () => now);
        var host = new PeerUdpSession(receipt, receipt.Candidates, true, () => now);
        Pump(client, host, hostAddress, clientAddress, client.Probe());
        Pump(client, host, hostAddress, clientAddress, client.Probe(), limit: 700);
        Assert.AreEqual(512, client.FragmentBytes);
        client.BeginAttempt();
        Pump(client, host, hostAddress, clientAddress, client.Probe());
        Assert.AreEqual(1150, client.FragmentBytes);
        now = 10_001;
        Pump(client, host, hostAddress, clientAddress, client.Probe(), limit: 700);
        now += 3001;
        Pump(client, host, hostAddress, clientAddress, client.Probe(), limit: 700);
        Assert.AreEqual(512, client.FragmentBytes, "A path that stops carrying large packets must return to small fragments without reconnecting.");
    }

    [TestMethod]
    public void DelayedUdpResponseRemainsValidAfterAProbeRetry()
    {
        long now = 0;
        var receipt = PeerReceipt.Create(Assisted(PeerTests.Invitation()), [new(IPAddress.Parse("198.51.100.2"), 42000)]);
        var hostAddress = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 24878);
        var clientAddress = new IPEndPoint(receipt.Candidates[0].Address, receipt.Candidates[0].Port);
        var client = new PeerUdpSession(receipt, [new(hostAddress.Address, hostAddress.Port)], false, () => now);
        var host = new PeerUdpSession(receipt, receipt.Candidates, true, () => now);
        var ready = client.BeginAttempt();
        var first = client.Probe();
        now = 1500;
        _ = client.Probe();
        Pump(client, host, hostAddress, clientAddress, first);
        Assert.IsTrue(ready.IsCompletedSuccessfully, "A delayed response must still satisfy the original challenge after a retry.");
    }

    [TestMethod]
    public async Task ReverseHandshakeRejectsAProofReplayedFromAnEarlierConnection()
    {
        var receipt = PeerReceipt.Create(Assisted(PeerTests.Invitation()), [new(IPAddress.Parse("192.0.2.1"), 42000)], PeerReceiptTransport.ReverseTcp);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var challenge = new byte[64];
        RandomNumberGenerator.Fill(challenge.AsSpan(32));
        byte[]? previousProof = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var authenticating = PeerReverseHandshake.AuthenticateAsync(client.GetStream(), receipt, host: true, timeout.Token);
            var stream = server.GetStream();
            await stream.ReadExactlyAsync(new byte[16], timeout.Token);
            await stream.ReadExactlyAsync(challenge.AsMemory(0, 32), timeout.Token);
            await stream.WriteAsync(challenge.AsMemory(32), timeout.Token);
            await stream.ReadExactlyAsync(new byte[32], timeout.Token);
            var proof = previousProof ?? HMACSHA256.HashData(PeerPathAuthentication.Key(receipt, "reverse-join"), challenge);
            await stream.WriteAsync(proof, timeout.Token);
            if (attempt == 0) { await authenticating; previousProof = proof; }
            else await Assert.ThrowsExactlyAsync<IOException>(() => authenticating);
        }
    }

    [TestMethod]
    public async Task ReverseTcpCarriesPinnedTrackAndRaceMessagesWithoutQuic()
    {
        using var files = new PeerTests.TestDirectory();
        var settings = PeerTests.Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        await using var join = new PeerReverseJoin(room.Invitation);
        Assert.AreEqual(PeerReceiptTransport.ReverseTcp, join.Receipt.Transports);
        Assert.IsTrue(room.Control(new("acceptReceipt", join.Receipt.Encode())).Success);
        Assert.IsFalse(room.Control(new("acceptReceipt", join.Receipt.Encode())).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connection = await join.ConnectAsync(timeout.Token);
        Assert.AreNotEqual(room.Port, connection.Origin.Port);
        using var http = connection.CreateHttpClient(settings.Password);
        CollectionAssert.AreEqual(File.ReadAllBytes(settings.TrackPackagePath), await http.GetByteArrayAsync("peer/track", timeout.Token));
        using var socket = await PeerTests.Connect(connection);
        await PeerTests.Send(socket, RaceMessageTypes.Login, PeerTests.Login(settings, "reverse driver"));
        var accepted = RaceProtocolJson.DeserializePayload<RaceLoginAccepted>(await PeerTests.Receive(socket, RaceMessageTypes.LoginAccepted));
        Assert.IsTrue(room.Coordinator.Snapshot().Participants.Any(item => item.Id == accepted.ParticipantId));
        await PeerTests.Send(socket, RaceMessageTypes.Leave, new { });
        _ = await PeerTests.Receive(socket, RaceMessageTypes.Left);
    }

    [TestMethod]
    public async Task OccupiedUdpPortStillAllowsTcpHostingAndReverseConnections()
    {
        using var occupied = new UdpClient(AddressFamily.InterNetworkV6);
        occupied.Client.DualMode = true;
        occupied.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        using var files = new PeerTests.TestDirectory();
        await using var room = new PeerRoom(PeerTests.Settings(files.Path) with { Port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port });
        await room.StartAsync(default);
        Assert.IsFalse(room.Invitation.SupportsUdp);
        using var http = PeerTests.Loopback(room).CreateHttpClient();
        Assert.IsTrue((await http.GetAsync("peer/identity")).IsSuccessStatusCode);
        await using var reverse = new PeerReverseJoin(room.Invitation);
        Assert.IsTrue(room.Control(new("acceptReceipt", reverse.Receipt.Encode())).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(await reverse.ConnectAsync(timeout.Token));
    }

    [TestMethod]
    public async Task ReverseTcpStillRejectsTheWrongHostCertificate()
    {
        using var files = new PeerTests.TestDirectory();
        await using var room = new PeerRoom(PeerTests.Settings(files.Path));
        await room.StartAsync(default);
        await using var join = new PeerReverseJoin(Assisted(room.Invitation, new string('B', 64)));
        Assert.IsTrue(room.Control(new("acceptReceipt", join.Receipt.Encode())).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => join.ConnectAsync(timeout.Token));
        Assert.AreEqual(0, room.Coordinator.Snapshot().Participants.Count);
    }

    private static void Pump(PeerUdpSession client, PeerUdpSession host, IPEndPoint hostAddress, IPEndPoint clientAddress,
        IReadOnlyList<(IPEndPoint Remote, byte[] Bytes)> messages, int limit = int.MaxValue, bool fromHost = false)
    {
        var queue = new Queue<(bool ToHost, IPEndPoint Destination, byte[] Bytes)>(messages.Select(item => (!fromHost, item.Remote, item.Bytes)));
        var count = 0;
        while (queue.TryDequeue(out var item))
        {
            Assert.IsTrue(++count < 100, "Discovery must not form a reply loop.");
            if (item.Bytes.Length > limit || !item.Destination.Equals(item.ToHost ? hostAddress : clientAddress)) continue;
            var receiver = item.ToHost ? host : client;
            receiver.Receive(item.Bytes, item.ToHost ? clientAddress : hostAddress, out var replies);
            foreach (var reply in replies) queue.Enqueue((!item.ToHost, reply.Remote, reply.Bytes));
        }
    }

    internal static PeerInvitation Assisted(PeerInvitation source, string? pin = null) => new()
    {
        RoomId = source.RoomId, Generation = source.Generation, ExpiresAt = source.ExpiresAt,
        PublicKeySha256 = pin ?? source.PublicKeySha256, Candidates = source.Candidates,
        SupportsUdp = source.SupportsUdp, SupportsAssistedConnection = true
    };
}
