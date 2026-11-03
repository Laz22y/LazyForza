using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

public sealed partial class LapAnalysisBehaviorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnmatchedPointToPointRequiresFinishEvidenceAndKeepsDataAcrossPause(bool finish)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-recorded-p2p-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new PointToPointFrameFeed(module);
            feed.Drive(0, 100);
            for (var i = 0; i < 40; i++) feed.SendInactive();
            Assert.AreEqual(0, store.CountLaps());
            feed.Drive(101, 199);
            feed.Send(200, lastLapOverride: finish ? 20 : 0);
            for (var i = 0; i <= 20; i++) feed.SendFreeRoam();
            var route = store.ListTracks("simulator").Single();
            Assert.IsTrue(route.IsRecordOnly);
            var lap = store.LoadLapHistory(route.Id).Single();
            Assert.AreEqual(finish, lap.IsValid);
            Assert.AreEqual(TrackLayoutKind.PointToPoint, route.LayoutKind);
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void UnmatchedRewindAndMissingFramesKeepIncompleteRecordsOutsideBests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-recorded-rewind-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 120);
            feed.Send(0, 70);
            feed.Drive(0, 71, 100);
            feed.Drive(0, 130, 239);
            feed.Send(1, 0);
            var laps = store.ListTracks("simulator").SelectMany(route => store.LoadLapHistory(route.Id)).ToArray();
            Assert.HasCount(2, laps);
            Assert.IsTrue(laps.All(lap => !lap.IsValid));
            Assert.IsTrue(laps.Any(lap => lap.InvalidReason == "recording-rewind"));
            Assert.IsTrue(laps.Any(lap => lap.InvalidReason == "recording-sample-gap"));
            Assert.AreEqual(0, store.CountTracks(TrackCatalogKind.UserCustom));
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task UnmatchedRouteKeepsFirstAndFollowingLapsWithoutEnrollment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-recorded-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            SaveCircleTrack(store, "Elsewhere", new Vector3F(5000, 0, 5000));
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239);
            feed.Send(1, 0);
            Assert.IsNotNull(module.CurrentTrack);
            Assert.IsTrue(module.CurrentTrack.IsRecordOnly);
            var first = module.VisibleLaps.Single();
            Assert.IsTrue(first.IsValid, first.InvalidReason);
            Assert.AreEqual(24, first.TotalSeconds, 0.01);
            Assert.AreEqual(1, store.CountTracks(TrackCatalogKind.UserCustom));
            feed.Drive(1, 1, 239);
            feed.Send(2, 0);
            Assert.HasCount(2, module.VisibleLaps);
            Assert.IsTrue(module.VisibleLaps.All(lap => lap.IsValid), string.Join(",", module.VisibleLaps.Select(lap => lap.InvalidReason)));
            Assert.IsTrue(module.VisibleLaps.All(lap => lap.TrackRevision == first.TrackRevision));
            var before = store.LoadLapsByIds([first.Id]).Single();
            Assert.IsTrue(await module.EnrollRecordedRouteAsync(first.TrackId));
            var enrolled = store.LoadTrack(first.TrackId)!.Value.Track;
            Assert.IsFalse(enrolled.IsRecordOnly);
            Assert.AreEqual(first.TrackRevision, LapTrackRevision.Create(enrolled));
            Assert.AreEqual(before.Id, store.LoadLapsByIds([first.Id]).Single().Id);
            store.RenameTrack(first.TrackId, "My route");
            Assert.AreEqual(first.TrackRevision, LapTrackRevision.Create(store.LoadTrack(first.TrackId)!.Value.Track));
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void UnmatchedPartialIsRecoveredAfterRestartAndCanBeSelectedForAnalysis()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-recorded-recovery-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 40, 160);
            Assert.HasCount(1, store.LoadPendingLapCaptures("simulator"));
            var recovered = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.HasCount(0, store.LoadPendingLapCaptures("simulator"));
            var route = store.ListTracks("simulator").Single();
            Assert.IsTrue(route.IsRecordOnly);
            recovered.SelectTrack(route.Id);
            var lap = recovered.VisibleLaps.Single();
            Assert.IsFalse(lap.IsValid);
            Assert.IsTrue(lap.InvalidReason!.StartsWith("recording-", StringComparison.Ordinal));
            Assert.IsTrue(recovered.LoadLapDetails([lap.Id]).Single().Samples.Count > 20);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void UnmatchedExitRetainsPartialFollowingACompleteLapOnTheSameRoute()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-recorded-exit-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239);
            feed.Send(1, 0);
            var routeId = module.CurrentTrack!.Id;
            feed.Drive(1, 1, 120);
            for (var i = 0; i <= 20; i++) feed.SendFreeRoam();
            var laps = store.LoadLapsByIds(store.LoadLapHistory(routeId).Select(lap => lap.Id).ToArray());
            Assert.HasCount(2, laps);
            Assert.HasCount(1, laps.Where(lap => lap.IsValid));
            Assert.AreEqual("recording-missing-finish", laps.Single(lap => !lap.IsValid).InvalidReason);
            Assert.IsTrue(laps.All(lap => lap.Samples.Count > 20));
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task RepeatedUnlistedRacesReuseTheRouteAcrossRestartAndCanMoveOrDeleteRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-repeated-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var source = module.CurrentTrack!;
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            Assert.AreEqual(source.Id, module.CurrentTrack!.Id);
            Assert.HasCount(2, store.LoadLapHistory(source.Id));
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            module = new(store, TelemetrySourceKind.Simulator);
            feed = new(module); feed.Drive(0, 0, 239); feed.Send(1, 0);
            Assert.AreEqual(source.Id, module.CurrentTrack!.Id);
            Assert.HasCount(3, store.LoadLapHistory(source.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => module.DeleteRecordedRouteAsync(source.Id));
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            var target = source with { Id = Guid.NewGuid(), IsRecordOnly = false, Name = "Saved event" };
            store.SaveTrack(target, LazyForza.Analysis.TrackAlgorithms.CreateSectors(target));
            var ids = store.LoadLapHistory(source.Id).Select(lap => lap.Id).ToArray();
            await module.AssociateRecordedRouteAsync(source.Id, target.Id, false);
            Assert.IsNull(store.LoadTrack(source.Id));
            Assert.HasCount(3, store.LoadLapHistory(target.Id));
            CollectionAssert.AreEquivalent(ids, store.LoadLapHistory(target.Id).Select(lap => lap.Id).ToArray());
            Assert.IsTrue(store.LoadLapHistory(target.Id).All(lap => lap.IsValid));
            Assert.AreEqual(target.Id, module.CurrentTrack!.Id);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task ForcedAssignmentRequiresConsentAndNeverAddsAnUnrelatedBestLap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-forced-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var source = module.CurrentTrack!;
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            var target = SaveCircleTrack(store, "Another event", new Vector3F(5000, 0, 5000));
            await Assert.ThrowsAsync<InvalidOperationException>(() => module.AssociateRecordedRouteAsync(source.Id, target.Id, false));
            Assert.AreEqual(1, store.CountLaps(source.Id));
            await module.AssociateRecordedRouteAsync(source.Id, target.Id, true);
            Assert.IsNull(store.LoadTrack(source.Id));
            Assert.IsFalse(store.LoadLapHistory(target.Id).Single().IsValid);
            Assert.AreEqual("recording-route-mismatch", store.LoadLapHistory(target.Id).Single().InvalidReason);
            Assert.IsTrue(store.LoadLap(store.LoadLapHistory(target.Id).Single().Id)!.Samples[^1].S > 100);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task ShortUnmatchedFragmentsAreNotSavedAndExplicitDeleteRemovesTheRoute()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-short-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 40);
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            Assert.AreEqual(0, store.CountLaps()); Assert.AreEqual(0, store.CountTracks());
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var route = module.CurrentTrack!;
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            await module.DeleteRecordedRouteAsync(route.Id);
            Assert.IsNull(store.LoadTrack(route.Id)); Assert.AreEqual(0, store.CountLaps());
            Assert.IsNull(module.CurrentTrack);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void ExistingDuplicateRecordedRoutesMergeAtStartupWithoutLosingTheirSessions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recorded-existing-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            module.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(module);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var original = store.LoadLap(module.VisibleLaps.Single().Id)!;
            for (var i = 0; i <= 21; i++) feed.SendFreeRoam();
            var capture = new RecordedLapCapture(Guid.NewGuid(), Guid.NewGuid(), TelemetrySourceKind.Simulator,
                original.StartedAt.AddMinutes(5), original.Vehicle, original.PlayerCode, true, null,
                original.Samples.Select(sample => sample with { X = sample.X + 2 }).ToArray());
            var duplicate = LazyForza.Analysis.RecordedRouteAnalysis.Build(capture, true)!;
            Assert.IsTrue(duplicate.Lap.IsValid);
            store.SaveRecordedLap(capture, duplicate.Track, duplicate.Sectors, duplicate.Lap);
            store.UpdateLapAnnotation(duplicate.Lap.Id, new("Second race", "Keep", true, false));
            Assert.AreEqual(2, store.ListTracks().Count);
            _ = new LapAnalysisModule(store, TelemetrySourceKind.Simulator);
            Assert.AreEqual(1, store.ListTracks().Count);
            var moved = store.LoadLap(duplicate.Lap.Id)!;
            Assert.AreEqual(original.TrackId, moved.TrackId);
            Assert.AreEqual(capture.SessionId, moved.SessionId);
            Assert.AreNotEqual(original.SessionId, moved.SessionId);
            Assert.AreEqual("Second race", moved.Annotation.Name);
            Assert.IsTrue(moved.Annotation.IsFavorite);
            Assert.AreEqual(duplicate.Lap.TotalSeconds, moved.TotalSeconds);
            CollectionAssert.AreEqual(duplicate.Lap.Samples.Select(sample => (sample.X, sample.Y, sample.Z, sample.ElapsedSeconds)).ToArray(),
                moved.Samples.Select(sample => (sample.X, sample.Y, sample.Z, sample.ElapsedSeconds)).ToArray());
        }
        finally { DeleteDatabase(path); }
    }
}
