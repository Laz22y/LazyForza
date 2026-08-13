using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateCollisionEvidenceDetectorTests
{
    [TestMethod]
    public void CapturesShortHorizontalVelocityImpulseAndKeepsItLongEnoughForNetworkUpload()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        var impact = detector.Observe(Frame(2, now.AddMilliseconds(50), 1_050, new(14, 0, 0)), true);

        Assert.IsTrue(impact.ImpactSequence > 0);
        Assert.AreEqual(6, impact.ImpactMagnitudeMps, .01);
        Assert.AreEqual(6, impact.ImpactSpeedLossMps, .01);
        var retained = detector.Observe(Frame(3, now.AddMilliseconds(500), 1_500, new(14, 0, 0)), true);
        Assert.AreEqual(impact.ImpactSequence, retained.ImpactSequence);
        Assert.IsTrue(retained.ImpactAgeMilliseconds >= 400);
    }

    [TestMethod]
    public void RejectsVerticalLandingOrdinarySteeringAndInvalidTelemetry()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        var landing = detector.Observe(Frame(2, now.AddMilliseconds(50), 1_050, new(19, -10, 0)), true);
        Assert.AreEqual(0, landing.ImpactMagnitudeMps);

        var steering = detector.Observe(Frame(3, now.AddMilliseconds(100), 1_100, new(18.5f, -10, 2)), true);
        Assert.AreEqual(0, steering.ImpactMagnitudeMps);

        var invalid = detector.Observe(Frame(4, now.AddMilliseconds(150), 1_150, new(5, 0, 0)), false);
        Assert.AreEqual(0, invalid.ImpactMagnitudeMps);
        var afterInvalid = detector.Observe(Frame(5, now.AddMilliseconds(200), 1_200, new(20, 0, 0)), true);
        Assert.AreEqual(0, afterInvalid.ImpactMagnitudeMps);
    }

    [TestMethod]
    public void ResetDoesNotReuseAnOldSequenceOrCrossSessionEvidence()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        var first = detector.Observe(Frame(2, now.AddMilliseconds(50), 1_050, new(14, 0, 0)), true);
        detector.Reset();
        var reset = detector.Observe(Frame(3, now.AddSeconds(2), 3_000, new(20, 0, 0)), true);
        Assert.AreEqual(0, reset.ImpactMagnitudeMps);
        var second = detector.Observe(Frame(4, now.AddSeconds(2.05), 3_050, new(14, 0, 0)), true);
        Assert.IsTrue(second.ImpactSequence > first.ImpactSequence);
    }

    private static TelemetryFrame Frame(
        long sequence,
        DateTimeOffset arrival,
        uint timestamp,
        Vector3F velocity)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = timestamp,
            Position = new Vector3F(100, 0, 50),
            Velocity = velocity,
            Speed = (float)Math.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z)
        };
        return new TelemetryFrame(
            sequence,
            arrival,
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(raw.Speed * 3.6, raw.Speed * 2.236936, 0, 0, 0, 0, 0, 0, default),
            ReadOnlyMemory<byte>.Empty);
    }
}
