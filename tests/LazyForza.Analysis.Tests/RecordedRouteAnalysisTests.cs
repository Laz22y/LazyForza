using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class RecordedRouteAnalysisTests
{
    [TestMethod]
    [DataRow(2d, 200d, false)]
    [DataRow(4d, 500d, false)]
    [DataRow(20d, 12d, false)]
    [DataRow(20d, 0d, false)]
    [DataRow(12d, 500d, true)]
    public void ShortOrStationaryCapturesAreDiscardedEvenWhenReferencingALongRoute(double duration, double travel, bool keep)
    {
        var original = Capture();
        var reference = RecordedRouteAnalysis.Build(original, true)!;
        var capture = original with { ReferenceTrack = reference.Track, ReferenceSectors = reference.Sectors,
            Samples = Enumerable.Range(0, 201).Select(i => new LapSample(0, 100 + i / 200d * duration, 40, 5000, 4, 1, 0, 0,
                i / 200d * travel, 0, 0)).ToArray() };
        Assert.AreEqual(keep, RecordedRouteAnalysis.Build(capture, false) is not null);
    }

    [TestMethod]
    public void RouteAssociationSeparatesDirectionElevationAndBranchesAndPreservesTelemetryOnForcedAssignment()
    {
        var source = RecordedRouteAnalysis.Build(Capture(), true)!;
        TrackTemplate Destination(Func<TrackPoint, TrackPoint> transform) => TrackAlgorithms.BuildTemplate("Target",
            source.Track.Points.Select(transform).ToArray(), layoutKind: source.Track.LayoutKind) with { Source = source.Track.Source };
        var near = Destination(point => point with { Z = point.Z + 3 });
        var match = RecordedRouteAssociation.Compare(source.Track, near);
        Assert.IsTrue(match.CanAutoMerge);
        var lap = source.Lap with { Annotation = new("Lap name", "Notes", true, true) };
        var aligned = RecordedRouteAssociation.Reproject(lap, source.Track, near, TrackAlgorithms.CreateSectors(near), match);
        Assert.IsTrue(aligned.IsValid);
        Assert.AreEqual(lap.TotalSeconds, aligned.TotalSeconds);
        Assert.AreEqual(lap.TotalSeconds, aligned.Segments.Sum(segment => segment.TimeSeconds), .001);
        Assert.AreEqual(lap.Annotation, aligned.Annotation);
        Assert.AreEqual(lap.SessionInfo, aligned.SessionInfo);
        foreach (var other in new[] { Destination(point => point with { Y = 30 }),
                     Destination(point => point with { X = 1000 - point.X }),
                     Destination(point => point with { Z = point.X is > 300 and < 700 ? 200 : 0 }),
                     Destination(point => point with { Z = 5000 }) })
        {
            var mismatch = RecordedRouteAssociation.Compare(source.Track, other);
            Assert.IsFalse(mismatch.CanAutoMerge);
            Assert.IsTrue(mismatch.IsSevereMismatch);
            var forced = RecordedRouteAssociation.Reproject(lap, source.Track, other, TrackAlgorithms.CreateSectors(other), mismatch);
            Assert.IsFalse(forced.IsValid);
            Assert.AreEqual("recording-route-mismatch", forced.InvalidReason);
            Assert.IsFalse(forced.Annotation.IsReference);
            Assert.IsTrue(forced.Annotation.IsFavorite);
            CollectionAssert.AreEqual(lap.Samples.ToArray(), forced.Samples.ToArray(), "Forced assignment must preserve the original analysis distance axis.");
            Assert.AreEqual(lap.TrackRevision, forced.TrackRevision);
            CollectionAssert.AreEqual(lap.Samples.Select(sample => (sample.ElapsedSeconds, sample.X, sample.Y, sample.Z, sample.Dynamics)).ToArray(),
                forced.Samples.Select(sample => (sample.ElapsedSeconds, sample.X, sample.Y, sample.Z, sample.Dynamics)).ToArray());
        }
    }

    [TestMethod]
    public void PartialRouteCanMatchTheMiddleOfAnEnrolledRouteWithoutBecomingACompleteLap()
    {
        var full = RecordedRouteAnalysis.Build(Capture(), true)!;
        var capture = Capture() with { StartConfirmed = false, Samples = Capture().Samples.Skip(60).Take(121).ToArray() };
        var partial = RecordedRouteAnalysis.Build(capture, false)!;
        var match = RecordedRouteAssociation.Compare(partial.Track, full.Track, partial: true);
        Assert.IsFalse(match.IsSevereMismatch);
        Assert.IsFalse(match.CanAutoMerge);
        Assert.IsTrue(match.Coverage > .99);
        var aligned = RecordedRouteAssociation.Reproject(partial.Lap, partial.Track, full.Track, full.Sectors, match);
        Assert.IsFalse(aligned.IsValid);
        Assert.IsTrue(aligned.Samples[0].S > 250);
        Assert.IsTrue(aligned.Samples[^1].S > 850);
        Assert.AreEqual(partial.Lap.TotalSeconds, aligned.TotalSeconds);
    }

    [TestMethod]
    [DataRow(false, true, null, true)]
    [DataRow(false, false, null, false)]
    [DataRow(true, true, null, false)]
    [DataRow(false, true, "recording-sample-gap", false)]
    public void OnlyCompleteContinuousLapsQualifyForEnrollment(bool missingStart, bool finish, string? fault, bool expected)
    {
        var result = RecordedRouteAnalysis.Build(Capture() with { StartConfirmed = !missingStart, Fault = fault }, finish)!;
        Assert.AreEqual(expected, result.Lap.IsValid);
        Assert.IsTrue(result.Track.IsRecordOnly);
        Assert.AreEqual(result.Lap.TrackRevision, LapTrackRevision.Create(result.Track));
        Assert.AreEqual(201, result.Lap.Samples.Count);
        if (expected) Assert.AreEqual(result.Lap.TotalSeconds, result.Lap.Segments.Sum(segment => segment.TimeSeconds), .01);
    }

    [TestMethod]
    public void FollowingLapUsesFixedRouteAndTeleportDoesNotBuildAFalseLongTrack()
    {
        var capture = Capture();
        var first = RecordedRouteAnalysis.Build(capture, true)!;
        var changed = capture with { Id = Guid.NewGuid(), ReferenceTrack = first.Track, ReferenceSectors = first.Sectors,
            Samples = capture.Samples.Select((sample, i) => sample with { Z = i == 100 ? 5000 : 5 }).ToArray() };
        var following = RecordedRouteAnalysis.Build(changed, true)!;
        Assert.IsFalse(following.Lap.IsValid);
        Assert.AreEqual(first.Track.Id, following.Track.Id);
        Assert.AreEqual(first.Lap.TrackRevision, following.Lap.TrackRevision);
        Assert.AreEqual(201, following.Lap.Samples.Count);
        Assert.AreEqual(5000, following.Lap.Samples[100].Z);
        var extreme = RecordedRouteAnalysis.Build(changed with { ReferenceTrack = null, ReferenceSectors = null }, true)!;
        Assert.IsTrue(extreme.Track.LengthMeters < 2000);
    }

    [TestMethod]
    public async Task ExchangeCarriesRouteAndRejectsMismatchedGeometry()
    {
        var result = RecordedRouteAnalysis.Build(Capture(), true)!;
        var path = Path.Combine(Path.GetTempPath(), $"recorded-exchange-{Guid.NewGuid():N}.lfzlap");
        try
        {
            await LapAnalysisExchangeFile.WriteAsync(path, result.Track.Id, result.Track.Name, 1, 2, null,
                [result.Lap], CancellationToken.None, result.Track, result.Sectors);
            var loaded = await LapAnalysisExchangeFile.ReadAsync(path, CancellationToken.None);
            Assert.AreEqual(result.Lap.TrackRevision, LapTrackRevision.Create(loaded.Route!));
            Assert.HasCount(result.Sectors.Count, loaded.Sectors!);
            Assert.IsTrue(loaded.Route!.IsRecordOnly);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LapAnalysisExchangeFile.WriteAsync(path,
                result.Track.Id, result.Track.Name, 1, 2, null, [result.Lap], CancellationToken.None,
                result.Track with { UpdatedAt = DateTimeOffset.UtcNow }, result.Sectors));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static RecordedLapCapture Capture() => new(Guid.NewGuid(), Guid.NewGuid(), TelemetrySourceKind.Simulator,
        DateTimeOffset.UnixEpoch, new(1, 4, 800, 2, 8, 8000, "", ""), null, true, null,
        Enumerable.Range(0, 201).Select(i => new LapSample(0, i * .1, 50, 5000, 4, 1, 0, 0, i * 5, 0, 0)).ToArray());
}
