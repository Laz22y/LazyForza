using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class VehicleNameBackupTests
{
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task SelectedNamesRoundTripOfflineAndBothRestoreModesKeepNewerData(bool settings, bool vehicles)
    {
        using var fixture = new Fixture();
        var source = Catalog('a', 1);
        await fixture.SourceCache.SaveAsync(source, CancellationToken.None);
        fixture.Source.SetAppSetting("test", "restored");
        var selection = new BackupSelection(settings, vehicles, false, false);
        var manifest = fixture.SourceBackup.Create(fixture.Backup, selection);
        Assert.IsTrue(manifest.Files.ContainsKey("vehicle-names.json"));
        Assert.AreEqual(source.Info, fixture.DestinationBackup.Preview(fixture.Backup).VehicleNames);
        fixture.DestinationBackup.Import(fixture.Backup, BackupImportMode.Merge);
        Assert.AreEqual(source.Info, fixture.DestinationCache.Load(VehicleNameCatalog.EmbeddedSnapshot).Info);
        var newer = Catalog('b', 2);
        await fixture.DestinationCache.SaveAsync(newer, CancellationToken.None);
        foreach (var mode in Enum.GetValues<BackupImportMode>())
        {
            fixture.DestinationBackup.Import(fixture.Backup, mode);
            Assert.AreEqual(newer.Info, fixture.DestinationCache.Load(VehicleNameCatalog.EmbeddedSnapshot).Info);
        }
        fixture.SourceBackup.Create(fixture.Backup, new(false, false, true, false));
        Assert.IsNull(fixture.DestinationBackup.Preview(fixture.Backup).VehicleNames);
    }

    [TestMethod]
    public void BundledNamesAreIncludedWithoutAPreviousDownloadAndOldArchivesRemainReadable()
    {
        using var fixture = new Fixture();
        fixture.SourceBackup.Create(fixture.Backup, new());
        Assert.AreEqual(VehicleNameCatalog.EmbeddedSnapshot.Info, fixture.DestinationBackup.Preview(fixture.Backup).VehicleNames);
        using (var archive = ZipFile.Open(fixture.Backup, ZipArchiveMode.Update))
        {
            archive.GetEntry("vehicle-names.json")!.Delete();
            JsonObject manifest;
            using (var input = archive.GetEntry("manifest.json")!.Open()) manifest = JsonNode.Parse(input)!.AsObject();
            manifest["files"]!.AsObject().Remove("vehicle-names.json");
            archive.GetEntry("manifest.json")!.Delete();
            using var output = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(output, manifest);
        }
        Assert.IsNull(fixture.DestinationBackup.Preview(fixture.Backup).VehicleNames);
        fixture.DestinationBackup.Import(fixture.Backup, BackupImportMode.Overwrite);
        Assert.IsFalse(File.Exists(fixture.DestinationCachePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidCatalogOrHashRejectsTheEntireImportBeforeChangingData(bool matchingHash)
    {
        using var fixture = new Fixture();
        fixture.Source.SetAppSetting("test", "source");
        fixture.Destination.SetAppSetting("test", "destination");
        await fixture.DestinationCache.SaveAsync(Catalog('b', 2), CancellationToken.None);
        var before = File.ReadAllBytes(fixture.DestinationCachePath);
        fixture.SourceBackup.Create(fixture.Backup, new());
        using (var archive = ZipFile.Open(fixture.Backup, ZipArchiveMode.Update))
        {
            byte[] broken = "{}"u8.ToArray();
            archive.GetEntry("vehicle-names.json")!.Delete();
            using (var output = archive.CreateEntry("vehicle-names.json").Open()) output.Write(broken);
            if (matchingHash)
            {
                JsonObject manifest;
                using (var input = archive.GetEntry("manifest.json")!.Open()) manifest = JsonNode.Parse(input)!.AsObject();
                manifest["files"]!["vehicle-names.json"] = Convert.ToHexString(SHA256.HashData(broken));
                archive.GetEntry("manifest.json")!.Delete();
                using var output = archive.CreateEntry("manifest.json").Open();
                JsonSerializer.Serialize(output, manifest);
            }
        }
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.DestinationBackup.Import(fixture.Backup, BackupImportMode.Overwrite));
        Assert.AreEqual("destination", fixture.Destination.GetAppSetting("test"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.DestinationCachePath));
    }

    [TestMethod]
    public async Task FailedDatabaseImportRollsBackCatalogAndFailedCacheWriteNeverChangesDatabase()
    {
        using var fixture = new Fixture();
        await fixture.SourceCache.SaveAsync(Catalog('b', 2), CancellationToken.None);
        await fixture.DestinationCache.SaveAsync(Catalog('a', 1), CancellationToken.None);
        fixture.Source.SetAppSetting("reject", "source");
        fixture.Destination.SetAppSetting("keep", "destination");
        fixture.SourceBackup.Create(fixture.Backup, new());
        using (var raw = new WinSqliteDatabase(fixture.DestinationDirectories.DatabasePath))
            raw.Execute("CREATE TRIGGER RejectBackup BEFORE INSERT ON AppSettings WHEN NEW.Key='reject' BEGIN SELECT RAISE(ABORT,'test failure'); END;");
        var bytes = File.ReadAllBytes(fixture.DestinationCachePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.DestinationBackup.Import(fixture.Backup, BackupImportMode.Overwrite));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.DestinationCachePath));
        Assert.IsNull(fixture.Destination.GetAppSetting("reject"));
        Assert.AreEqual("destination", fixture.Destination.GetAppSetting("keep"));
        using (var raw = new WinSqliteDatabase(fixture.DestinationDirectories.DatabasePath)) raw.Execute("DROP TRIGGER RejectBackup;");
        File.Delete(fixture.DestinationCachePath);
        Directory.CreateDirectory(fixture.DestinationCachePath); // A directory cannot be atomically replaced by the cache file.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => fixture.DestinationBackup.Import(fixture.Backup, BackupImportMode.Overwrite)));
        Assert.IsNull(fixture.Destination.GetAppSetting("reject"));
    }

    private static VehicleNameSnapshot Catalog(char revision, int day) => VehicleNameSnapshot.FromUpstream(
        JsonSerializer.Serialize(Enumerable.Range(1, 100).ToDictionary(i => $"Car {revision} {i}", i => i)),
        new string(revision, 40), new DateTimeOffset(2030, 1, day, 0, 0, 0, TimeSpan.Zero));

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "lfz-name-backup-" + Guid.NewGuid().ToString("N"));
        public LazyForzaStore Source { get; }
        public LazyForzaStore Destination { get; }
        public DataDirectoryService DestinationDirectories { get; }
        public VehicleNameCatalogCache SourceCache { get; }
        public VehicleNameCatalogCache DestinationCache { get; }
        public DataBackupService SourceBackup { get; }
        public DataBackupService DestinationBackup { get; }
        public string Backup => Path.Combine(root, "test.lfzbackup");
        public string DestinationCachePath => Path.Combine(DestinationDirectories.Root, "Cache", "vehicle-names.json");
        public Fixture()
        {
            var sourceDirectories = new DataDirectoryService(Path.Combine(root, "source"));
            DestinationDirectories = new(Path.Combine(root, "destination"));
            Source = new(sourceDirectories.DatabasePath); Destination = new(DestinationDirectories.DatabasePath);
            SourceCache = VehicleNameCatalogCache.ForDirectory(sourceDirectories.Root);
            DestinationCache = VehicleNameCatalogCache.ForDirectory(DestinationDirectories.Root);
            SourceBackup = new(Source, "test", sourceDirectories); DestinationBackup = new(Destination, "test", DestinationDirectories);
        }
        public void Dispose() { Source.Dispose(); Destination.Dispose(); Directory.Delete(root, true); }
    }
}
