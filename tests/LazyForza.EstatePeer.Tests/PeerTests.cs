using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.EstatePeer.Host;
using LazyForza.RaceServer.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]
namespace LazyForza.EstatePeer.Tests;

[TestClass]
public sealed class PeerTests
{
    [TestMethod]
    public async Task FailedProjectAttemptsLeaveNoProjectAndPreserveExistingProjects()
    {
        using var files = new TestDirectory();
        string saved;
        using (var project = new PeerProjectCreation(files.Path))
        { saved = project.Folder; project.Commit("{\"name\":\"keep\"}"); }
        string failed;
        using (var project = new PeerProjectCreation(files.Path))
        {
            failed = project.Folder;
            var settings = Settings(project.Folder);
            using var occupied = new System.Net.Sockets.TcpListener(IPAddress.IPv6Any, 0);
            occupied.Server.DualMode = true; occupied.Start();
            var executable = Environment.GetEnvironmentVariable("LAZYFORZA_PEER_HOST_QA") ?? Path.Combine(AppContext.BaseDirectory, "LazyForza.EstatePeer.Host.exe");
            await Assert.ThrowsExactlyAsync<IOException>(() => PeerHostProcess.StartAsync(executable,
                settings with { Port = ((IPEndPoint)occupied.LocalEndpoint).Port }, default));
            Assert.IsFalse(File.Exists(Path.Combine(project.Folder, "project.json")));
        }
        Assert.IsFalse(Directory.Exists(failed));
        Assert.AreEqual("{\"name\":\"keep\"}", File.ReadAllText(Path.Combine(saved, "project.json")));
    }

