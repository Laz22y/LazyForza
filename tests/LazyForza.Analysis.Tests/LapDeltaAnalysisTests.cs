using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class LapDeltaAnalysisTests
{
    private static readonly VehicleProfileFingerprint Car = new(1, 4, 800, 1, 6, 8000, "g", "p");

    [TestMethod]
    public void FourScopesSelectOneRealCompleteLapWithoutFallback()
    {
        var track = TrackAlgorithms.BuildTemplate("Test", [new(0, 0, 0, 0, 1, 0), new(100, 0, 0, 100, 1, 0)]);
        LapSummary Summary(double time, VehicleProfileFingerprint car) => new(Guid.NewGuid(), track.Id, track.Direction,
            TrackAlgorithms.SectorSchemaVersion, Guid.NewGuid(), car, DateTimeOffset.UnixEpoch, time, true, null, [new(0, time, true)])
            { TrackRevision = LapTrackRevision.Create(track) };
        var all = Summary(10, Car with { CarOrdinal = 5, CarClass = 5, PerformanceIndex = 900 });
        var sameClass = Summary(11, Car with { CarOrdinal = 3, PerformanceIndex = 799 });
        var samePi = Summary(12, Car with { CarOrdinal = 2 });
        var sameCar = Summary(13, Car with { CarClass = 3, PerformanceIndex = 700 });
        var invalid = Summary(1, Car) with { IsValid = false };
        var wrongRevision = Summary(2, Car) with { TrackRevision = "old" };
        var wrongDirection = Summary(3, Car) with { Direction = -track.Direction };
        var wrongTrack = Summary(4, Car) with { TrackId = Guid.NewGuid() };
        LapSummary[] laps = [invalid, wrongRevision, wrongDirection, wrongTrack, all, sameClass, samePi, sameCar];
        Assert.AreEqual(all.Id, LapDeltaAnalysis.SelectReference(track, 1, laps, Car, LapDeltaComparison.TrackBest)!.Id);
        Assert.AreEqual(sameClass.Id, LapDeltaAnalysis.SelectReference(track, 1, laps, Car, LapDeltaComparison.ClassBest)!.Id);
        Assert.AreEqual(samePi.Id, LapDeltaAnalysis.SelectReference(track, 1, laps, Car, LapDeltaComparison.PerformanceIndexBest)!.Id);
        Assert.AreEqual(sameCar.Id, LapDeltaAnalysis.SelectReference(track, 1, laps, Car, LapDeltaComparison.CarBest)!.Id);
        Assert.IsNull(LapDeltaAnalysis.SelectReference(track, 1, [all], Car, LapDeltaComparison.CarBest));
        Assert.IsNull(LapDeltaAnalysis.SelectReference(track, 1, [samePi with { Segments = [new(0, double.NaN, true)] }], Car, LapDeltaComparison.ClassBest));
    }

    [TestMethod]
    public void CurveInterpolatesAtDistanceAndDoesNotExtrapolateOrBridgeGaps()
    {
        var curve = LapDeltaCurve.Create(Lap([Sample(10, 1), Sample(20, 1.5), Sample(30, 2), Sample(100, 5), Sample(110, 5.5)]), 120)!;
        Assert.AreEqual(1.25, curve.ElapsedAt(15));
        Assert.AreEqual(5.25, curve.ElapsedAt(105));
        Assert.IsNull(curve.ElapsedAt(0)); Assert.IsNull(curve.ElapsedAt(120));
        Assert.IsNull(curve.ElapsedAt(60)); Assert.IsNull(curve.ElapsedAt(double.NaN));
    }

    [TestMethod]
    public void CurveRejectsRewindAndInvalidSamplesButRetainsStationaryTime()
    {
        Assert.IsNull(LapDeltaCurve.Create(Lap([Sample(10, 1), Sample(0, 2)]), 120));
        Assert.IsNull(LapDeltaCurve.Create(Lap([Sample(10, 1), Sample(20, .5)]), 120));
        Assert.IsNull(LapDeltaCurve.Create(Lap([Sample(10, 1), Sample(double.NaN, 2)]), 120));
        var curve = LapDeltaCurve.Create(Lap([Sample(10, 1), Sample(10, 1.5), Sample(20, 2)]), 120)!;
        Assert.AreEqual(1d, curve.ElapsedAt(10));
        Assert.AreEqual(1.75, curve.ElapsedAt(15));
    }

    private static LapSample Sample(double s, double time) => new(s, time, 20, 5000, 3, 1, 0, 0, s, 0, 0);
    private static LapRecord Lap(LapSample[] samples) => new(Guid.NewGuid(), Guid.NewGuid(), 1,
        TrackAlgorithms.SectorSchemaVersion, Guid.NewGuid(), Car, DateTimeOffset.UnixEpoch, 6, true, null, [new(0, 6, true)], samples);
}
