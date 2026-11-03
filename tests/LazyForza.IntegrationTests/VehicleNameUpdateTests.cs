using System.Net;
using System.Text;
using System.Text.Json;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class VehicleNameUpdateTests
{
    private static readonly string First = new('a', 40), Second = new('b', 40);
    private static readonly DateTimeOffset Date = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MigrationPreservesNewerDestinationAndDelayedDownloadCannotDowngradeRestoredNames()
    {
        using var source = new Fixture(); using var destination = new Fixture();
        var handler = new GistHandler();
        using var manager = source.Manager(handler);
        await manager.InstallAsync(new(First, Date, 1, 0), CancellationToken.None);
        Assert.ThrowsExactly<IOException>(() => manager.MigrateTo(destination.Directories.Root,
            () => throw new IOException("Cannot save startup profile")));
        Assert.IsFalse(File.Exists(destination.CachePath));
        var savedDirectory = false;
        manager.MigrateTo(destination.Directories.Root, () => savedDirectory = true);
        Assert.IsTrue(savedDirectory);
        using var offline = destination.Manager(new GistHandler { Fail = true });
        Assert.AreEqual(First, offline.Snapshot.Info!.Revision);
        var newer = VehicleNameSnapshot.FromUpstream(JsonSerializer.Serialize(
            Enumerable.Range(1, 100).ToDictionary(i => "Imported " + i, i => i)), Second, Date.AddDays(1));
        await VehicleNameCatalogCache.ForDirectory(destination.Directories.Root).SaveAsync(newer, CancellationToken.None);
        manager.MigrateTo(destination.Directories.Root, () => { });
        offline.Reload();
        Assert.AreEqual(Second, offline.Snapshot.Info!.Revision);

        await VehicleNameCatalogCache.ForDirectory(source.Directories.Root).SaveAsync(newer, CancellationToken.None);
        var result = await manager.InstallAsync(new(First, Date, 1, 0), CancellationToken.None);
        Assert.AreEqual(Second, result.Info!.Revision, "A download accepted before a backup restore must not replace the newer restored catalog.");
        using var restarted = source.Manager(new GistHandler { Latest = First });
        Assert.AreEqual(Second, restarted.Snapshot.Info!.Revision);
        Assert.AreEqual(VehicleNameUpdateState.Current, (await restarted.CheckAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task CheckOnlyReadsMetadataAndDownloadsExactlyTheAcceptedRevision()
    {
        using var fixture = new Fixture();
        var handler = new GistHandler();
        using var manager = fixture.Manager(handler);
        var result = await manager.CheckAsync(CancellationToken.None);
        Assert.AreEqual(VehicleNameUpdateState.Available, result.State);
        Assert.AreEqual(1, handler.Urls.Count);
        StringAssert.EndsWith(handler.Urls[0], "/commits?per_page=1");
        Assert.IsFalse(File.Exists(fixture.CachePath), "Checking must not cache name content before consent.");
        handler.Latest = Second; // The upstream changes while the offer is open.
        var installed = await manager.InstallAsync(result.Revision, CancellationToken.None);
        Assert.AreEqual(First, installed.Info!.Revision);
        StringAssert.EndsWith(handler.Urls[1], "/" + First);
        Assert.AreEqual("New vehicle", installed.Names[999991]);
        using var offline = fixture.Manager(new GistHandler { Fail = true });
        Assert.AreEqual(First, offline.Snapshot.Info!.Revision);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => offline.CheckAsync(CancellationToken.None));
        Assert.AreEqual("New vehicle", offline.Snapshot.Names[999991]);
    }

    [TestMethod]
    public async Task LaterOffersAgainWhileIgnorePersistsForOnlyThatRevisionIncludingManualChecks()
    {
        using var fixture = new Fixture();
        using (var manager = fixture.Manager(new()))
        {
            var check = await manager.CheckAsync(CancellationToken.None);
            Assert.AreEqual(VehicleNameUpdateState.Available, check.State);
        }
        using (var manager = fixture.Manager(new()))
        {
            var check = await manager.CheckAsync(CancellationToken.None);
            Assert.AreEqual(VehicleNameUpdateState.Available, check.State);
            manager.Ignore(check.Revision);
        }
        var handler = new GistHandler();
        using var reopened = fixture.Manager(handler);
        var ignored = await reopened.CheckAsync(CancellationToken.None);
        Assert.AreEqual(VehicleNameUpdateState.Ignored, ignored.State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.InstallAsync(ignored.Revision, CancellationToken.None));
        Assert.AreEqual(1, handler.Urls.Count);
        Assert.IsFalse(File.Exists(fixture.CachePath));
        handler.Latest = Second;
        Assert.AreEqual(VehicleNameUpdateState.Available, (await reopened.CheckAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("truncated")]
    [DataRow("revision")]
    [DataRow("duplicate")]
    [DataRow("incomplete")]
    [DataRow("oversized")]
    public async Task FailedDownloadNeverReplacesLastGoodCache(string failure)
    {
        using var fixture = new Fixture();
        var handler = new GistHandler();
        using var manager = fixture.Manager(handler);
        var initial = await manager.InstallAsync(new(First, Date, 1, 0), CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath);
        handler.Failure = failure;
        try
        {
            await manager.InstallAsync(new(Second, Date.AddDays(1), 1, 0), CancellationToken.None);
            Assert.Fail("An invalid download must not be accepted.");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException) { }
        Assert.AreSame(initial, manager.Snapshot);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.CachePath));
    }

    [TestMethod]
    public async Task CancelDuringDownloadKeepsExistingCatalogAndDoesNotCreatePartialCache()
    {
        using var fixture = new Fixture();
        var handler = new GistHandler();
        using var manager = fixture.Manager(handler);
        await manager.InstallAsync(new(First, Date, 1, 0), CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath);
        handler.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource();
        var pending = manager.InstallAsync(new(Second, Date.AddDays(1), 1, 0), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(fixture.CachePath)!).Length);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ExistingAutomaticNamesFollowNewAndCorrectedNamesWhileCustomNamesSurvive()
    {
        using var fixture = new Fixture();
        var previous = VehicleNameCatalog.Current;
        try
        {
            VehicleNameCatalog.Activate(VehicleNameCatalog.EmbeddedSnapshot);
            foreach (var ordinal in new[] { 999991, 2038 })
                await fixture.Store.SaveShiftLearningAsync(new(LearningState.Ready, 1, 1,
                    new(ordinal, 4, 800, 2, 6, 8000, "gears", "curve"), [], [], [], new Dictionary<string, int>(), "ready"), CancellationToken.None);
            var unknown = fixture.Store.ListVehicleProfiles().Single(profile => profile.Fingerprint.CarOrdinal == 999991);
            var known = fixture.Store.ListVehicleProfiles().Single(profile => profile.Fingerprint.CarOrdinal == 2038);
            Assert.IsNull(unknown.CustomName);
            StringAssert.Contains(MainWindow.VehicleProfileDisplayName(unknown, 0, 1), "999991");
            var originalName = MainWindow.VehicleProfileDisplayName(known, 0, 1);
            var handler = new GistHandler();
            using var manager = fixture.Manager(handler);
            VehicleNameCatalog.Activate(await manager.InstallAsync(new(First, Date, 1, 0), CancellationToken.None));
            Assert.AreEqual("New vehicle", MainWindow.VehicleProfileDisplayName(unknown, 0, 1));
            Assert.AreEqual("Corrected Alfa Romeo", MainWindow.VehicleProfileDisplayName(known, 0, 1));
            // Even an explicit rename equal to a previous catalog name is user-owned.
            fixture.Store.RenameVehicleProfile(known.Id, originalName);
            handler.VehicleName = "Renamed upstream vehicle";
            VehicleNameCatalog.Activate(await manager.InstallAsync(new(Second, Date.AddDays(1), 2, 1), CancellationToken.None));
            Assert.AreEqual("Renamed upstream vehicle", MainWindow.VehicleProfileDisplayName(unknown, 0, 1));
            var saved = fixture.Store.ListVehicleProfiles();
            Assert.IsNull(saved.Single(profile => profile.Id == unknown.Id).CustomName);
            Assert.AreEqual(originalName, MainWindow.VehicleProfileDisplayName(saved.Single(profile => profile.Id == known.Id), 0, 1));
        }
        finally { VehicleNameCatalog.Activate(previous); }
    }

    private sealed class Fixture : IDisposable
    {
        public DataDirectoryService Directories { get; } = new(Path.Combine(Path.GetTempPath(), "lfz-name-update-" + Guid.NewGuid().ToString("N")));
        public LazyForzaStore Store { get; }
        public string CachePath => Path.Combine(Directories.Root, "Cache", "vehicle-names.json");
        public Fixture() { Directories.EnsureCreated(); Store = new(Directories.DatabasePath); }
        public VehicleNameUpdateManager Manager(GistHandler handler) => new(Store, Directories, new VehicleNameCatalogClient(handler));
        public void Dispose() { Store.Dispose(); Directory.Delete(Directories.Root, true); }
    }

    private sealed class GistHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public string Latest { get; set; } = First;
        public string VehicleName { get; set; } = "New vehicle";
        public string? Failure { get; set; }
        public bool Fail { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            if (WaitForCancellation) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            if (Fail || Failure == "http") return new(HttpStatusCode.ServiceUnavailable);
            if (url.Contains("/commits?", StringComparison.Ordinal))
                return Json(new[] { new { version = Latest, committed_at = Date, change_status = new { additions = 5, deletions = 2 } } });
            if (Failure == "oversized") return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 4 * 1024 * 1024 + 1)) };
            var names = Enumerable.Range(1, 100).ToDictionary(i => "Car " + i, i => i);
            names[VehicleName] = 999991; names["Corrected Alfa Romeo"] = 2038;
            if (Failure == "duplicate") names["Duplicated"] = 1;
            if (Failure == "incomplete") names.Clear();
            var revision = url.Split('/')[^1];
            return Json(new { id = VehicleNameCatalogClient.GistId,
                history = new[] { new { version = Failure == "revision" ? new string('c', 40) : revision } },
                files = new Dictionary<string, object> { [VehicleNameCatalogClient.FileName] = new
                    { truncated = Failure == "truncated", content = JsonSerializer.Serialize(names) } } });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
