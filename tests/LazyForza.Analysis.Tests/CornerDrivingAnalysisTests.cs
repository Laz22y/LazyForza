using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class CornerDrivingAnalysisTests
{
    [TestMethod]
    public void SingleNonFastestLapUsesValidFastestLapFromTheSamePerformanceClass()
    {
        var selected = Summary(100, performanceClass: 5);
        var expectedReference = Summary(94, performanceClass: 5);
        var fasterOtherClass = Summary(80, performanceClass: 6);
        var invalidSameClass = Summary(90, performanceClass: 5, valid: false);

        var plan = LapComparisonPlanner.Resolve(
            selected,
            [selected, expectedReference, fasterOtherClass, invalidSameClass]);

        Assert.AreEqual(SingleLapAnalysisMode.CompareWithClassFastest, plan.Mode);
        Assert.AreEqual(expectedReference.Id, plan.ReferenceLap?.Id);
    }

    [TestMethod]
    public void SingleClassFastestLapUsesPersonalBestAnalysisWithoutAddingAReference()
    {
        var selected = Summary(92, performanceClass: 4);
        var slower = Summary(96, performanceClass: 4);

        var plan = LapComparisonPlanner.Resolve(selected, [slower, selected]);

        Assert.AreEqual(SingleLapAnalysisMode.AnalyzePersonalBest, plan.Mode);
        Assert.IsNull(plan.ReferenceLap);
    }

    [TestMethod]
    public void SingleLapWithoutAValidClassReferenceFallsBackToLightweightAnalysis()
    {
        var selected = Summary(100, performanceClass: 3, valid: false);

        var plan = LapComparisonPlanner.Resolve(selected, [selected]);

        Assert.AreEqual(SingleLapAnalysisMode.AnalyzeWithoutReference, plan.Mode);
        Assert.IsNull(plan.ReferenceLap);
    }

    [TestMethod]
    public void CornerComparisonReportsBrakeThrottleSpeedLineAndLocalTimeDifferences()
    {
        var reference = Record(
            totalSeconds: 20,
            Samples(brakeStart: 175, throttleStart: 280, lineOffset: 0, slower: false));
        var selected = Record(
            totalSeconds: 22,
            Samples(brakeStart: 150, throttleStart: 315, lineOffset: 2.5, slower: true));

        var corners = CornerDrivingAnalyzer.Compare(selected, reference);

        Assert.IsNotEmpty(corners);
        var corner = corners.OrderByDescending(candidate => candidate.TimeLossSeconds).First();
        Assert.IsNotNull(corner.BrakePointDeltaMeters);
        Assert.IsTrue(corner.BrakePointDeltaMeters.Value < -10);
        Assert.IsNotNull(corner.ThrottleRecoveryDeltaSeconds);
        Assert.IsTrue(corner.ThrottleRecoveryDeltaSeconds.Value > 0);
        Assert.IsTrue(corner.MeanLineDeviationMeters > 2);
        Assert.IsTrue(corner.TimeLossSeconds > 0);
        Assert.IsTrue(corner.SelectedExitSpeedKph < corner.ReferenceExitSpeedKph);
    }

    [TestMethod]
    public void PersonalBestAnalysisFindsCoastingAndDelayedThrottleWithoutClaimingGripLoss()
    {
        var lap = Record(
            totalSeconds: 21,
            Samples(brakeStart: 175, throttleStart: 325, lineOffset: 0, slower: true));

        var corners = CornerDrivingAnalyzer.AnalyzePersonalBest(lap);

        Assert.IsNotEmpty(corners);
        var corner = corners.OrderByDescending(candidate => candidate.OpportunityScore).First();
        Assert.IsTrue(corner.CoastingSeconds > 0.3);
        Assert.IsNotNull(corner.ThrottleRecoverySeconds);
        Assert.IsTrue(corner.ThrottleRecoverySeconds.Value > 0.5);
        Assert.IsTrue(corner.OpportunityScore > 0);
    }

    private static LapSummary Summary(
        double totalSeconds,
        int performanceClass,
        bool valid = true) =>
        new(
            Guid.NewGuid(),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            1,
            TrackAlgorithms.SectorSchemaVersion,
            Guid.NewGuid(),
            Fingerprint(performanceClass),
            DateTimeOffset.UtcNow.AddSeconds(totalSeconds),
            totalSeconds,
            valid,
            valid ? null : "invalid",
            []);

    private static LapRecord Record(double totalSeconds, IReadOnlyList<LapSample> samples) =>
        new(
            Guid.NewGuid(),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            1,
            TrackAlgorithms.SectorSchemaVersion,
            Guid.NewGuid(),
            Fingerprint(5),
            DateTimeOffset.UtcNow,
            totalSeconds,
            true,
            null,
            [],
            samples);

    private static VehicleProfileFingerprint Fingerprint(int performanceClass) =>
        new(123, performanceClass, 850, 2, 8, 8_000, "g", "c");

    private static IReadOnlyList<LapSample> Samples(
        double brakeStart,
        double throttleStart,
        double lineOffset,
        bool slower)
    {
        var samples = new List<LapSample>();
        var elapsed = 0d;
        for (var progress = 0d; progress <= 500; progress += 5)
        {
            var referenceSpeed = progress switch
            {
                < 150 => 34,
                <= 250 => 34 - (progress - 150) * 0.18,
                <= 360 => 16 + (progress - 250) * 0.16,
                _ => 33.6
            };
            var speed = slower && progress is >= 145 and <= 370
                ? Math.Max(10, referenceSpeed - 2.2)
                : referenceSpeed;
            if (samples.Count > 0) elapsed += 5 / Math.Max(1, speed);
            var brake = progress >= brakeStart && progress <= 250 ? 0.8 : 0;
            var accel = progress < brakeStart
                ? 0.85
                : progress >= throttleStart
                    ? 0.9
                    : 0;
            var gear = slower
                ? progress switch
                {
                    < 210 => (byte)4,
                    < 305 => (byte)3,
                    _ => (byte)4
                }
                : (byte)4;
            samples.Add(new LapSample(
                progress,
                elapsed,
                speed,
                5_500,
                gear,
                accel,
                brake,
                0,
                progress is >= 120 and <= 390 ? lineOffset : 0,
                0,
                progress));
        }

        return samples;
    }
}
