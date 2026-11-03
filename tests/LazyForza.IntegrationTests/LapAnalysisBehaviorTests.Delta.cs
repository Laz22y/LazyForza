using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

public sealed partial class LapAnalysisBehaviorTests
{
    [TestMethod]
    public void EveryDeltaScopeSelectsItsOwnBestLapInTheLiveModule()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfz-delta-scopes-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path); SaveCircleTrack(store);
            var seedModule = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var seedFeed = new CircleFrameFeed(seedModule);
            seedFeed.Send(0, 20, raceTimeOverride: 2.1f); seedFeed.Send(0, 0, raceTimeOverride: 2.2f);
            seedFeed.Drive(0, 1, 239); seedFeed.Send(1, 0);
            var baseline = seedModule.LoadLapDetails([seedModule.VisibleLaps.Single().Id]).Single();
            var definitions = new[]
            {
                (LapDeltaComparison.TrackBest, 5, 5, 900, 19d),
                (LapDeltaComparison.ClassBest, 3, 4, 799, 20d),
                (LapDeltaComparison.PerformanceIndexBest, 2, 4, 800, 21d),
                (LapDeltaComparison.CarBest, 1, 3, 700, 22d)
            };
            var ids = new Dictionary<LapDeltaComparison, Guid>();
            foreach (var (kind, ordinal, carClass, pi, total) in definitions)
            {
                var ratio = total / baseline.TotalSeconds;
                var lap = baseline with
                {
                    Id = Guid.NewGuid(), TotalSeconds = total,
                    Vehicle = baseline.Vehicle with { CarOrdinal = ordinal, CarClass = carClass, PerformanceIndex = pi },
                    Segments = baseline.Segments.Select(segment => segment with { TimeSeconds = segment.TimeSeconds * ratio }).ToArray(),
                    Samples = baseline.Samples.Select(sample => sample with { ElapsedSeconds = sample.ElapsedSeconds * ratio }).ToArray()
                };
                ids[kind] = lap.Id; store.SaveLap(lap);
            }
            foreach (var (kind, id) in ids)
            {
                var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
                module.SetDeltaOptions(new(true, kind));
                var feed = new CircleFrameFeed(module);
                feed.Send(0, 20, raceTimeOverride: 2.1f); feed.Send(0, 0, raceTimeOverride: 2.2f); feed.Drive(0, 1, 100);
                Assert.AreEqual(id, Hud(module).DeltaReferenceLapId, kind.ToString());
                Assert.IsTrue(Hud(module).ContinuousDeltaSeconds > 0, kind.ToString());
            }
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void ContinuousDeltaUpdatesBetweenSectorsAndReferenceScopeWaitsUntilNextLap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfz-delta-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.AreEqual(new LapDeltaOptions(), module.DeltaOptions);
            var feed = new CircleFrameFeed(module);
            feed.Send(0, 20, raceTimeOverride: 2.1f); feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 239); feed.Send(1, 0);
            var first = module.VisibleLaps.Single();
            feed.Drive(1, 1, 40);
            module.SetDeltaOptions(new(true, LapDeltaComparison.CarBest));
            Assert.IsTrue(module.DeltaComparisonPending);
            feed.Send(1, 41);
            Assert.AreEqual(first.Id, Hud(module).DeltaReferenceLapId);
            Assert.AreEqual(LapDeltaComparison.ClassBest, Hud(module).ActiveDeltaComparison);
            Assert.IsNotNull(Hud(module).ContinuousDeltaSeconds);
            var sector = Hud(module).CurrentSector;
            var initial = Hud(module).ContinuousDeltaSeconds!.Value;
            feed.Send(1, 42, currentLapOverride: 4.3f);
            Assert.AreEqual(sector, Hud(module).CurrentSector);
            Assert.IsTrue(Hud(module).ContinuousDeltaSeconds > initial + .05);
            feed.Send(1, 43);
            feed.Drive(1, 44, 239); feed.Send(2, 0); feed.Send(2, 1);
            Assert.IsFalse(module.DeltaComparisonPending);
            Assert.AreEqual(LapDeltaComparison.CarBest, Hud(module).ActiveDeltaComparison);
            Assert.IsNotNull(Hud(module).CumulativeHistoricalDeltaSeconds, "Completed-lap split remains available beside the live delta.");
            module.SetDeltaOptions(module.DeltaOptions with { Continuous = false });
            Assert.IsFalse(Hud(module).ContinuousDeltaEnabled);
            var restored = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.AreEqual(module.DeltaOptions, restored.DeltaOptions);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void MissingReferencePauseRewindAndPacketGapNeverInventLiveDelta()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfz-delta-gaps-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path); SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetDeltaOptions(new(true));
            var feed = new CircleFrameFeed(module);
            feed.Send(0, 20, raceTimeOverride: 2.1f); feed.Send(0, 0, raceTimeOverride: 2.2f);
            feed.Drive(0, 1, 100); Assert.IsNull(Hud(module).ContinuousDeltaSeconds);
            feed.Drive(0, 101, 239); feed.Send(1, 0); feed.Drive(1, 1, 50);
            Assert.IsNotNull(Hud(module).ContinuousDeltaSeconds);
            feed.SendInactive(); Assert.IsNull(Hud(module).ContinuousDeltaSeconds);
            feed.Send(1, 51); Assert.IsNotNull(Hud(module).ContinuousDeltaSeconds);
            feed.Send(1, 30); Assert.IsNull(Hud(module).ContinuousDeltaSeconds);
            feed.Drive(1, 31, 239); feed.Send(2, 0); feed.Drive(2, 1, 50);
            Assert.IsNotNull(Hud(module).ContinuousDeltaSeconds);
            feed.Send(2, 80); Assert.IsNull(Hud(module).ContinuousDeltaSeconds);
            feed.Send(2, 81); Assert.IsNull(Hud(module).ContinuousDeltaSeconds);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void UnreadableReferenceSamplesDoNotInterruptLapRecording()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfz-delta-broken-reference-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path); SaveCircleTrack(store);
            var seed = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            var seedFeed = new CircleFrameFeed(seed);
            seedFeed.Send(0, 20, raceTimeOverride: 2.1f); seedFeed.Send(0, 0, raceTimeOverride: 2.2f);
            seedFeed.Drive(0, 1, 239); seedFeed.Send(1, 0);
            var reference = seed.VisibleLaps.Single();
            var database = typeof(LazyForzaStore).GetField("database", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            database.GetType().GetMethod("Execute")!.Invoke(database, [$"UPDATE LapSamples SET Gear=256 WHERE LapId='{reference.Id}';"]);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetDeltaOptions(new(true));
            var feed = new CircleFrameFeed(module);
            feed.Send(0, 20, raceTimeOverride: 2.1f); feed.Send(0, 0, raceTimeOverride: 2.2f); feed.Drive(0, 1, 100);
            Assert.AreEqual(reference.Id, Hud(module).DeltaReferenceLapId);
            Assert.IsNull(Hud(module).ContinuousDeltaSeconds);
            feed.Drive(0, 101, 239); feed.Send(1, 0);
            Assert.IsTrue(module.VisibleLaps.Any(lap => lap.Id != reference.Id && lap.IsValid));
            Assert.IsNotNull(Hud(module).CumulativeHistoricalDeltaSeconds);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void UnlistedRouteCanProvideContinuousDeltaOnItsNextLap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfz-delta-unlisted-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            module.SetDeltaOptions(new(true));
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0); feed.Drive(1, 1, 50);
            Assert.IsTrue(module.CurrentTrack!.IsRecordOnly);
            Assert.IsNotNull(Hud(module).ContinuousDeltaSeconds);
        }
        finally { DeleteDatabase(path); }
    }
}
