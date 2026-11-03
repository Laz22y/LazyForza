using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class RecordedRouteStorageTests
{
    [TestMethod]
    public void SchemaFifteenMigrationPreservesExistingTrackAndLaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-migration-{Guid.NewGuid():N}.db");
        try
        {
            Guid id;
            using (var store = new LazyForzaStore(path))
            {
                var track = TrackAlgorithms.BuildTemplate("Existing", Enumerable.Range(0, 100)
                    .Select(i => new TrackPoint(i * 5, 0, 0, 0, 1, 0)).ToArray());
                id = track.Id;
                store.SaveTrack(track, TrackAlgorithms.CreateSectors(track));
                store.SetAppSetting("kept", "yes");
            }
            using (var database = new WinSqliteDatabase(path))
                database.Execute("ALTER TABLE TrackTemplates DROP COLUMN IsRecordOnly; DROP TABLE PendingLapCaptures; UPDATE SchemaVersion SET Version=15;");
            using var migrated = new LazyForzaStore(path);
            Assert.AreEqual(16, migrated.SchemaVersion);
            Assert.IsFalse(migrated.LoadTrack(id)!.Value.Track.IsRecordOnly);
            Assert.AreEqual("yes", migrated.GetAppSetting("kept"));
            Assert.HasCount(0, migrated.LoadPendingLapCaptures(null));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    [TestMethod]
    public void RecordedLapCommitIsAtomicRetryableAndSurvivesLapOnlyBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-storage-{Guid.NewGuid():N}");
        try
        {
            using var store = new LazyForzaStore(path + ".db");
            var capture = new RecordedLapCapture(Guid.NewGuid(), Guid.NewGuid(), TelemetrySourceKind.Simulator,
                DateTimeOffset.UnixEpoch, new(1, 4, 800, 2, 8, 8000, "", ""), null, true, null,
                Enumerable.Range(0, 201).Select(i => new LapSample(0, i * .1, 50, 5000, 4, 1, 0, 0, i * 5, 0, 0)).ToArray())
            { FinishConfirmed = true };
            var result = RecordedRouteAnalysis.Build(capture, true)!;
            var invalid = result.Lap with { Segments = [result.Lap.Segments[0], result.Lap.Segments[0]] };
            Assert.ThrowsExactly<InvalidOperationException>(() => store.SaveRecordedLap(capture, result.Track, result.Sectors, invalid));
            Assert.HasCount(1, store.LoadPendingLapCaptures("simulator"));
            Assert.IsNull(store.LoadTrack(result.Track.Id));
            Assert.AreEqual(0, store.CountLaps());
            store.SaveRecordedLap(capture, result.Track, result.Sectors, result.Lap);
            store.SaveRecordedLap(capture, result.Track, result.Sectors, result.Lap);
            Assert.AreEqual(1, store.CountLaps());
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
            store.SavePendingLapCapture(capture with { Id = Guid.NewGuid(), FinishConfirmed = false });
            new DataBackupService(store, "test").Create(path + ".lfzbackup", new(false, false, true, false));
            using var restored = new LazyForzaStore(path + "-restored.db");
            new DataBackupService(restored, "test").Import(path + ".lfzbackup", BackupImportMode.Merge);
            Assert.IsTrue(restored.LoadTrack(result.Track.Id)!.Value.Track.IsRecordOnly);
            Assert.AreEqual(result.Lap.TrackRevision, restored.LoadLap(result.Lap.Id)!.TrackRevision);
            Assert.HasCount(1, restored.LoadPendingLapCaptures("simulator"));
            Assert.AreEqual(0, restored.CountTracks(TrackCatalogKind.UserCustom));
            Assert.IsTrue(restored.EnrollRecordedRoute(result.Track.Id));
            Assert.AreEqual(1, restored.CountTracks(TrackCatalogKind.UserCustom));
        }
        finally
        {
            foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm", "-restored.db", "-restored.db-wal", "-restored.db-shm", ".lfzbackup" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
