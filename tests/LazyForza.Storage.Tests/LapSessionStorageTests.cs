using LazyForza.Analysis;
using LazyForza.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class LapSessionStorageTests
{
    [TestMethod]
    public void UpgradeAndBackupPreserveSessionAndAnEntireLongRace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lap-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.db");
        var track = TrackAlgorithms.BuildTemplate("Race", Enumerable.Range(0, 40)
            .Select(i => new TrackPoint(i * 5, 0, 0, 0, 1, 0)).ToArray());
        var session = new LapSessionInfo(Guid.NewGuid(), LapSessionKind.EstateRace, "Saturday race");
        var lap = new LapRecord(Guid.NewGuid(), track.Id, 1, 2, session.Id,
            new VehicleProfileFingerprint(1, 5, 850, 2, 8, 8000, "g", "c"),
            DateTimeOffset.UnixEpoch, 100, true, null, [new LapSegment(0, 100, true)],
            [new LapSample(0, 0, 30, 5000, 3, 1, 0, 0, 0, 0, 0)]) { SessionInfo = session };
        try
        {
            using (var store = new LazyForzaStore(path))
            {
                store.SaveTrack(track, TrackAlgorithms.CreateSectors(track));
                store.SaveLap(lap with { SessionInfo = null });
                store.SetAppSetting("kept", "yes");
            }
            using (var raw = new WinSqliteDatabase(path))
                raw.Execute("ALTER TABLE Laps DROP COLUMN SessionInfo; UPDATE SchemaVersion SET Version=13;");
            using (var store = new LazyForzaStore(path))
            {
                Assert.AreEqual(14, store.SchemaVersion);
                Assert.IsNull(store.LoadLap(lap.Id)!.SessionInfo);
                Assert.AreEqual("yes", store.GetAppSetting("kept"));
                for (var i = 1; i <= 70; i++)
                    store.SaveLap(lap with { Id = Guid.NewGuid(), StartedAt = lap.StartedAt.AddMinutes(i * 2) });
                Assert.HasCount(71, store.LoadLapHistory(track.Id));
                Assert.AreEqual(session, store.LoadLapHistory(track.Id)[^1].SessionInfo);
                Assert.HasCount(1, store.LoadLap(lap.Id)!.Samples);
                new DataBackupService(store, "test").Create(Path.Combine(directory, "data.lfzbackup"), new BackupSelection());
            }
            using var restored = new LazyForzaStore(Path.Combine(directory, "restored.db"));
            new DataBackupService(restored, "test").Import(Path.Combine(directory, "data.lfzbackup"), BackupImportMode.Merge);
            Assert.HasCount(71, restored.LoadLapHistory(track.Id));
            Assert.AreEqual(session, restored.LoadLapHistory(track.Id)[^1].SessionInfo);
            Assert.AreEqual(1, restored.LoadLap(restored.LoadLapHistory(track.Id)[^1].Id)!.Samples.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void RetentionRemovesWholeSessionsAndProtectsTheWholeBestSession()
    {
        var track = Guid.NewGuid();
        var laps = Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 3).Select(j =>
            new LapSummary(Guid.NewGuid(), track, 1, 2, new Guid(i + 1, 0, 0, new byte[8]),
                new VehicleProfileFingerprint(1, 5, 850, 2, 8, 8000, "g", "c"),
                DateTimeOffset.UnixEpoch.AddMinutes(i * 10 + j), 100 + i + j, true, null, []))).ToArray();
        var retained = LazyForzaStore.SelectRetainedLapIds(laps, 2);
        Assert.HasCount(6, retained);
        Assert.IsTrue(laps.Take(3).Concat(laps.Skip(9)).All(lap => retained.Contains(lap.Id)));
    }
}
