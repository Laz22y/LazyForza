using System.IO;
using System.Net;
using LazyForza.Analysis;
using LazyForza.EstatePeer;
using LazyForza.EstatePeer.Host;
using LazyForza.Modules.EstateRace;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

public sealed partial class EstateRaceClientModuleTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PeerModuleUsesPinnedTransportAndKeepsRecoveryIdentitySeparateFromServerMode(bool useUdp)
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-peer-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var track = CreateTrack();
            var definition = CreateDefinition(track);
            using var store = new LazyForzaStore(Path.Combine(root, "client.db"));
            store.SaveTrack(track, TrackAlgorithms.CreateSectors(track), definition);
            var packages = new EstateTrackPackageService(store, "1.5.4");
            var package = Path.Combine(root, "track.lfzestate");
            packages.Export(track.Id, package, default);
            var hash = packages.Identify(track.Id).TrackFingerprintSha256;
            var settings = new PeerHostStart(Path.Combine(root, "room"), "Module test", "test-password", 0, "192.0.2.10", null,
                track.Id.ToString("D"), track.Name, definition.MapRevision, hash, package, 3, 3, false);
            await using var room = new PeerRoom(settings);
            await room.StartAsync(default);
            if (useUdp && !PeerQuicHost.IsSupported) { Assert.Inconclusive("QUIC unavailable."); return; }
            await using var udp = useUdp ? new PeerQuicJoin(room.Invitation) : null;
            PeerConnection connection;
            if (udp is not null)
            {
                Assert.IsTrue(room.Control(new("acceptReceipt", udp.Receipt.Encode())).Success);
                connection = await udp.ConnectAsync(default);
            }
            else connection = new PeerConnection(room.Invitation, new PeerEndpoint(IPAddress.Loopback, room.Port));
            var descriptor = await EstateRaceModule.ReadServerDescriptorAsync(connection.Origin.AbsoluteUri, default, connection, settings.Password);
            Assert.AreEqual(hash, descriptor.ActiveTrackPackageHash);
            await store.SetAsync(EstateRaceModule.ModuleId, "resumeToken", "legacy-server-token", default);
            await store.SetAsync(EstateRaceModule.ModuleId, "serverAddress", "https://existing.example", default);
            await using var feed = new TestFeed();
            await using var module = new EstateRaceModule(() => new EstateRaceTrackContext(track, definition, 0, 0, 0, true, null, SectorCount: 3));
            await module.InitializeAsync(new TestContext(feed, store), default);
            _ = await module.LoadSavedProfileAsync(default);
            await module.StartAsync(default);
            await module.ConnectAsync(new(connection.Origin.AbsoluteUri, settings.Password, "Peer driver", "#42D7E8", null, Peer: connection), default, hash);
            Assert.AreEqual(EstateRaceConnectionState.Connected, module.State.ConnectionState);
            Assert.AreEqual("legacy-server-token", await store.GetAsync(EstateRaceModule.ModuleId, "resumeToken", default));
            Assert.AreEqual("https://existing.example", await store.GetAsync(EstateRaceModule.ModuleId, "serverAddress", default));
            var key = $"peerResume.{connection.RecoveryScope}.driver";
            Assert.IsFalse(string.IsNullOrWhiteSpace(await store.GetAsync(EstateRaceModule.ModuleId, key, default)));
            await module.DisconnectAsync();
            Assert.AreEqual(string.Empty, await store.GetAsync(EstateRaceModule.ModuleId, key, default));
            Assert.AreEqual("legacy-server-token", await store.GetAsync(EstateRaceModule.ModuleId, "resumeToken", default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
