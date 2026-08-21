using LazyForza.Analysis;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateEnrollmentDraftStoreTests
{
    [TestMethod]
    public void DraftRoundTripsCandidateRouteAndIdentityData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-draft-store-{Guid.NewGuid():N}");
        try
        {
            var route = Enumerable.Range(0, 121)
                .Select(index =>
                {
                    var angle = index * Math.PI * 2 / 120;
                    return new TrackPoint(80 * Math.Cos(angle), 2, 80 * Math.Sin(angle), 0, 0, 0);
                })
                .ToArray();
            var track = TrackAlgorithms.BuildTemplate("暂存往返", route) with
            {
                TimingKind = TrackTimingKind.EstateGeometry,
                Category = "地产环道"
            };
            var gate = new EstateTimingGate(
                new EstateGatePoint(-8, 2, 0), new EstateGatePoint(8, 2, 0), 0, 1, 0.05, 0.04, 0.1);
            var sectors = TrackAlgorithms.CreateSectors(track, requestedCount: 4);
            var definition = new EstateTrackDefinition(
                track.Id, track.Name, "作者", "share", "rev-2", gate,
                EstateTrackAlgorithms.CreateCheckpoints(track, 4), null,
                60, 0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            var draft = new EstateEnrollmentDraft(
                1,
                DateTimeOffset.UtcNow,
                new EstateEnrollmentRequest(track.Name, "作者", "share", "rev-2", 4),
                EstateCircuitPhase.ValidationFailed,
                [new EstateGatePoint(-8, 2, 0), new EstateGatePoint(8, 2, 0)],
                [new EstateGatePoint(8, 2, 0), new EstateGatePoint(-8, 2, 0)],
                [new EstateGatePoint(0, 2, -8), new EstateGatePoint(0, 2, 8)],
                gate,
                track,
                sectors,
                definition,
                60);

            var store = new EstateEnrollmentDraftStore(root);
            store.Save(draft);
            var loaded = store.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(EstateCircuitPhase.ValidationFailed, loaded.ResumePhase);
            Assert.AreEqual("rev-2", loaded.Enrollment.MapRevision);
            Assert.AreEqual(track.Id, loaded.ActiveTrack?.Id);
            Assert.AreEqual(track.Points.Count, loaded.ActiveTrack?.Points.Count);
            Assert.AreEqual(definition.TrackId, loaded.ActiveDefinition?.TrackId);
            Assert.AreEqual(sectors.Count, loaded.ActiveSectors.Count);
            Assert.AreEqual(gate, loaded.FittedGate);
            store.Delete();
            Assert.IsFalse(store.Exists);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }
}