    [TestMethod]
    public void CompactInvitationKeepsLegacyCompatibilityAndReceiptsAreRoomBoundAndExpiring()
    {
        var invitation = Invitation();
        using var legacy = new MemoryStream();
        using (var writer = new BinaryWriter(legacy, System.Text.Encoding.UTF8, true))
        {
            writer.Write(invitation.RoomId.ToByteArray()); writer.Write(invitation.Generation);
            writer.Write(invitation.ExpiresAt.ToUnixTimeSeconds()); writer.Write(Convert.FromHexString(invitation.PublicKeySha256));
            writer.Write((byte)invitation.Candidates.Count);
            foreach (var endpoint in invitation.Candidates)
            { var address = endpoint.Address.GetAddressBytes(); writer.Write((byte)address.Length); writer.Write(address); writer.Write((ushort)endpoint.Port); }
        }
        legacy.Write(SHA256.HashData(legacy.ToArray()).AsSpan(0, 8));
        var old = "LFZP1-" + Convert.ToBase64String(legacy.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(invitation.RoomId, PeerInvitation.Parse(old).RoomId);
        Assert.IsTrue(invitation.Encode().Length < old.Length);
        Assert.AreEqual(invitation.PublicKeySha256, PeerInvitation.Parse(invitation.Encode()).PublicKeySha256);
        var receipt = PeerReceipt.Create(invitation, invitation.Candidates);
        Assert.AreEqual(receipt.Nonce, PeerReceipt.Parse(receipt.Encode(), invitation).Nonce);
        Assert.IsTrue(receipt.Encode().Length < invitation.Encode().Length);
        Assert.ThrowsExactly<InvalidDataException>(() => PeerReceipt.Parse(receipt.Encode(), Invitation()));
        Assert.ThrowsExactly<InvalidDataException>(() => PeerReceipt.Parse(receipt.Encode(), invitation, receipt.ExpiresAt.AddSeconds(1)));
    }

    [TestMethod]
    public async Task NativeWebControlSharesAuthorityRequiresLocalSessionAndRejectsCrossOriginActions()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        var entry = new Uri(room.Control(new("openControl")).ControlUrl!);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(entry.GetLeftPart(UriPartial.Authority)) };
        using var unauthorized = await browser.GetAsync("/api/admin/state");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var html = await browser.GetStringAsync("/");
        Assert.IsTrue(html.Contains("id=\"dashboard\"", StringComparison.Ordinal));
        Assert.IsTrue((await browser.GetStringAsync("/app.js")).Contains("/api/admin/event-projects", StringComparison.Ordinal));
        browser.DefaultRequestHeaders.Add("Origin", browser.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var login = await browser.PostAsJsonAsync("/peer-session", new { token = entry.Fragment[1..] });
        Assert.IsTrue(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        using var replay = await browser.PostAsJsonAsync("/peer-session", new { token = entry.Fragment[1..] });
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var state = await browser.GetAsync("/api/admin/state");
        Assert.IsTrue(state.IsSuccessStatusCode);
        using var phase = await browser.PostAsJsonAsync("/api/admin/session", new { phase = "practice", countdownSeconds = 5, startSequenceSeconds = 10 });
        Assert.IsTrue(phase.IsSuccessStatusCode, await phase.Content.ReadAsStringAsync());
        Assert.AreEqual(RaceSessionPhase.Practice, room.Coordinator.Snapshot().Phase);
        browser.DefaultRequestHeaders.Remove("Origin"); browser.DefaultRequestHeaders.Add("Origin", "https://unrelated.invalid");
        using var denied = await browser.PostAsJsonAsync("/api/admin/flag", new { flag = "red" });
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var remote = Loopback(room).CreateHttpClient(settings.Password);
        using var hidden = await remote.GetAsync("/api/admin/state");
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [TestMethod]
    public async Task WrongUdpCertificatePinDoesNotStopSubsequentValidJoins()
    {
        if (!OperatingSystem.IsWindows() || !PeerQuicHost.IsSupported) { Assert.Inconclusive("QUIC unavailable."); return; }
        using var files = new TestDirectory();
        await using var room = new PeerRoom(Settings(files.Path));
        await room.StartAsync(default);
        var wrong = new PeerInvitation { RoomId = room.Invitation.RoomId, Generation = room.Invitation.Generation,
            ExpiresAt = room.Invitation.ExpiresAt, Candidates = room.Invitation.Candidates,
            SupportsUdp = true, PublicKeySha256 = new string('B', 64) };
        await using (var invalid = new PeerQuicJoin(wrong))
        {
            Assert.IsTrue(room.Control(new("acceptReceipt", invalid.Receipt.Encode())).Success);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await invalid.ConnectAsync(timeout.Token); Assert.Fail("The untrusted pin must fail before metadata or credentials are sent."); }
            catch (Exception error) when (error is System.Security.Authentication.AuthenticationException or System.Net.Quic.QuicException) { }
        }
        await using var valid = new PeerQuicJoin(room.Invitation);
        Assert.IsTrue(room.Control(new("acceptReceipt", valid.Receipt.Encode())).Success);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(await valid.ConnectAsync(deadline.Token));
    }

    [TestMethod]
    public async Task UdpReceiptCarriesPinnedMetadataTrackAndReliableWebSocketMessages()
    {
        if (!OperatingSystem.IsWindows() || !PeerQuicHost.IsSupported) { Assert.Inconclusive("Windows QUIC is unavailable."); return; }
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        await using var pending = new PeerQuicJoin(room.Invitation);
        Assert.IsTrue(room.Control(new("acceptReceipt", pending.Receipt.Encode())).Success);
        Assert.IsFalse(room.Control(new("acceptReceipt", pending.Receipt.Encode())).Success, "Receipts cannot allocate duplicate paths.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connection = await pending.ConnectAsync(timeout.Token);
        Assert.AreNotEqual(room.Port, connection.Origin.Port, "HTTPS must enter the UDP tunnel.");
        using var http = connection.CreateHttpClient(settings.Password);
        var bytes = await http.GetByteArrayAsync("peer/track", timeout.Token);
        CollectionAssert.AreEqual(File.ReadAllBytes(settings.TrackPackagePath), bytes);
        using var socket = await Connect(connection);
        await Send(socket, RaceMessageTypes.Login, Login(settings, "UDP driver"));
        var accepted = RaceProtocolJson.DeserializePayload<RaceLoginAccepted>(await Receive(socket, RaceMessageTypes.LoginAccepted));
        Assert.IsTrue(room.Coordinator.Snapshot().Participants.Any(item => item.Id == accepted.ParticipantId));
        await Send(socket, RaceMessageTypes.Leave, new { });
        _ = await Receive(socket, RaceMessageTypes.Left);
    }

    [TestMethod]
    public void InvitationRoundTripsDualStackAndRejectsExpiredOrModifiedCodes()
    {
        var invite = Invitation();
        var encoded = invite.Encode();
        var parsed = PeerInvitation.Parse(encoded);
        Assert.AreEqual(invite.RoomId, parsed.RoomId);
        CollectionAssert.AreEqual(invite.Candidates.ToArray(), parsed.Candidates.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => PeerInvitation.Parse(encoded[..20] + "x" + encoded[21..]));
        Assert.ThrowsExactly<InvalidDataException>(() => PeerInvitation.Parse(encoded, invite.ExpiresAt.AddSeconds(1)));
        Assert.ThrowsExactly<InvalidDataException>(() => PeerInvitation.Parse(new string('x', 2048)));
    }

    [TestMethod]
    public void InvitationCannotTargetLoopbackMulticastMappedOrScopedAddresses()
    {
        foreach (var address in new[] { "127.0.0.1", "::1", "0.0.0.0", "224.0.0.1", "ff02::1", "::ffff:192.168.1.2", "fe80::1%3", "::2" })
            Assert.IsFalse(PeerInvitation.IsAllowedAddress(IPAddress.Parse(address)), address);
        Assert.IsTrue(PeerInvitation.IsAllowedAddress(IPAddress.Parse("fd12::123")));
    }

    [TestMethod]
    public async Task OriginGuardNeverSendsCredentialsToAnUnpinnedOrigin()
    {
        var connection = new PeerConnection(Invitation(), new PeerEndpoint(IPAddress.Loopback, 12345));
        using var client = connection.CreateHttpClient("not-transmitted");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:12345/peer/track"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetAsync("https://127.0.0.1:12346/peer/track"));
    }

    [TestMethod]
    public async Task LocalControlRejectsOversizedFramesBeforeAllocatingPayload()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PeerPipe.ReadAsync<PeerHostCommand>(stream, default));
    }

