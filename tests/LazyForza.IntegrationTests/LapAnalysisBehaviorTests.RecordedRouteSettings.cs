using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

public sealed partial class LapAnalysisBehaviorTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("false")]
    [DataRow("invalid")]
    public void UnmatchedRecordingDefaultsOffAndCreatesNoCheckpointsOrRecords(string? saved)
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-off-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            if (saved is not null) store.SetAppSetting(LapAnalysisModule.RecordUnmatchedTracksSettingKey, saved);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.IsFalse(module.RecordUnmatchedTracks);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            feed.Drive(1, 1, 239); feed.Send(2, 0);
            Assert.IsTrue(module.CurrentCompetitionSnapshot!.IsCompetitionActive);
            Assert.AreEqual("赛道未识别。", module.CurrentCompetitionSnapshot.Status);
            Assert.IsNull(module.CurrentTrack);
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            Assert.AreEqual(0, store.CountTracks());
            Assert.AreEqual(0, store.CountLaps());
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void DisablingUnmatchedRecordingDiscardsThePartialAndEnablingMidLapStartsNewSamples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-toggle-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            Assert.IsTrue(new LapAnalysisModule(store, TelemetrySourceKind.Simulator).RecordUnmatchedTracks);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 120);
            Assert.HasCount(1, store.LoadPendingLapCaptures("simulator"));
            feed.SendInactive();
            module.SetRecordUnmatchedTracks(false);
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
            Assert.AreEqual("赛道未识别。", module.CurrentCompetitionSnapshot!.Status);
            Assert.IsFalse(new LapAnalysisModule(store, TelemetrySourceKind.Simulator).RecordUnmatchedTracks);
            Assert.AreEqual(0, store.CountLaps());
            feed.Drive(0, 121, 239); feed.Send(1, 0); feed.Drive(1, 1, 80);
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
            module.SetRecordUnmatchedTracks(true);
            feed.Drive(1, 81, 239); feed.Send(2, 0);
            var partial = store.ListTracks().SelectMany(route => store.LoadLapHistory(route.Id)).Single();
            Assert.IsFalse(partial.IsValid);
            Assert.AreEqual("recording-missing-start", partial.InvalidReason);
            var samples = store.LoadLap(partial.Id)!.Samples;
            Assert.AreEqual(Position(81).X, samples[0].X, .01);
            Assert.AreEqual(Position(81).Z, samples[0].Z, .01);
            Assert.AreEqual(15.9, partial.TotalSeconds, .01);
            feed.Drive(2, 1, 239); feed.Send(3, 0);
            Assert.IsTrue(module.VisibleLaps.Single().IsValid);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void DisablingAnActiveRecordedRouteKeepsCompletedHistoryAndStopsFollowingLaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-stop-route-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var route = module.CurrentTrack!;
            var original = module.VisibleLaps.Single();
            feed.Drive(1, 1, 120);
            module.SetRecordUnmatchedTracks(false);
            feed.Drive(1, 121, 239); feed.Send(2, 0);
            feed.Drive(2, 1, 239); feed.Send(3, 0);
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
            Assert.AreEqual(1, store.CountLaps());
            var reopened = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.IsFalse(reopened.RecordUnmatchedTracks);
            reopened.SelectTrack(route.Id);
            Assert.AreEqual(original.Id, reopened.VisibleLaps.Single().Id);
            Assert.IsTrue(reopened.LoadLapDetails([original.Id]).Single().Samples.Count > 100);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task DisablingAfterEnrollmentKeepsRecordingTheNowPersonalTrack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-enrolled-toggle-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var routeId = module.CurrentTrack!.Id;
            feed.Drive(1, 1, 120);
            Assert.IsTrue(await module.EnrollRecordedRouteAsync(routeId));
            module.SetRecordUnmatchedTracks(false);
            feed.Drive(1, 121, 239); feed.Send(2, 0);
            Assert.IsFalse(module.CurrentTrack!.IsRecordOnly);
            Assert.HasCount(2, store.LoadLapHistory(routeId));
            Assert.IsTrue(store.LoadLapHistory(routeId).All(lap => lap.IsValid));
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MatchedTrackRecordingDoesNotDependOnTheUnmatchedTrackOption(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"matched-toggle-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var known = SaveCircleTrack(store);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(enabled);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            feed.Drive(1, 1, 239); feed.Send(2, 0);
            Assert.AreEqual(known.Id, module.CurrentTrack!.Id);
            Assert.IsTrue(store.LoadLapHistory(known.Id).Any(lap => lap.IsValid));
            Assert.IsFalse(store.ListTracks().Any(route => route.IsRecordOnly));
        }
        finally { DeleteDatabase(path); }
    }
}
