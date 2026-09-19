using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class LapLibraryTests
{
    [TestMethod]
    public void Schema14UpgradeAnnotationsCapacityAndBackupRoundTripWithoutChangingTelemetry()
    {
        InDirectory(directory =>
        {
            var path = Path.Combine(directory, "source.db");
            var (track, lap) = Fixture();
            LapSample[] persistedSamples;
            using (var store = new LazyForzaStore(path))
            {
                store.SaveTrack(track, TrackAlgorithms.CreateSectors(track)); store.SaveLap(lap);
                persistedSamples = store.LoadLap(lap.Id)!.Samples.ToArray();
            }
            using (var database = new WinSqliteDatabase(path))
                database.Execute("ALTER TABLE Laps DROP COLUMN Annotation; UPDATE SchemaVersion SET Version=14;");
            var annotation = new LapAnnotation("  雨天练习 '  ", "制动点\n第二段慢一些", true, true);
            using (var store = new LazyForzaStore(path))
            {
                Assert.AreEqual(15, store.SchemaVersion);
                Assert.AreEqual(new LapAnnotation(), store.LoadLap(lap.Id)!.Annotation);
                store.UpdateLapAnnotation(lap.Id, annotation);
                store.SetLapCapacity(1500);
            }
            using (var store = new LazyForzaStore(path))
            {
                Assert.AreEqual(1500, store.LapCapacity);
                var loaded = store.LoadLap(lap.Id)!;
                Assert.AreEqual(LapAnnotation.Normalize(annotation), loaded.Annotation);
                CollectionAssert.AreEqual(persistedSamples, loaded.Samples.ToArray());
                Assert.AreEqual(loaded.Annotation, store.LoadRecentLapSummaries().Single().Annotation);
                Assert.AreEqual(loaded.Annotation, store.LoadLapHistory(track.Id).Single().Annotation);
                new DataBackupService(store, "test").Create(Path.Combine(directory, "backup.lfzbackup"), new BackupSelection());
            }
            using var restored = new LazyForzaStore(Path.Combine(directory, "restored.db"));
            new DataBackupService(restored, "test").Import(Path.Combine(directory, "backup.lfzbackup"), BackupImportMode.Merge);
            Assert.AreEqual(LapAnnotation.Normalize(annotation), restored.LoadLap(lap.Id)!.Annotation);
            Assert.AreEqual(1500, restored.LapCapacity);
        });
    }

    [TestMethod]
    public void ReferenceReplacementIsScopedAndAtomicAndInvalidLapCannotBecomeReference()
    {
        InDirectory(directory =>
        {
            var path = Path.Combine(directory, "data.db");
            using var store = new LazyForzaStore(path);
            var (track, lap) = Fixture(); store.SaveTrack(track, TrackAlgorithms.CreateSectors(track)); store.SaveLap(lap);
            var other = lap with { Id = Guid.NewGuid(), TotalSeconds = 110 };
            var differentCar = lap with { Id = Guid.NewGuid(), Vehicle = lap.Vehicle with { CarOrdinal = 20 } };
            var invalid = lap with { Id = Guid.NewGuid(), IsValid = false };
            foreach (var entry in new[] { other, differentCar, invalid }) store.SaveLap(entry);
            store.UpdateLapAnnotation(lap.Id, new(IsFavorite: true, IsReference: true));
            store.UpdateLapAnnotation(differentCar.Id, new(IsReference: true));
            using var raw = new WinSqliteDatabase(path);
            raw.Execute($"CREATE TRIGGER prevent_pin BEFORE UPDATE ON Laps WHEN NEW.Id='{other.Id}' BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
            Assert.ThrowsExactly<InvalidOperationException>(() => store.UpdateLapAnnotation(other.Id, new(IsReference: true)));
            Assert.IsTrue(store.LoadLap(lap.Id)!.Annotation.IsReference, "An interrupted replacement must roll back the previous unpin.");
            raw.Execute("DROP TRIGGER prevent_pin;");
            store.UpdateLapAnnotation(other.Id, new(IsReference: true));
            Assert.IsFalse(store.LoadLap(lap.Id)!.Annotation.IsReference);
            Assert.IsTrue(store.LoadLap(lap.Id)!.Annotation.IsFavorite);
            Assert.IsTrue(store.LoadLap(differentCar.Id)!.Annotation.IsReference);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.UpdateLapAnnotation(invalid.Id, new(IsReference: true)));
        });
    }

    [TestMethod]
    public void RetentionProtectsFavoritesReferencesAndObservedVehicleRepresentativesWithWholeSessions()
    {
        var (_, lap) = Fixture();
        LapSummary At(int number, VehicleProfileFingerprint? vehicle = null) => LapSummary.FromRecord(lap with
        { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), StartedAt = lap.StartedAt.AddMinutes(number), TotalSeconds = 100 + number, Vehicle = vehicle ?? lap.Vehicle });
        var best = At(0);
        var disposable = At(1);
        var car = At(2, lap.Vehicle with { CarOrdinal = 9 });
        var pi = At(3, lap.Vehicle with { PerformanceIndex = 899 });
        var drive = At(4, lap.Vehicle with { DrivetrainType = 1 });
        var gears = At(5, lap.Vehicle with { GearSlopeSignature = "g3_200" });
        var power = At(6, lap.Vehicle with { CurveSignature = "p600000_t500_r7000" });
        var unresolved = At(7, lap.Vehicle with { GearSlopeSignature = "learning", CurveSignature = "learning" });
        var favorite = At(8) with { IsValid = false, Annotation = new(IsFavorite: true) };
        var sibling = At(9) with { SessionId = favorite.SessionId };
        var reference = At(10) with { Annotation = new(IsReference: true) };
        var newest = At(11);
        LapSummary[] laps = [best, disposable, car, pi, drive, gears, power, unresolved, favorite, sibling, reference, newest];
        var keep = LazyForzaStore.SelectRetainedLapIds(laps, 2);
        Assert.IsFalse(keep.Contains(disposable.Id));
        Assert.IsTrue(laps.Where(item => item != disposable).All(item => keep.Contains(item.Id)));
        Assert.IsTrue(keep.Count > 2, "Protection must win over the soft lap target.");
    }

    [TestMethod]
    public void ConfigurableLapTargetUsesLapsRatherThanSessionsAndReportsAllocatedAndEstimatedBytes()
    {
        InDirectory(directory =>
        {
            using var store = new LazyForzaStore(Path.Combine(directory, "data.db"));
            var (track, lap) = Fixture(); store.SaveTrack(track, TrackAlgorithms.CreateSectors(track));
            Assert.AreEqual(500, store.LapCapacity);
            store.SetLapCapacity(80);
            var favoriteId = Guid.NewGuid(); var referenceId = Guid.NewGuid();
            for (var i = 0; i < 85; i++) store.SaveLap(lap with
            {
                Id = i == 1 ? favoriteId : i == 2 ? referenceId : Guid.NewGuid(), SessionId = Guid.NewGuid(),
                StartedAt = lap.StartedAt.AddMinutes(i), TotalSeconds = 100 + i,
                Annotation = new(IsFavorite: i == 1, IsReference: i == 2)
            });
            Assert.AreEqual(80, store.CountLaps(track.Id));
            var usage = store.GetLapStorageUsage();
            Assert.AreEqual(80, usage.Laps); Assert.IsTrue(usage.EstimatedLapBytes > 0);
            Assert.IsTrue(usage.DatabaseBytes >= usage.ReusableBytes);
            Assert.IsTrue(usage.DatabaseBytes > 0);
            store.SetLapCapacity(3);
            Assert.AreEqual(80, store.CountLaps(track.Id), "Editing a target alone must not delete user data.");
            store.PruneTrackLaps(track.Id);
            Assert.AreEqual(4, store.CountLaps(track.Id), "Favorite, reference, representative and newest laps override a smaller budget.");
            Assert.IsNotNull(store.LoadLap(favoriteId)); Assert.IsNotNull(store.LoadLap(referenceId));
            Assert.IsTrue(store.GetLapStorageUsage().EstimatedLapBytes < usage.EstimatedLapBytes);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.SetLapCapacity(0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.SetLapCapacity(10001));
        });
    }

    private static (TrackTemplate Track, LapRecord Lap) Fixture()
    {
        var track = TrackAlgorithms.BuildTemplate("Library", Enumerable.Range(0, 40).Select(i => new TrackPoint(i * 5, 0, 0, 0, 1, 0)).ToArray());
        return (track, new LapRecord(Guid.NewGuid(), track.Id, 1, TrackAlgorithms.SectorSchemaVersion, Guid.NewGuid(),
            new(1, 5, 850, 2, 8, 8000, "g3_100", "p300000_t500_r7000"), DateTimeOffset.UnixEpoch, 100, true, null,
            [new(0, 100, true)], Enumerable.Range(0, 25).Select(i => new LapSample(i * 5, i * .1, 50, 5000, 3, .8, 0, 0, i * 5, 0, 0)).ToArray())
            { TrackRevision = LapTrackRevision.Create(track) });
    }

    private static void InDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lap-library-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
