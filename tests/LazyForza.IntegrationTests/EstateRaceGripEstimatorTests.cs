using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateRaceGripEstimatorTests
{
    [TestMethod]
    public void UsesFirstThreeCompletedLapsAsBaselineAndShowsDeclineForFiveSeconds()
    {
        var estimator = new EstateRaceGripEstimator();
        var now = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        long sequence = 0;
        for (var lap = 0; lap < 3; lap++)
        for (var sample = 0; sample < 40; sample++)
            estimator.Observe(Frame(++sequence, now = now.AddMilliseconds(50), .10f), lap, true);

        estimator.Observe(Frame(++sequence, now = now.AddMilliseconds(50), .10f), 3, true);
        Assert.AreEqual(RaceGripCondition.Unknown, estimator.Current);
        for (var sample = 0; sample < 40; sample++)
            estimator.Observe(Frame(++sequence, now = now.AddMilliseconds(50), .60f), 3, true);

        estimator.Observe(Frame(++sequence, now = now.AddMilliseconds(50), .10f), 4, true);
        Assert.AreEqual(RaceGripCondition.AtLimit, estimator.Current);
        estimator.Observe(Frame(++sequence, now.AddSeconds(5.1), .10f), 4, true);
        Assert.AreEqual(RaceGripCondition.Unknown, estimator.Current);
    }

    private static TelemetryFrame Frame(long sequence, DateTimeOffset arrival, float combinedSlip)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = (uint)(sequence * 50),
            Speed = 25,
            TireCombinedSlip = new WheelValues(combinedSlip, combinedSlip, combinedSlip, combinedSlip),
            TireSlipRatio = new WheelValues(.05f, .05f, .05f, .05f)
        };
        return new TelemetryFrame(
            sequence,
            arrival,
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(90, 55.9, 100, 0.5, 0, 0, 0, 0.5, default),
            ReadOnlyMemory<byte>.Empty);
    }
}
