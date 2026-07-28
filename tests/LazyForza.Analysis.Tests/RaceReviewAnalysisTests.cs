using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class RaceReviewAnalysisTests
{
    [TestMethod]
    public void ReviewCalculatesConsistencyTheoreticalBestAndTrend()
    {
        var session = Guid.NewGuid();
        var laps = new[]
        {
            Lap(session, 100.0, [31.0, 34.0, 35.0], 0),
            Lap(session, 98.7, [30.4, 33.4, 34.9], 1),
            Lap(session, 99.1, [30.7, 33.1, 35.3], 2)
        };

        var review = RaceReviewAnalyzer.Analyze(laps);

        Assert.AreEqual(3, review.ValidLaps);
        Assert.AreEqual(98.7, review.BestLapSeconds!.Value, 0.0001);
        Assert.AreEqual(98.4, review.TheoreticalBestSeconds!.Value, 0.0001);
        Assert.AreEqual(0.3, review.TheoreticalGainSeconds!.Value, 0.0001);
        Assert.AreEqual(-0.9, review.TrendSeconds!.Value, 0.0001);
        Assert.HasCount(3, review.Sectors);
        Assert.IsNotEmpty(review.Findings);
    }

    [TestMethod]
    public void ReviewExcludesInvalidLapsAndExplainsSmallSamples()
    {
        var session = Guid.NewGuid();
        var laps = new[]
        {
            Lap(session, 100, [32.0, 33.0, 35.0], 0),
            Lap(session, 90, [29.0, 30.0, 31.0], 1, valid: false)
        };

        var review = RaceReviewAnalyzer.Analyze(laps);

        Assert.AreEqual(2, review.TotalLaps);
        Assert.AreEqual(1, review.ValidLaps);
        Assert.AreEqual(100, review.BestLapSeconds);
        Assert.IsTrue(review.Findings.Any(item => item.Contains("少于 3 圈", StringComparison.Ordinal)));
        Assert.IsTrue(review.Findings.Any(item => item.Contains("未计入", StringComparison.Ordinal)));
    }

    private static LapSummary Lap(
        Guid session,
        double total,
        IReadOnlyList<double> segments,
        int offsetMinutes,
        bool valid = true) =>
        new(
            Guid.NewGuid(),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            1,
            TrackAlgorithms.SectorSchemaVersion,
            session,
            new VehicleProfileFingerprint(1, 5, 900, 1, 6, 8_000, "g", "c"),
            DateTimeOffset.UtcNow.AddMinutes(offsetMinutes),
            total,
            valid,
            valid ? null : "invalid",
            segments.Select((seconds, index) => new LapSegment(index, seconds, valid)).ToArray());
}
