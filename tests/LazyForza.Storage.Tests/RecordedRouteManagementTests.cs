using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class RecordedRouteManagementTests
{
    [TestMethod]
    public void LastLapDeletionRemovesOnlyEmptyRecordedRoutesAndPreservesRecoveryCheckpoints()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var route = fixture.Recorded();
        var saved = route.Track with { Id = Guid.NewGuid(), IsRecordOnly = false };
        store.SaveTrack(saved, TrackAlgorithms.CreateSectors(saved));
        store.DeleteLap(route.Lap.Id);
        Assert.IsNull(store.LoadTrack(route.Track.Id));
        Assert.IsNotNull(store.LoadTrack(saved.Id));
        route = fixture.Recorded();
        store.SavePendingLapCapture(fixture.Capture() with { ReferenceTrack = route.Track, ReferenceSectors = route.Sectors });
        store.DeleteTrackLaps(route.Track.Id);
        Assert.IsNotNull(store.LoadTrack(route.Track.Id));
        store.DeleteRecordedRoute(route.Track.Id);
        Assert.IsNull(store.LoadTrack(route.Track.Id));
        Assert.HasCount(0, store.LoadPendingLapCaptures(null));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.DeleteRecordedRoute(saved.Id));
    }

    [TestMethod]
    public void AssignmentIsAtomicRetainsMetadataAndRejectsStaleRecordSets()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var source = fixture.Recorded();
        store.UpdateLapAnnotation(source.Lap.Id, new("Named lap", "Note", true, true));
        var target = source.Track with { Id = Guid.NewGuid(), IsRecordOnly = false, Name = "Destination" };
        var sectors = TrackAlgorithms.CreateSectors(target);
        store.SaveTrack(target, sectors);
        var lap = store.LoadLap(source.Lap.Id)!;
        var aligned = RecordedRouteAssociation.Reproject(lap, source.Track, target, sectors, RecordedRouteAssociation.Compare(source.Track, target));
        var broken = aligned with { Segments = [aligned.Segments[0], aligned.Segments[0]] };
        Assert.ThrowsExactly<InvalidOperationException>(() => store.MoveRecordedLaps(source.Track, target, [broken]));
        Assert.AreEqual(source.Track.Id, store.LoadLap(lap.Id)!.TrackId);
        Assert.IsNotNull(store.LoadTrack(source.Track.Id));
        var additional = source.Lap with { Id = Guid.NewGuid() };
        store.SaveLap(additional);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.MoveRecordedLaps(source.Track, target, [aligned]));
        store.DeleteLap(additional.Id);
        store.MoveRecordedLaps(source.Track, target, [aligned]);
        Assert.IsNull(store.LoadTrack(source.Track.Id));
        var moved = store.LoadLap(lap.Id)!;
        Assert.AreEqual(target.Id, moved.TrackId);
        Assert.AreEqual(lap.Annotation, moved.Annotation);
        Assert.AreEqual(lap.SessionInfo, moved.SessionInfo);
        Assert.AreEqual(lap.TotalSeconds, moved.TotalSeconds);
        Assert.HasCount(lap.Samples.Count, moved.Samples);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"recorded-management-{Guid.NewGuid():N}.db");
        internal LazyForzaStore Store { get; }
        internal Fixture() => Store = new(path);
        internal RecordedLapCapture Capture() => new(Guid.NewGuid(), Guid.NewGuid(), TelemetrySourceKind.Simulator,
            DateTimeOffset.UnixEpoch, new(1, 4, 800, 2, 8, 8000, "", ""), null, true, null,
            Enumerable.Range(0, 201).Select(i => new LapSample(0, i * .1, 50, 5000, 4, 1, 0, 0, i * 5, 0, 0)).ToArray());
        internal RecordedRouteResult Recorded()
        {
            var capture = Capture(); var result = RecordedRouteAnalysis.Build(capture, true)!;
            Store.SaveRecordedLap(capture, result.Track, result.Sectors, result.Lap); return result;
        }
        public void Dispose()
        {
            Store.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
