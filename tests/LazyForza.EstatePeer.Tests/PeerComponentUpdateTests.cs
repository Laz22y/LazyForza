using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyForza.EstatePeer;
using LazyForza.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.EstatePeer.Tests;

[TestClass]
public sealed class PeerComponentUpdateTests
{
    private const string ClientVersion = "1.5.4-alpha-1";

    [TestMethod]
    public void SignedCatalogRejectsTamperingWrongKeysExpiryAndUntrustedDownloads()
    {
        using var fixture = new Fixture();
        var signed = fixture.Sign(fixture.Release);
        Assert.AreEqual(fixture.NewCatalog.Version, fixture.Distribution.Verify(signed).Release.Catalog.Version);
        var payload = Convert.FromBase64String(signed.Payload);
        payload[^2] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Distribution.Verify(signed with { Payload = Convert.ToBase64String(payload) }));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherTrust = new PeerComponentDistribution(otherKey.ExportSubjectPublicKeyInfoPem(), ClientVersion, true);
        Assert.ThrowsExactly<InvalidDataException>(() => otherTrust.Verify(signed));
        var expired = fixture.Sign(fixture.Release with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Distribution.Verify(expired));
        Assert.IsNotNull(fixture.Distribution.Verify(expired, requireFresh: false));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Distribution.Verify(fixture.Sign(fixture.Release with
        { Catalog = fixture.NewCatalog with { DownloadUrls = ["https://example.com/host.zip"] } })));
        foreach (var url in new[] { "http://github.com/Laz22y/LazyForza.Components/releases/download/a/b", "https://github.com:444/Laz22y/LazyForza.Components/releases/download/a/b",
                     "https://github.com/Other/Components/releases/download/a/b", "https://github.com@evil.test/Laz22y/LazyForza.Components/releases/download/a/b" })
            Assert.ThrowsExactly<InvalidDataException>(() => PeerComponentDistribution.ValidateAssetUri(url));
    }

    [TestMethod]
    public void CompatibilityChecksClientChannelPipeRaceProtocolAndProjectFormat()
    {
        using var fixture = new Fixture();
        foreach (var release in new[]
        {
            fixture.Release with { MinimumClientVersion = "1.5.4-alpha-2" },
            fixture.Release with { MaximumClientVersionExclusive = ClientVersion },
            fixture.Release with { RaceProtocolVersion = 3 }, fixture.Release with { ProjectFormatVersion = 2 },
            fixture.Release with { Catalog = fixture.NewCatalog with { ControlVersion = 2 } },
            fixture.Release with { Catalog = fixture.NewCatalog with { Runtime = "win-arm64" } }
        }) Assert.ThrowsExactly<PeerComponentIncompatibleException>(() => fixture.Distribution.Verify(fixture.Sign(release)));
        var stable = new PeerComponentDistribution(fixture.Key.ExportSubjectPublicKeyInfoPem(), "1.5.4", false);
        Assert.ThrowsExactly<PeerComponentIncompatibleException>(() => stable.Verify(fixture.Sign(fixture.Release)));
        Assert.IsNotNull(stable.Verify(fixture.Sign(fixture.Release with { Channel = "stable", Catalog = fixture.NewCatalog with { Version = "0.3.0" } })));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DiscoveryFallsBackOnUnavailableOrTamperedMirrorAndSkipsIncompatibleLatest(bool tampered)
    {
        using var fixture = new Fixture();
        var hosts = new List<string>();
        var currentTag = PeerComponentDistribution.TagPrefix + fixture.NewCatalog.ReleaseId;
        var incompatible = fixture.Release with { Catalog = fixture.NewCatalog with { Version = "9.0.0" }, MinimumClientVersion = "9.0.0", MaximumClientVersionExclusive = "10.0.0" };
        using var http = new HttpClient(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "api.gitcode.com")
                return Task.FromResult(tampered ? Json(request.RequestUri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new[] { new { tag_name = currentTag } }) : "{\"Payload\":\"AA==\",\"Signature\":\"AA==\"}")
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.RequestUri.Host == "api.github.com") return Task.FromResult(Json(JsonSerializer.Serialize(new[]
            {
                new { tag_name = PeerComponentDistribution.TagPrefix + "9.0.0-r1" }, new { tag_name = currentTag }
            })));
            return Task.FromResult(Json(JsonSerializer.Serialize(fixture.Sign(request.RequestUri.AbsolutePath.Contains("9.0.0", StringComparison.Ordinal) ? incompatible : fixture.Release))));
        }));
        var result = await fixture.Distribution.CheckAsync(UpdateSourceKind.GitCode, 0, fixture.Baseline.Version, http, default);
        Assert.AreEqual(fixture.NewCatalog.Version, result!.Release.Catalog.Version);
        Assert.AreEqual("api.gitcode.com", hosts[0]);
        Assert.IsTrue(hosts.Contains("api.github.com"));
    }

    [TestMethod]
    public async Task DiscoveryFindsUpdatesWhenPreferredMirrorHasNotSyncedYet()
    {
        using var fixture = new Fixture();
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.Host);
            return Task.FromResult(Json(request.RequestUri.Host switch
            {
                "api.gitcode.com" => "[]",
                "api.github.com" => JsonSerializer.Serialize(new[] { new { tag_name = PeerComponentDistribution.TagPrefix + fixture.NewCatalog.ReleaseId } }),
                _ => JsonSerializer.Serialize(fixture.Sign(fixture.Release))
            }));
        }));
        var result = await fixture.Distribution.CheckAsync(UpdateSourceKind.GitCode, 0, fixture.Baseline.Version, http, default);
        Assert.AreEqual(fixture.NewCatalog.Version, result!.Release.Catalog.Version);
        CollectionAssert.AreEqual(new[] { "api.gitcode.com", "api.github.com", "github.com" }, requests);
    }

    [TestMethod]
    [DataRow("0.6.0", 2)]
    [DataRow("0.7.0", 1)]
    public async Task ServerVersionsAndHostRevisionsUpdateIndependently(string nextVersion, int nextRevision)
    {
        using var fixture = new Fixture("0.6.0", nextVersion, nextRevision);
        var stable = new PeerComponentDistribution(fixture.Key.ExportSubjectPublicKeyInfoPem(), "1.5.4", false);
        var signed = fixture.Sign(fixture.Release with { Channel = "stable" });
        var tag = PeerComponentDistribution.TagPrefix + fixture.NewCatalog.ReleaseId;
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "api.github.com"
            ? JsonSerializer.Serialize(new[] { new { tag_name = tag }, new { tag_name = PeerComponentDistribution.TagPrefix + "0.6.0-r1" } })
            : JsonSerializer.Serialize(signed)))));
        var candidate = await stable.CheckAsync(UpdateSourceKind.GitHub, 0, fixture.Baseline.Version, http, default, fixture.Baseline.Revision);
        Assert.IsNotNull(candidate);
        Assert.AreEqual(nextRevision, candidate.Release.Catalog.Revision);
        var manager = fixture.Manager();
        await manager.Current.InstallAsync(fixture.OldArchive, default);
        await manager.InstallAsync(candidate, fixture.NewArchive, () => false, null, default);
        var reopened = fixture.Manager(); await reopened.RestoreAsync(default);
        Assert.AreEqual($"{nextVersion}-r{nextRevision}", reopened.Current.Catalog.ReleaseId);
        await reopened.RollbackAsync(() => false, default);
        Assert.AreEqual("0.6.0-r1", reopened.Current.Catalog.ReleaseId);
    }

    [TestMethod]
    public async Task PackageFallbackVerifiesHashesAndHonorsPreferredSource()
    {
        using var fixture = new Fixture();
        var bytes = await File.ReadAllBytesAsync(fixture.NewArchive);
        var bad = bytes.ToArray(); bad[^1] ^= 1;
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(requests.Count == 1 ? bad : bytes) });
        }));
        var store = new PeerComponentStore(fixture.Components, fixture.NewCatalog, UpdateSourceKind.GitHub);
        await store.DownloadAndInstallAsync(null, default, http);
        CollectionAssert.AreEqual(new[] { "github.com", "api.gitcode.com" }, requests);
        Assert.IsNotNull(await store.FindVerifiedExecutableAsync(default));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Components, ".download-*").Length);
    }

    [TestMethod]
    public async Task UpdateBacksUpProjectsPersistsTrustedSelectionAndRollsBackWithoutRevertingResults()
    {
        using var fixture = new Fixture();
        var manager = fixture.Manager();
        await manager.Current.InstallAsync(fixture.OldArchive, default);
        var candidate = fixture.Distribution.Verify(fixture.Sign(fixture.Release));
        // A caller-supplied catalog is not trusted over the signed payload.
        await manager.InstallAsync(candidate with { Release = fixture.Release with { Catalog = fixture.Baseline } }, fixture.NewArchive, () => false, null, default);
        Assert.AreEqual(fixture.NewCatalog.Version, manager.Current.Catalog.Version);
        Assert.AreEqual("saved results", File.ReadAllText(fixture.RoomFile));
        using (var backup = ZipFile.OpenRead(Directory.GetFiles(Path.Combine(fixture.Root, "Backups", "Components")).Single()))
        using (var reader = new StreamReader(backup.GetEntry("project/results.json")!.Open()))
            Assert.AreEqual("saved results", reader.ReadToEnd());
        var reopened = fixture.Manager(); await reopened.RestoreAsync(default);
        Assert.AreEqual(fixture.NewCatalog.Version, reopened.Current.Catalog.Version);
        Assert.IsTrue(reopened.CanRollback);
        File.WriteAllText(fixture.RoomFile, "new results");
        await reopened.RollbackAsync(() => false, default);
        Assert.AreEqual(fixture.Baseline.Version, reopened.Current.Catalog.Version);
        Assert.AreEqual("new results", File.ReadAllText(fixture.RoomFile));
        var rollbackReopened = fixture.Manager(); await rollbackReopened.RestoreAsync(default);
        Assert.AreEqual(fixture.Baseline.Version, rollbackReopened.Current.Catalog.Version);
        var replayed = fixture.Distribution.Verify(fixture.Sign(fixture.Release with { Sequence = 1 }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => rollbackReopened.InstallAsync(replayed, fixture.NewArchive, () => false, null, default));
    }

    [TestMethod]
    public async Task RunningRoomBlocksInstallAndRollbackAndInterruptedSwitchCanRetry()
    {
        using var fixture = new Fixture();
        var manager = fixture.Manager(); await manager.Current.InstallAsync(fixture.OldArchive, default);
        var candidate = fixture.Distribution.Verify(fixture.Sign(fixture.Release));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.InstallAsync(candidate, fixture.NewArchive, () => true, null, default));
        Assert.AreEqual(fixture.Baseline.Version, manager.Current.Catalog.Version);
        var checks = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.InstallAsync(candidate, fixture.NewArchive, () => ++checks > 1, null, default));
        var reopened = fixture.Manager(); await reopened.RestoreAsync(default);
        Assert.AreEqual(fixture.Baseline.Version, reopened.Current.Catalog.Version);
        await reopened.InstallAsync(candidate, fixture.NewArchive, () => false, null, default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.RollbackAsync(() => true, default));
        Assert.AreEqual(fixture.NewCatalog.Version, reopened.Current.Catalog.Version);
    }

    [TestMethod]
    public async Task FailedBackupAndCanceledInstallNeverChangeSelection()
    {
        using var fixture = new Fixture();
        var manager = fixture.Manager(); await manager.Current.InstallAsync(fixture.OldArchive, default);
        var candidate = fixture.Distribution.Verify(fixture.Sign(fixture.Release));
        using (File.Open(fixture.RoomFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsExactlyAsync<IOException>(() => manager.InstallAsync(candidate, fixture.NewArchive, () => false, null, default));
        Assert.AreEqual(fixture.Baseline.Version, manager.Current.Catalog.Version);
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(fixture.Root, "Backups", "Components")).Length);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => manager.InstallAsync(candidate, fixture.NewArchive, () => false, null, canceled.Token));
        Assert.AreEqual(fixture.Baseline.Version, manager.Current.Catalog.Version);
        await manager.InstallAsync(candidate, fixture.NewArchive, () => false, null, default);
        Assert.AreEqual(fixture.NewCatalog.Version, manager.Current.Catalog.Version);
    }

    [TestMethod]
    public async Task OfflineImportRequiresSignatureAndCanRepairRemovedInstallWithoutDowngrading()
    {
        using var fixture = new Fixture();
        var manager = fixture.Manager();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.ImportAsync(fixture.NewArchive, () => false, default));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, PeerComponentDistribution.ManifestName), JsonSerializer.Serialize(fixture.Sign(fixture.Release)));
        await manager.ImportAsync(fixture.NewArchive, () => false, default);
        Assert.IsFalse(manager.CanRollback, "The original component was never installed.");
        manager.Current.Uninstall();
        var reopened = fixture.Manager(); await reopened.RestoreAsync(default);
        Assert.AreEqual(fixture.NewCatalog.Version, reopened.Current.Catalog.Version);
        // Repair of an already trusted version does not need an online check or a still-fresh sidecar.
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, PeerComponentDistribution.ManifestName), JsonSerializer.Serialize(fixture.Sign(fixture.Release with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) })));
        await reopened.ImportAsync(fixture.NewArchive, () => false, default);
        Assert.IsNotNull(await reopened.Current.FindVerifiedExecutableAsync(default));
        File.WriteAllText(Path.Combine(reopened.Current.InstallDirectory, PeerComponentStore.ExecutableName), "broken");
        await reopened.ImportAsync(fixture.NewArchive, () => false, default);
        Assert.IsNotNull(await reopened.Current.FindVerifiedExecutableAsync(default));
        Assert.AreEqual("saved results", File.ReadAllText(fixture.RoomFile));
    }

    [TestMethod]
    public async Task CancellationDoesNotContactFallbackSource()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, token) =>
        {
            calls++; cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token);
        }));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fixture.Distribution.CheckAsync(UpdateSourceKind.GitCode, 0, fixture.Baseline.Version, http, cancellation.Token));
        Assert.AreEqual(1, calls);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PeerTests.TestDirectory files = new();
        public string Root => files.Path;
        public string Components => Path.Combine(Root, "Components");
        public string RoomFile => Path.Combine(Root, "Rooms", "project", "results.json");
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public PeerComponentDistribution Distribution { get; }
        public PeerComponentCatalog Baseline { get; }
        public PeerComponentCatalog NewCatalog { get; }
        public string OldArchive { get; }
        public string NewArchive { get; }
        public PeerComponentRelease Release { get; }
        public Fixture(string oldVersion = "0.5.0", string newVersion = "0.6.0", int newRevision = 1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RoomFile)!); File.WriteAllText(RoomFile, "saved results");
            (Baseline, OldArchive) = Package(oldVersion, 1);
            (NewCatalog, NewArchive) = Package(newVersion, newRevision);
            Distribution = new(Key.ExportSubjectPublicKeyInfoPem(), ClientVersion, true);
            Release = new(1, 10, "preview", "1.5.3", "1.6.0", 2, 1, DateTimeOffset.UtcNow.AddDays(7), NewCatalog);
        }
        private (PeerComponentCatalog, string) Package(string version, int revision)
        {
            var archive = Path.Combine(Root, $"{version}-r{revision}.zip");
            var bytes = Encoding.UTF8.GetBytes($"host {version}-r{revision}");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var output = zip.CreateEntry(PeerComponentStore.ExecutableName).Open()) output.Write(bytes);
            return (new(1, 1, version, "win-x64", new FileInfo(archive).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))), null,
                [new(PeerComponentStore.ExecutableName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))],
                [$"https://api.gitcode.com/api/v5/repos/{PeerComponentDistribution.Repository}/releases/v{version}/attach_files/host.zip/download",
                    $"https://github.com/{PeerComponentDistribution.Repository}/releases/download/v{version}/host.zip"], revision), archive);
        }
        public PeerSignedComponent Sign(PeerComponentRelease release)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(release);
            return new(Convert.ToBase64String(payload), Convert.ToBase64String(Key.SignData(payload, HashAlgorithmName.SHA256)));
        }
        public PeerComponentUpdateManager Manager() => new(Components, Path.Combine(Root, "Rooms"), Baseline, Distribution, UpdateSourceKind.GitCode);
        public void Dispose() { Key.Dispose(); files.Dispose(); }
    }
}
