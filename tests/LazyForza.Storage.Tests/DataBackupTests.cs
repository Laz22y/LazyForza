using System.IO.Compression;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class DataBackupTests
{
    [TestMethod]
    public async Task PortableBackupRoundTripsSelectedDataAndPreviewsConflicts()
    {
        var sourcePath = TempPath(".db");
        var destinationPath = TempPath(".db");
        var backupPath = TempPath(".lfzbackup");
        try
        {
            Guid trackId;
            Guid lapId;
            using (var source = new LazyForzaStore(sourcePath))
            {
                source.SetAppSetting("ui.test", "source");
                await source.SetAsync("dashboard", "enabled", "True", CancellationToken.None);
                await source.SaveShiftLearningAsync(
                    VehicleSnapshot(),
                    CancellationToken.None);
                var saved = SaveTrackAndLap(source);
                trackId = saved.TrackId;
                lapId = saved.LapId;

                var service = new DataBackupService(source, "1.2.1");
                var manifest = service.Create(
                    backupPath,
                    new BackupSelection(),
                    CancellationToken.None);
                Assert.AreEqual(DataBackupService.CurrentFormatVersion, manifest.FormatVersion);
                Assert.AreEqual(source.SchemaVersion, manifest.SchemaVersion);
                Assert.AreEqual("1.2.1", manifest.ApplicationVersion);
                Assert.IsTrue(manifest.Files.ContainsKey("data.json"));
            }

            using (var destination = new LazyForzaStore(destinationPath))
            {
                destination.SetAppSetting("ui.test", "destination");
                var service = new DataBackupService(destination, "1.2.1");
                var preview = service.Preview(backupPath);
                Assert.AreEqual(1, preview.Vehicles);
                Assert.AreEqual(1, preview.Laps);
                Assert.AreEqual(1, preview.CustomTracks);
                Assert.IsTrue(preview.Conflicts.Any(conflict =>
                    conflict.Category == "配置" &&
                    conflict.Key == "ui.test"));

                var imported = service.Import(backupPath, BackupImportMode.Merge);
                Assert.AreEqual("destination", destination.GetAppSetting("ui.test"));
                Assert.AreEqual(1, imported.PreservedConflicts);
                Assert.AreEqual(1, destination.CountVehicleProfiles());
                Assert.IsNotNull(destination.LoadTrack(trackId));
                Assert.IsNotNull(destination.LoadLap(lapId));

                destination.SetAppSetting("ui.test", "changed-again");
                destination.RenameTrack(trackId, "Destination name");
                var overwritten = service.Import(backupPath, BackupImportMode.Overwrite);
                Assert.AreEqual("source", destination.GetAppSetting("ui.test"));
                Assert.AreEqual("Portable track", destination.LoadTrack(trackId)!.Value.Track.Name);
                Assert.AreEqual(0, overwritten.PreservedConflicts);
                var importedLap = destination.LoadLap(lapId)!;
                Assert.HasCount(24, importedLap.Samples);
                Assert.IsNotNull(importedLap.Samples[0].Dynamics);
                Assert.AreEqual(
                    0.42,
                    importedLap.Samples[0].Dynamics!.Steering,
                    0.0001);
            }
        }
        finally
        {
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [TestMethod]
    public void BackupRejectsPayloadWhoseChecksumNoLongerMatchesManifest()
    {
        var databasePath = TempPath(".db");
        var backupPath = TempPath(".lfzbackup");
        try
        {
            using var store = new LazyForzaStore(databasePath);
            store.SetAppSetting("test", "value");
            var service = new DataBackupService(store, "1.2.1");
            service.Create(
                backupPath,
                new BackupSelection(Settings: true, Vehicles: false, Laps: false, CustomTracks: false));

            using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update))
            {
                archive.GetEntry("data.json")!.Delete();
                var entry = archive.CreateEntry("data.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("{\"tables\":[]}");
            }

            var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                service.Preview(backupPath));
            StringAssert.Contains(exception.Message, "校验失败");
        }
        finally
        {
            DeleteDatabase(databasePath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [TestMethod]
    public async Task ComponentSelectionDoesNotLeakUnselectedPersonalData()
    {
        var sourcePath = TempPath(".db");
        var destinationPath = TempPath(".db");
        var backupPath = TempPath(".lfzbackup");
        try
        {
            using (var source = new LazyForzaStore(sourcePath))
            {
                source.SetAppSetting("selected.setting", "included");
                await source.SaveShiftLearningAsync(
                    VehicleSnapshot(),
                    CancellationToken.None);
                SaveTrackAndLap(source);
                new DataBackupService(source, "1.2.1").Create(
                    backupPath,
                    new BackupSelection(
                        Settings: true,
                        Vehicles: false,
                        Laps: false,
                        CustomTracks: false));
            }

            using var destination = new LazyForzaStore(destinationPath);
            var service = new DataBackupService(destination, "1.2.1");
            var preview = service.Preview(backupPath);
            Assert.IsTrue(preview.Settings > 0);
            Assert.AreEqual(0, preview.Vehicles);
            Assert.AreEqual(0, preview.Laps);
            Assert.AreEqual(0, preview.CustomTracks);

            service.Import(backupPath, BackupImportMode.Merge);
            Assert.AreEqual("included", destination.GetAppSetting("selected.setting"));
            Assert.AreEqual(0, destination.CountVehicleProfiles());
            Assert.AreEqual(0, destination.CountTracks());
            Assert.AreEqual(0, destination.CountLaps());
        }
        finally
        {
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [TestMethod]
    public void PreMigrationSnapshotAndRotationProtectOnlyAutomaticBackups()
    {
        var databasePath = TempPath(".db");
        var backupDirectory = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        try
        {
            using (var store = new LazyForzaStore(databasePath))
                Assert.AreEqual(LazyForzaStore.CurrentSchemaVersion, store.SchemaVersion);
            using (var database = new WinSqliteDatabase(databasePath))
                database.Execute("UPDATE SchemaVersion SET Version=7;");

            var snapshot = DataBackupService.CreatePreMigrationSnapshotIfNeeded(
                databasePath,
                backupDirectory,
                "1.2.1");
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(File.Exists(snapshot));
            using (var archive = ZipFile.OpenRead(snapshot))
            {
                Assert.IsNotNull(archive.GetEntry("manifest.json"));
                Assert.IsNotNull(archive.GetEntry("lazyforza.db"));
            }

            var manual = Path.Combine(backupDirectory, "manual.lfzbackup");
            File.WriteAllText(manual, "keep");
            for (var index = 0; index < 12; index++)
            {
                var automatic = Path.Combine(
                    backupDirectory,
                    $"auto-test-{index:00}.lfzbackup");
                File.WriteAllText(automatic, index.ToString());
                File.SetLastWriteTimeUtc(automatic, DateTime.UtcNow.AddMinutes(index));
            }

            DataBackupService.RotateAutomaticBackups(backupDirectory);
            Assert.AreEqual(
                DataBackupService.AutomaticBackupRetention,
                Directory.EnumerateFiles(backupDirectory, "auto-*.lfzbackup").Count());
            Assert.IsTrue(File.Exists(manual));
        }
        finally
        {
            DeleteDatabase(databasePath);
            if (Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, true);
        }
    }

    private static ShiftLearningSnapshot VehicleSnapshot()
    {
        var fingerprint = new VehicleProfileFingerprint(
            2038,
            4,
            800,
            2,
            8,
            8_500,
            "g2_250-g3_180",
            "p70_t55_r7200");
        return new ShiftLearningSnapshot(
            LearningState.Ready,
            1,
            0.9,
            fingerprint,
            [new EngineCurveBin(7_000, 30, 350_000, 510, 12, 3, 0.9)],
            [new GearModel(2, 250, 30, 0.9), new GearModel(3, 180, 30, 0.9)],
            [new ShiftTarget(2, 3, 7_600, 7_200, 5_400, 0.85, false)],
            new Dictionary<string, int>(),
            "ready");
    }

    private static (Guid TrackId, Guid LapId) SaveTrackAndLap(LazyForzaStore store)
    {
        var raw = Enumerable.Range(0, 80)
            .Select(index =>
            {
                var angle = index / 79d * Math.PI * 2;
                return new TrackPoint(
                    120 * Math.Cos(angle),
                    0,
                    120 * Math.Sin(angle),
                    0,
                    0,
                    0);
            })
            .ToArray();
        var track = TrackAlgorithms.BuildTemplate("Portable track", raw);
        var sectors = TrackAlgorithms.CreateSectors(track);
        store.SaveTrack(track, sectors);
        var samples = track.Points.Take(24)
            .Select((point, index) => new LapSample(
                point.S,
                index * 0.2,
                35,
                5_500,
                4,
                0.8,
                0,
                0,
                point.X,
                point.Y,
                point.Z,
                new LapDynamics(
                    0.42,
                    new WheelValues(0.1f, 0.2f, 0.3f, 0.4f),
                    new WheelValues(0.02f, 0.03f, 0.04f, 0.05f),
                    new WheelValues(0.15f, 0.2f, 0.25f, 0.3f))))
            .ToArray();
        var lapId = Guid.NewGuid();
        store.SaveLap(new LapRecord(
            lapId,
            track.Id,
            track.Direction,
            TrackAlgorithms.SectorSchemaVersion,
            Guid.NewGuid(),
            new VehicleProfileFingerprint(2038, 4, 800, 2, 8, 8_500, "g", "c"),
            DateTimeOffset.UtcNow,
            72.5,
            true,
            null,
            sectors.Select(sector => new LapSegment(
                sector.Index,
                72.5 / sectors.Count,
                true)).ToArray(),
            samples));
        return (track.Id, lapId);
    }

    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"lazyforza-{Guid.NewGuid():N}{extension}");

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