    [TestMethod]
    public async Task ComponentInstallRejectsTamperingAndPreservesRoomDataOnUninstall()
    {
        using var files = new TestDirectory();
        var archive = Path.Combine(files.Path, "component.zip");
        var bytes = new byte[] { 1, 2, 3, 4 };
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry(PeerComponentStore.ExecutableName).Open()) output.Write(bytes);
        var catalog = new PeerComponentCatalog(1, 1, "test", "win-x64", new FileInfo(archive).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))), null,
            [new(PeerComponentStore.ExecutableName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))]);
        var store = new PeerComponentStore(Path.Combine(files.Path, "components"), catalog);
        var roomData = Path.Combine(files.Path, "room-state.dat");
        File.WriteAllText(roomData, "keep");
        await store.InstallAsync(archive, default);
        Assert.IsNotNull(await store.FindVerifiedExecutableAsync(default));
        File.WriteAllText(Path.Combine(store.InstallDirectory, PeerComponentStore.ExecutableName), "changed");
        Assert.IsNull(await store.FindVerifiedExecutableAsync(default));
        store.Uninstall();
        Assert.AreEqual("keep", File.ReadAllText(roomData));
        File.AppendAllText(archive, "changed");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.InstallAsync(archive, default));
    }

    [TestMethod]
    public async Task ComponentCatalogCannotEscapeItsInstallDirectory()
    {
        using var files = new TestDirectory();
        var catalog = new PeerComponentCatalog(1, 1, "test", "win-x64", 10, new string('A', 64), null,
            [new(PeerComponentStore.ExecutableName, 1, new string('A', 64)), new("../escape", 1, new string('A', 64))]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PeerComponentStore(files.Path, catalog).FindVerifiedExecutableAsync(default));
    }

    [TestMethod]
    public async Task RealTlsRoomRequiresPinAndPasswordAndKeepsObserversReadOnly()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        var connection = Loopback(room);
        using var anonymous = connection.CreateHttpClient();
        using var unauthorized = await anonymous.GetAsync(".well-known/lazyforza-race.json");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var authenticated = connection.CreateHttpClient(settings.Password);
        using var descriptor = await authenticated.GetAsync(".well-known/lazyforza-race.json");
        Assert.IsTrue(descriptor.IsSuccessStatusCode);
        using var wrongPin = new PeerConnection(Invitation(), new PeerEndpoint(IPAddress.Loopback, room.Port)).CreateHttpClient(settings.Password);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => wrongPin.GetAsync("peer/identity"));
        using var driver = await Connect(connection);
        await Send(driver, RaceMessageTypes.Login, Login(settings, "Driver One"));
        var accepted = RaceProtocolJson.DeserializePayload<RaceLoginAccepted>(await Receive(driver, RaceMessageTypes.LoginAccepted));
        Assert.IsFalse(accepted.IsObserver);
        using var observer = await Connect(connection);
        await Send(observer, RaceMessageTypes.Login, Login(settings, "Observer One") with { IsObserver = true });
        Assert.IsTrue(RaceProtocolJson.DeserializePayload<RaceLoginAccepted>(await Receive(observer, RaceMessageTypes.LoginAccepted)).IsObserver);
        await Send(observer, RaceMessageTypes.Ready, new RaceReadyUpdate(true));
        var error = RaceProtocolJson.DeserializePayload<RaceErrorPayload>(await Receive(observer, RaceMessageTypes.Error));
        Assert.AreEqual("observerReadOnly", error.Code);
        await Send(driver, RaceMessageTypes.Leave, new { });
        _ = await Receive(driver, RaceMessageTypes.Left);
        Assert.IsTrue(room.Coordinator.Snapshot().Participants.All(participant => participant.Id != accepted.ParticipantId));
    }

    [TestMethod]
    public async Task RoomRecoveryKeepsIdentityAndFreezesRunningStageUntilHostConfirms()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        Guid identity;
        string pin;
        await using (var room = new PeerRoom(settings))
        {
            await room.StartAsync(default);
            identity = room.Invitation.RoomId;
            pin = room.Invitation.PublicKeySha256;
            Assert.IsTrue(room.Control(new PeerHostCommand("phase", "Practice")).Success);
        }
        await using var restored = new PeerRoom(settings with { Resume = true });
        await restored.StartAsync(default);
        Assert.AreEqual(identity, restored.Invitation.RoomId);
        Assert.AreEqual(pin, restored.Invitation.PublicKeySha256);
        Assert.IsTrue(restored.Coordinator.AwaitingRecoveryConfirmation);
        Assert.AreEqual(RaceSessionPhase.Suspended, restored.Coordinator.Snapshot().Phase);
        Assert.IsTrue(restored.Control(new PeerHostCommand("flag", "Green")).Success);
        Assert.AreEqual(RaceSessionPhase.Practice, restored.Coordinator.Snapshot().Phase);
    }

    [TestMethod]
    public async Task RefreshedInvitationRejectsNewJoinsButKeepsAuthenticatedRecovery()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        var original = Loopback(room);
        using var first = await Connect(original);
        await Send(first, RaceMessageTypes.Login, Login(settings, "Returning driver"));
        var accepted = RaceProtocolJson.DeserializePayload<RaceLoginAccepted>(await Receive(first, RaceMessageTypes.LoginAccepted));
        Assert.IsTrue(room.Control(new("refreshInvitation")).Success);
        using var probe = original.CreateHttpClient();
        using var response = await probe.GetAsync("peer/identity");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using var resumed = await Connect(original);
        await Send(resumed, RaceMessageTypes.Login, Login(settings, "Returning driver") with { ResumeToken = accepted.ResumeToken });
        var recovered = RaceProtocolJson.DeserializePayload<RaceLoginAccepted>(await Receive(resumed, RaceMessageTypes.LoginAccepted));
        Assert.AreEqual(accepted.ParticipantId, recovered.ParticipantId);
        Assert.AreEqual(1, room.Coordinator.Snapshot().Participants.Count);
    }

    [TestMethod]
    public async Task SameNetworkCanJoinTwelveDriversAndTwelveObserversWithoutBeingRateLimited()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        var sockets = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 24; i++)
            {
                var socket = await Connect(Loopback(room)); sockets.Add(socket);
                await Send(socket, RaceMessageTypes.Login, Login(settings, "Member " + i) with { IsObserver = i >= 12 });
                _ = await Receive(socket, RaceMessageTypes.LoginAccepted);
            }
            Assert.AreEqual(12, room.Coordinator.Snapshot().Participants.Count);
            var snapshot = await Receive(sockets[^1], RaceMessageTypes.Snapshot);
            Assert.IsTrue(snapshot.Payload.GetRawText().Length < RaceProtocol.MaximumMessageBytes);
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    [TestMethod]
    public async Task OwnedHelperStopsOnPipeClosureAndCanResumeTheSameRoom()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        var executable = Environment.GetEnvironmentVariable("LAZYFORZA_PEER_HOST_QA") ?? Path.Combine(AppContext.BaseDirectory, "LazyForza.EstatePeer.Host.exe");
        Guid identity;
        await using (var process = await PeerHostProcess.StartAsync(executable, settings, default))
        {
            identity = process.Invitation.RoomId;
            Assert.IsTrue(process.IsRunning);
            var control = await process.CommandAsync(new("openControl"), default);
            Assert.IsTrue(control.Success);
            Assert.AreEqual("127.0.0.1", new Uri(control.ControlUrl!).Host);
            Assert.IsTrue((await process.CommandAsync(new("phase", "Practice"), default)).Success);
            Assert.IsTrue((await process.CommandAsync(new("refreshInvitation"), default)).Success);
            Assert.AreEqual(2, process.Invitation.Generation);
        }
        await using var restored = await PeerHostProcess.StartAsync(executable, settings with { Resume = true }, default);
        Assert.AreEqual(identity, restored.Invitation.RoomId);
        Assert.IsTrue((await restored.CommandAsync(new("flag", "Green"), default)).Success);
    }

    [TestMethod]
    public async Task DuplicateLapCommandsReceiveAcknowledgementsWithoutCountingTwice()
    {
        using var files = new TestDirectory();
        var settings = Settings(files.Path);
        await using var room = new PeerRoom(settings);
        await room.StartAsync(default);
        using var socket = await Connect(Loopback(room));
        await Send(socket, RaceMessageTypes.Login, Login(settings, "Lap driver"));
        _ = await Receive(socket, RaceMessageTypes.LoginAccepted);
        Assert.IsTrue(room.Control(new("phase", "Practice")).Success);
        var lap = new RaceLapCompleted(Guid.NewGuid(), 1, 80, [25, 25, 30], true, null, 80000);
        await Send(socket, RaceMessageTypes.LapCompleted, lap);
        var first = RaceProtocolJson.DeserializePayload<RaceLapAcknowledgement>(await Receive(socket, RaceMessageTypes.LapAcknowledged));
        await Send(socket, RaceMessageTypes.LapCompleted, lap);
        var second = RaceProtocolJson.DeserializePayload<RaceLapAcknowledgement>(await Receive(socket, RaceMessageTypes.LapAcknowledged));
        Assert.AreEqual(first, second);
        Assert.IsTrue(room.Coordinator.Snapshot().Participants.Single().CompletedLaps <= 1);
    }

    internal static PeerHostStart Settings(string root)
    {
        var track = Path.Combine(root, "track.lfzestate");
        var id = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        { track = new { id, name = "Test track" }, sectors = Array.Empty<object>(), definition = new { trackId = id, mapRevision = "v1" } });
        using (var archive = ZipFile.Open(track, ZipArchiveMode.Create))
        {
            using (var manifest = archive.CreateEntry("manifest.json").Open())
                JsonSerializer.Serialize(manifest, new
                {
                    format = "lazyforza-estate-track", formatVersion = 1, trackId = id, trackName = "Test track", mapRevision = "v1",
                    payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)), trackFingerprintSha256 = new string('A', 64)
                });
            using var data = archive.CreateEntry("track.json").Open(); data.Write(payload);
        }
        return new(root, "Peer test", "test-password", 0, "192.0.2.10", null,
            id.ToString("D"), "Test track", "v1", new string('A', 64), track, 3, 3, false);
    }

    internal static PeerConnection Loopback(PeerRoom room) => new(room.Invitation, new PeerEndpoint(IPAddress.Loopback, room.Port));
    internal static PeerInvitation Invitation() => new()
    {
        RoomId = Guid.NewGuid(), Generation = 1, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PublicKeySha256 = new string('A', 64),
        Candidates = [new(IPAddress.Parse("192.168.1.2"), 24878), new(IPAddress.Parse("2001:db8::1"), 24878)]
    };
    internal static RaceLoginRequest Login(PeerHostStart settings, string name) => new(settings.Password, name, "#42D7E8", null, "1.5.3", null,
        settings.TrackId, settings.TrackRevision, settings.TrackPackageHash, settings.SectorCount);
    internal static async Task<ClientWebSocket> Connect(PeerConnection connection)
    {
        var socket = new ClientWebSocket();
        connection.Configure(socket);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(connection.WebSocketUri, timeout.Token);
        return socket;
    }
    internal static async Task Send<T>(ClientWebSocket socket, string type, T value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(RaceProtocolJson.SerializeToUtf8Bytes(type, 1, value), WebSocketMessageType.Text, true, timeout.Token);
    }
    internal static async Task<RaceEnvelope> Receive(ClientWebSocket socket, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new byte[RaceProtocol.MaximumMessageBytes];
        while (true)
        {
            var offset = 0;
            ValueWebSocketReceiveResult frame;
            do { frame = await socket.ReceiveAsync(bytes.AsMemory(offset), timeout.Token); offset += frame.Count; }
            while (!frame.EndOfMessage);
            var envelope = RaceProtocolJson.DeserializeEnvelope(bytes.AsSpan(0, offset));
            if (envelope.Type == expected) return envelope;
        }
    }
    internal sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LazyForza-Peer-Tests-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
