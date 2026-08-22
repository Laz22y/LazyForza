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
        var candidate = detector.Observe(Frame(
            2,
            now.AddMilliseconds(50),
            1_050,
            new(14, 0, 0),
            acceleration: new(65, 0, 0)), true);
        Assert.AreEqual(0, candidate.ImpactMagnitudeMps, .01,
            "短暂候选应等待官方可破坏物字段，不应在同一帧上传。");

        var impact = detector.Observe(Frame(3, now.AddMilliseconds(250), 1_250, new(14, 0, 0)), true);

        Assert.IsTrue(impact.ImpactSequence > 0);
        Assert.AreEqual(6, impact.ImpactMagnitudeMps, .01);
        Assert.AreEqual(6, impact.ImpactSpeedLossMps, .01);
        var retained = detector.Observe(Frame(4, now.AddMilliseconds(500), 1_500, new(14, 0, 0)), true);
        Assert.AreEqual(impact.ImpactSequence, retained.ImpactSequence);
        Assert.IsTrue(retained.ImpactAgeMilliseconds >= 400);
    }

    [TestMethod]
    public void RejectsVerticalLandingOrdinarySteeringAndInvalidTelemetry()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        var landing = detector.Observe(Frame(
            2,
            now.AddMilliseconds(50),
            1_050,
            new(14, -10, 0),
            acceleration: new(65, 0, 0)), true);
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
        _ = detector.Observe(Frame(
            2,
            now.AddMilliseconds(50),
            1_050,
            new(14, 0, 0),
            acceleration: new(65, 0, 0)), true);
        var first = detector.Observe(Frame(3, now.AddMilliseconds(250), 1_250, new(14, 0, 0)), true);
        detector.Reset();
        var reset = detector.Observe(Frame(4, now.AddSeconds(2), 3_000, new(20, 0, 0)), true);
        Assert.AreEqual(0, reset.ImpactMagnitudeMps);
        _ = detector.Observe(Frame(
            5,
            now.AddSeconds(2.05),
            3_050,
            new(14, 0, 0),
            acceleration: new(65, 0, 0)), true);
        var second = detector.Observe(Frame(6, now.AddSeconds(2.25), 3_250, new(14, 0, 0)), true);
        Assert.IsTrue(second.ImpactSequence > first.ImpactSequence);
    }

    [TestMethod]
    public void UsesWorldVelocitySoNormalYawChangeDoesNotLookLikeAnImpact()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(0, 0, 20), yaw: 0), true);
        var turning = detector.Observe(Frame(
            2,
            now.AddMilliseconds(80),
            1_080,
            new(-9.59f, 0, 17.55f),
            acceleration: new(65, 0, 0),
            yaw: .5f), true);

        Assert.AreEqual(0, turning.ImpactMagnitudeMps, .01,
            "车辆转向时本地速度分量会变化，但转换到世界坐标后不应误报碰撞。");
    }

    [TestMethod]
    public void RejectsControlledBrakingDistributedAcrossTheOldLongWindow()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        _ = detector.Observe(Frame(
            2, now.AddMilliseconds(35), 1_035, new(19.4f, 0, 0), acceleration: new(20, 0, 0)), true);
        _ = detector.Observe(Frame(
            3, now.AddMilliseconds(70), 1_070, new(18.7f, 0, 0), acceleration: new(20, 0, 0)), true);
        _ = detector.Observe(Frame(
            4, now.AddMilliseconds(105), 1_105, new(17.8f, 0, 0), acceleration: new(20, 0, 0)), true);
        var afterOldWindow = detector.Observe(Frame(
            5, now.AddMilliseconds(250), 1_250, new(15, 0, 0), acceleration: new(20, 0, 0)), true);

        Assert.AreEqual(0, afterOldWindow.ImpactMagnitudeMps, .01);
        Assert.AreEqual(0, afterOldWindow.ImpactSequence);
    }

    [TestMethod]
    public void DetectsAnImpactDistributedAcrossSeveralShortFrames()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        _ = detector.Observe(Frame(
            2, now.AddMilliseconds(16), 1_016, new(19.9f, 0, 0), acceleration: new(3, 0, 0)), true);
        _ = detector.Observe(Frame(
            3, now.AddMilliseconds(32), 1_032, new(18.6f, 0, 0), acceleration: new(45, 0, 0)), true);
        _ = detector.Observe(Frame(
            4, now.AddMilliseconds(63), 1_063, new(16.8f, 0, 0), acceleration: new(50, 0, 0)), true);
        var impact = detector.Observe(Frame(5, now.AddMilliseconds(250), 1_250, new(16.8f, 0, 0)), true);

        Assert.IsTrue(impact.ImpactSequence > 0);
        Assert.IsTrue(impact.ImpactMagnitudeMps >= 3.1);
        Assert.IsTrue(impact.ImpactSpeedLossMps >= 3.1);
    }

    [TestMethod]
    public void OfficialSmashableObjectFieldsVetoVehicleCollisionEvidence()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        var impact = detector.Observe(Frame(
            2,
            now.AddMilliseconds(60),
            1_060,
            new(14, 0, 0),
            acceleration: new(65, 0, 0),
            smashableVelDiff: 6,
            smashableMass: 25), true);

        Assert.AreEqual(0, impact.ImpactMagnitudeMps, .01);
    }

    [TestMethod]
    public void DelayedOfficialSmashableEvidenceCancelsAnUnconfirmedCandidate()
    {
        var detector = new EstateCollisionEvidenceDetector();
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        _ = detector.Observe(Frame(1, now, 1_000, new(20, 0, 0)), true);
        _ = detector.Observe(Frame(
            2,
            now.AddMilliseconds(50),
            1_050,
            new(14, 0, 0),
            acceleration: new(65, 0, 0)), true);
        _ = detector.Observe(Frame(
            3,
            now.AddMilliseconds(80),
            1_080,
            new(14, 0, 0),
            smashableVelDiff: .3f), true);
        var afterGrace = detector.Observe(Frame(4, now.AddMilliseconds(300), 1_300, new(14, 0, 0)), true);

        Assert.AreEqual(0, afterGrace.ImpactMagnitudeMps, .01);
        Assert.AreEqual(0, afterGrace.ImpactSequence);
    }

    private static TelemetryFrame Frame(
        long sequence,
        DateTimeOffset arrival,
        uint timestamp,
        Vector3F velocity,
        Vector3F acceleration = default,
        float yaw = 0,
        float smashableVelDiff = 0,
        float smashableMass = 0)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = timestamp,
            Position = new Vector3F(100, 0, 50),
            Velocity = velocity,
            Acceleration = acceleration,
            Yaw = yaw,
            SmashableVelDiff = smashableVelDiff,
            SmashableMass = smashableMass,
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
