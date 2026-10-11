using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using LazyForza.EstatePeer.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.EstatePeer.Tests;

[TestClass]
public sealed class PeerConnectivityTests
{
    [TestMethod]
    public void DiagnosticsAreBoundedAndNeverSerializeEndpointOrExceptionText()
    {
        var attempt = Guid.NewGuid();
        for (var i = 0; i < 620; i++) PeerDiagnostics.Record(PeerStage.Tcp, PeerOutcome.Timeout, attempt, IPAddress.Parse("203.0.113.27"), i);
        var snapshot = PeerDiagnostics.Snapshot();
        Assert.AreEqual(500, snapshot.Events.Count);
        Assert.IsTrue(snapshot.Dropped >= 120);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.IsFalse(json.Contains("203.0.113.27", StringComparison.Ordinal));
        Assert.IsTrue(json.Length < 256 * 1024);
        Assert.AreEqual(PeerOutcome.Cancelled, PeerDiagnostics.Failure(new OperationCanceledException("secret"), true));
        Assert.AreEqual(PeerOutcome.Rejected, PeerDiagnostics.Failure(new System.Security.Authentication.AuthenticationException("secret")));
    }

    [TestMethod]
    public void GatewayRepliesRequireTheActualProtocolPortAndNonce()
    {
        var nonce = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var reply = new byte[60]; reply[0] = 2; reply[1] = 129;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 120);
        nonce.CopyTo(reply, 24); reply[36] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(40), 24878);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(42), 40000);
        IPAddress.Parse("203.0.113.1").MapToIPv6().GetAddressBytes().CopyTo(reply, 44);
        Assert.IsTrue(GatewayMapping.TryReadMapping(reply, true, true, 24878, nonce, null, out var endpoint, out var lease));
        Assert.AreEqual(40000, endpoint!.Port); Assert.AreEqual(120u, lease);
        Assert.IsFalse(GatewayMapping.TryReadMapping(reply, true, false, 24878, nonce, null, out _, out _));
        reply[24] ^= 1;
        Assert.IsFalse(GatewayMapping.TryReadMapping(reply, true, true, 24878, nonce, null, out _, out _));
        var pmp = new byte[16]; pmp[1] = 130;
        BinaryPrimitives.WriteUInt16BigEndian(pmp.AsSpan(8), 24878);
        BinaryPrimitives.WriteUInt16BigEndian(pmp.AsSpan(10), 45000);
        BinaryPrimitives.WriteUInt32BigEndian(pmp.AsSpan(12), 120);
        Assert.IsTrue(GatewayMapping.TryReadMapping(pmp, false, true, 24878, [], IPAddress.Parse("198.51.100.1"), out endpoint, out _));
        Assert.AreEqual(45000, endpoint!.Port);
        Assert.IsFalse(GatewayMapping.TryReadMapping(pmp, false, true, 24879, [], IPAddress.Parse("198.51.100.1"), out _, out _));
    }

    [TestMethod]
    public async Task MappingLifecycleRenewsAndReleasesOnlyOwnedFiniteLeases()
    {
        var tcp = new FakeMapping(40000); var udp = new FakeMapping(40001);
        var gateway = new PeerGateway(IPAddress.Parse("192.0.2.2"), IPAddress.Parse("192.0.2.1"));
        await using (var manager = new PeerPortMapping(24878, true, true, () => [gateway],
            (_, _, isTcp, _) => Task.FromResult<IPeerMapping?>(isTcp ? tcp : udp)))
        {
            Assert.AreEqual(2, manager.Endpoints.Count);
            Assert.AreEqual(1, tcp.Renewals);
            Assert.IsTrue(manager.Endpoints.All(e => e.Port != 24878));
        }
        Assert.IsTrue(tcp.Disposed); Assert.IsTrue(udp.Disposed);
        var calls = 0;
        await using var denied = new PeerPortMapping(24878, true, true, () => [gateway], (_, _, _, _) =>
        { calls++; throw new MappingDeniedException(); });
        Assert.AreEqual(1, calls, "Explicit denial must not cause probing the second protocol.");
        Assert.AreEqual(0, denied.Endpoints.Count);
    }

    [TestMethod]
    public void GatewayControlUrlsCannotEscapeTheSelectedGateway()
    {
        var gateway = IPAddress.Parse("192.0.2.1");
        Assert.IsTrue(PeerUpnpMapping.Allowed(new Uri("http://192.0.2.1:5000/control"), gateway));
        foreach (var url in new[] { "https://192.0.2.1/control", "http://127.0.0.1/control", "http://example.com/control", "http://secret@192.0.2.1/control" })
            Assert.IsFalse(PeerUpnpMapping.Allowed(new Uri(url), gateway));
    }

    [TestMethod]
    public void MappingCandidatesPreserveLocalAddressesAndTheCodeBudget()
    {
        var local = Enumerable.Range(1, 8).Select(i => new PeerEndpoint(IPAddress.Parse($"192.0.2.{i}"), 24878)).ToArray();
        var mapped = new[] { new PeerEndpoint(IPAddress.Parse("203.0.113.1"), 40000), new PeerEndpoint(IPAddress.Parse("203.0.113.1"), 40001) };
        var merged = PeerCandidateMonitor.Merge(local, mapped);
        Assert.AreEqual(8, merged.Count);
        Assert.IsTrue(mapped.All(merged.Contains)); Assert.IsTrue(local.Take(2).All(merged.Contains));
        var invitation = PeerPathTests.Assisted(PeerTests.Invitation());
        var receipt = PeerReceipt.Create(invitation, merged, PeerReceiptTransport.Udp | PeerReceiptTransport.ReverseTcp);
        CollectionAssert.AreEqual(merged.ToArray(), PeerReceipt.Parse(receipt.Encode(), invitation).Candidates.ToArray());
    }

    [TestMethod]
    public async Task ReverseListenerUsesLocalPortInsteadOfMappedCandidatePort()
    {
        using var reservation = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var local = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        var invitation = PeerPathTests.Assisted(PeerTests.Invitation());
        var receipt = PeerReceipt.Create(invitation, [new(IPAddress.Parse("203.0.113.1"), 1)]);
        await using var join = new PeerReverseJoin(invitation, receipt, local);
        Assert.AreEqual(local, join.LocalPort);
        Assert.AreEqual(1, join.Receipt.Candidates[0].Port);
    }

    [TestMethod]
    public void ValidatedPathSwitchesOnlyAfterSustainedLatencyAdvantage()
    {
        long now = 0;
        var a = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 24878);
        var b = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 24878);
        var receipt = PeerReceipt.Create(PeerPathTests.Assisted(PeerTests.Invitation()), [new(IPAddress.Parse("198.51.100.1"), 32000)]);
        var client = new PeerUdpSession(receipt, [new(a.Address, a.Port), new(b.Address, b.Port)], false, () => now);
        var host = new PeerUdpSession(receipt, receipt.Candidates, true, () => now);
        // Establish the slower route first; fresh responses on it prevent failure-based switching.
        for (var round = 0; round < 19; round++)
        {
            now = round * 1000;
            var probes = client.Probe().Where(p => p.Bytes[4] == PeerUdpSession.Challenge).ToArray();
            foreach (var probe in probes.OrderBy(p => p.Remote.Equals(a) ? 0 : 1))
            {
                var began = now;
                now = round * 1000 + (probe.Remote.Equals(a) ? 100 : 110);
                // b has a fresh 110 ms sample; a's original probe gets 100 ms. Resetting below tests larger improvement next.
                var token = probe.Bytes.AsSpan(PeerUdpSession.HeaderBytes, 16).ToArray();
                client.Receive(host.Encode(PeerUdpSession.Response, token), probe.Remote, out _);
                now = began;
            }
        }
        Assert.AreEqual(a, client.Selected, "Small variation must not change paths.");
        for (var round = 19; round < 35; round++)
        {
            now = round * 1000;
            var probes = client.Probe().Where(p => p.Bytes[4] == PeerUdpSession.Challenge).ToArray();
            foreach (var probe in probes.OrderBy(p => p.Remote.Equals(b) ? 0 : 1))
            {
                now = round * 1000 + (probe.Remote.Equals(b) ? 5 : 100);
                var token = probe.Bytes.AsSpan(PeerUdpSession.HeaderBytes, 16).ToArray();
                client.Receive(host.Encode(PeerUdpSession.Response, token), probe.Remote, out _);
            }
        }
        Assert.AreEqual(b, client.Selected);
    }

    [TestMethod]
    public async Task CandidateRefreshPreservesInvitationGenerationAndDiagnosticFramesFitPipe()
    {
        using var files = new PeerTests.TestDirectory();
        await using var room = new PeerRoom(PeerTests.Settings(files.Path));
        await room.StartAsync(default);
        var generation = room.Invitation.Generation;
        var reply = room.Control(new("connectivity", "0"));
        Assert.IsTrue(reply.Success); Assert.AreEqual(1, reply.ConnectivityVersion);
        Assert.AreEqual(generation, PeerInvitation.Parse(reply.Invitation!).Generation);
        using var stream = new MemoryStream();
        await PeerPipe.WriteAsync(stream, reply, default);
        Assert.IsTrue(stream.Length < PeerPipe.MaximumFrameBytes);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task GatewayWireLifecycleIgnoresUnmatchedRepliesAndRevokesUnexpectedLeases(bool pcp, bool longLease)
    {
        using var server = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var controlPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var lifetimes = new List<uint>();
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                var packet = await server.ReceiveAsync(timeout.Token);
                var request = packet.Buffer;
                if (!pcp && request[1] == 0)
                {
                    var address = new byte[12]; address[1] = 128;
                    IPAddress.Parse("203.0.113.1").GetAddressBytes().CopyTo(address, 8);
                    await server.SendAsync(address, packet.RemoteEndPoint, timeout.Token);
                    continue;
                }
                var life = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(pcp ? 4 : 8));
                lifetimes.Add(life);
                var reply = new byte[pcp ? 60 : 16];
                reply[0] = pcp ? (byte)2 : (byte)0; reply[1] = pcp ? (byte)129 : (byte)130;
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(pcp ? 4 : 12), life == 0 ? 0u : longLease ? 7200u : 120u);
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(pcp ? 8 : 4), lifetimes.Count == 1 ? 100u : 1u); // Router restarted.
                if (pcp)
                {
                    request.AsSpan(24, 12).CopyTo(reply.AsSpan(24)); reply[36] = 6;
                    IPAddress.Parse("203.0.113.1").MapToIPv6().GetAddressBytes().CopyTo(reply, 44);
                }
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(pcp ? 40 : 8), 24878);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(pcp ? 42 : 10), (ushort)(40000 + lifetimes.Count));
                var unrelated = (byte[])reply.Clone(); unrelated[pcp ? 24 : 8] ^= 1;
                await server.SendAsync(unrelated, packet.RemoteEndPoint, timeout.Token);
                await server.SendAsync(reply, packet.RemoteEndPoint, timeout.Token);
                if (life == 0) return;
            }
        });
        var mapping = new GatewayMapping(new(IPAddress.Loopback, IPAddress.Loopback), 24878, true, pcp, controlPort);
        try
        {
            if (longLease) await Assert.ThrowsExactlyAsync<IOException>(() => mapping.RenewAsync(timeout.Token));
            else
            {
                await mapping.RenewAsync(timeout.Token);
                Assert.AreEqual(40001, mapping.Endpoint.Port);
                await mapping.RenewAsync(timeout.Token);
                Assert.AreEqual(40002, mapping.Endpoint.Port);
            }
        }
        finally { await mapping.DisposeAsync(); }
        await serving;
        CollectionAssert.AreEqual(longLease ? new uint[] { 120, 0 } : new uint[] { 120, 120, 0 }, lifetimes.ToArray());
    }

    [TestMethod]
    public void CandidateUpdatesRequireCapabilitiesAndCannotRollBackToAnOlderSequence()
    {
        long now = 0;
        var hostAddress = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 24878);
        var clientAddress = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 32000);
        var receipt = PeerReceipt.Create(PeerPathTests.Assisted(PeerTests.Invitation()), [new(clientAddress.Address, clientAddress.Port)]);
        var client = new PeerUdpSession(receipt, [new(hostAddress.Address, hostAddress.Port)], false, () => now);
        var host = new PeerUdpSession(receipt, receipt.Candidates, true, () => now);
        void Deliver(IEnumerable<(IPEndPoint Remote, byte[] Bytes)> packets, bool fromHost)
        {
            var queue = new Queue<(byte[] Packet, bool FromHost)>(packets.Select(p => (p.Bytes, fromHost)));
            var budget = 64;
            while (queue.TryDequeue(out var item))
            {
                Assert.IsTrue(--budget > 0);
                (item.FromHost ? client : host).Receive(item.Packet, item.FromHost ? hostAddress : clientAddress, out var replies);
                foreach (var reply in replies) queue.Enqueue((reply.Bytes, !item.FromHost));
            }
        }
        Deliver(client.Probe(), false);
        Assert.AreEqual(hostAddress, client.Selected);
        Assert.AreEqual(clientAddress, host.Selected);
        var older = new PeerEndpoint(IPAddress.Parse("198.51.100.10"), 41000);
        var newer = new PeerEndpoint(IPAddress.Parse("198.51.100.11"), 42000);
        client.SetLocalCandidates([older]); client.MarkConnected();
        now = 1000;
        var beforeCapability = client.Probe();
        Assert.IsFalse(beforeCapability.Any(p => p.Bytes[4] == 8));
        Deliver(beforeCapability, false);
        host.MarkConnected(); Deliver(host.Probe(), true);
        now = 2000;
        var oldUpdate = client.Probe().Single(p => p.Bytes[4] == 8);
        client.SetLocalCandidates([newer]);
        now = 12000;
        // Refresh the validated path before exchanging further control messages.
        Deliver(client.Probe().Where(p => p.Bytes[4] != 8), false);
        now = 13000;
        var newUpdate = client.Probe().Single(p => p.Bytes[4] == 8);
        Deliver([newUpdate], false);
        Deliver([oldUpdate], false); // Valid HMAC but an older update inside the replay window.
        now = 18000;
        var probes = host.Probe();
        Assert.IsTrue(probes.Any(p => p.Remote.Equals(new IPEndPoint(newer.Address, newer.Port))));
        Assert.IsFalse(probes.Any(p => p.Remote.Equals(new IPEndPoint(older.Address, older.Port))));
        Assert.AreEqual(clientAddress, host.Selected, "New candidates are not trusted paths until challenged.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task UpnpReservesRenewsAndDeletesOnlyItsOwnFiniteMapping(bool changedOwner, bool permanent)
    {
        using var server = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/control");
        string owner = "";
        var actions = new List<string>();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await server.AcceptTcpClientAsync(timeout.Token);
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                    var body = new char[length];
                    Assert.AreEqual(length, await reader.ReadBlockAsync(body, timeout.Token));
                    var xml = System.Xml.Linq.XDocument.Parse(new string(body));
                    var call = xml.Descendants().Single(e => e.Name.LocalName == "Body").Elements().Single();
                    var action = call.Name.LocalName; actions.Add(action);
                    string? Value(string name) => call.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
                    var values = "";
                    switch (action)
                    {
                        case "GetExternalIPAddress": values = "<NewExternalIPAddress>203.0.113.1</NewExternalIPAddress>"; break;
                        case "AddAnyPortMapping":
                            Assert.AreEqual("120", Value("NewLeaseDuration"));
                            owner = Value("NewPortMappingDescription")!;
                            values = "<NewReservedPort>40000</NewReservedPort>"; break;
                        case "AddPortMapping":
                            Assert.AreEqual("40000", Value("NewExternalPort"));
                            Assert.AreEqual(owner, Value("NewPortMappingDescription")); break;
                        case "GetSpecificPortMappingEntry":
                            values = $"<NewPortMappingDescription>{owner}</NewPortMappingDescription><NewInternalClient>127.0.0.1</NewInternalClient><NewInternalPort>24878</NewInternalPort><NewLeaseDuration>{(permanent ? 0 : 120)}</NewLeaseDuration>"; break;
                        case "DeletePortMapping": Assert.AreEqual("40000", Value("NewExternalPort")); break;
                        default: Assert.Fail(action); break;
                    }
                    var payload = System.Text.Encoding.UTF8.GetBytes($"<Envelope><Body><{action}Response>{values}</{action}Response></Body></Envelope>");
                    var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(payload, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        var mapping = new PeerUpnpMapping(new(IPAddress.Loopback, IPAddress.Loopback), uri, 24878, true);
        try
        {
            if (permanent) await Assert.ThrowsExactlyAsync<IOException>(() => mapping.RenewAsync(timeout.Token));
            else
            {
                await mapping.RenewAsync(timeout.Token);
                Assert.AreEqual(40000, mapping.Endpoint.Port);
                await mapping.RenewAsync(timeout.Token);
                if (changedOwner) owner = "another-application";
            }
        }
        finally
        {
            await mapping.DisposeAsync();
            await timeout.CancelAsync();
            await serving;
        }
        Assert.AreEqual(1, actions.Count(a => a == "AddAnyPortMapping"));
        Assert.AreEqual(permanent ? 0 : 1, actions.Count(a => a == "AddPortMapping"));
        Assert.AreEqual(changedOwner ? 0 : 1, actions.Count(a => a == "DeletePortMapping"));
    }

    private sealed class FakeMapping(int port) : IPeerMapping
    {
        public PeerEndpoint Endpoint => new(IPAddress.Parse("203.0.113.1"), port);
        public DateTimeOffset ExpiresAt { get; private set; } = DateTimeOffset.MinValue;
        public int Renewals { get; private set; }
        public bool Disposed { get; private set; }
        public Task RenewAsync(CancellationToken token) { Renewals++; ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(120); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
