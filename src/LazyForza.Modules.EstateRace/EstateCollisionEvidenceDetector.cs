using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

/// <summary>
/// Extracts short-lived impact candidates from the player's own official UDP
/// dynamics. FH6 does not expose vehicle-to-vehicle contact, so the result is
/// evidence only; the server must correlate it with another driver's trajectory.
/// </summary>
internal sealed class EstateCollisionEvidenceDetector
{
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan EventCooldown = TimeSpan.FromMilliseconds(650);
    private const int MinimumWindowMilliseconds = 40;
    private const int MaximumWindowMilliseconds = 260;
    private const double MinimumHorizontalImpulseMps = 1.45;
    private readonly List<MotionSample> samples = [];
    private DateTimeOffset? lastEventAt;
    private PendingEvidence? pending;
    private long sequence;

    public EstateCollisionTelemetry Observe(TelemetryFrame frame, bool telemetryValid)
    {
        var now = frame.ArrivalTime;
        Expire(now);
        var raw = frame.Raw;
        var worldVelocity = ToWorldVelocity(raw.Velocity, raw.Yaw);
        if (!telemetryValid || !Finite(raw.Position) || !Finite(raw.Velocity) ||
            !Finite(worldVelocity) || !float.IsFinite(raw.Yaw))
        {
            samples.Clear();
            pending = null;
            return Snapshot(raw, worldVelocity, now);
        }

        var current = new MotionSample(
            raw.TimestampMS,
            raw.Position,
            worldVelocity,
            Math.Max(0, raw.SmashableVelDiff),
            Math.Max(0, raw.SmashableMass));
        samples.Add(current);
        PruneSamples(current);

        var baseline = FindBaseline(current);
        if (baseline is MotionSample prior && !HasRecentSmashableEvidence())
        {
            var deltaX = current.WorldVelocity.X - prior.WorldVelocity.X;
            var deltaY = current.WorldVelocity.Y - prior.WorldVelocity.Y;
            var deltaZ = current.WorldVelocity.Z - prior.WorldVelocity.Z;
            var horizontalDelta = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            var previousHorizontalSpeed = HorizontalLength(prior.WorldVelocity);
            var currentHorizontalSpeed = HorizontalLength(current.WorldVelocity);
            var speedLoss = Math.Max(0, previousHorizontalSpeed - currentHorizontalSpeed);
            var directionChangeDegrees = DirectionChangeDegrees(prior.WorldVelocity, current.WorldVelocity);
            var horizontalCandidate = horizontalDelta >= MinimumHorizontalImpulseMps &&
                                      previousHorizontalSpeed >= 2.5 &&
                                      (speedLoss >= .7 || directionChangeDegrees >= 4.5 ||
                                       horizontalDelta >= 2.35);
            var likelyVerticalLanding = Math.Abs(deltaY) > Math.Max(2.8, horizontalDelta * 1.45);
            if (horizontalCandidate && !likelyVerticalLanding)
                Register(now, current, horizontalDelta, speedLoss);
        }

        return Snapshot(raw, worldVelocity, now);
    }

    public void Reset()
    {
        samples.Clear();
        lastEventAt = null;
        pending = null;
    }

    private void Register(DateTimeOffset now, MotionSample sample, double magnitude, double speedLoss)
    {
        if (lastEventAt is DateTimeOffset last && now - last < EventCooldown)
        {
            if (pending is not null && magnitude > pending.Magnitude)
                pending = pending with
                {
                    Position = sample.Position,
                    WorldVelocity = sample.WorldVelocity,
                    Magnitude = magnitude,
                    SpeedLoss = Math.Max(speedLoss, pending.SpeedLoss)
                };
            return;
        }

        lastEventAt = now;
        sequence = Math.Max(sequence + 1, now.ToUnixTimeMilliseconds());
        pending = new PendingEvidence(
            sequence,
            now,
            sample.Position,
            sample.WorldVelocity,
            magnitude,
            speedLoss,
            sample.SmashableVelDiff,
            sample.SmashableMass);
    }

    private EstateCollisionTelemetry Snapshot(
        Fh6RawTelemetry raw,
        Vector3F worldVelocity,
        DateTimeOffset now)
    {
        Expire(now);
        return new EstateCollisionTelemetry(
            raw.Position,
            raw.Velocity,
            worldVelocity,
            pending?.Sequence ?? sequence,
            pending?.Magnitude ?? 0,
            pending?.SpeedLoss ?? 0,
            pending?.Position ?? raw.Position,
            pending?.WorldVelocity ?? worldVelocity,
            pending?.SmashableVelDiff ?? Math.Max(0, raw.SmashableVelDiff),
            pending?.SmashableMass ?? Math.Max(0, raw.SmashableMass),
            pending is null
                ? 0
                : Math.Clamp((int)Math.Round((now - pending.DetectedAt).TotalMilliseconds), 0, 2_000));
    }

    private MotionSample? FindBaseline(MotionSample current)
    {
        foreach (var sample in samples)
        {
            var elapsed = unchecked(current.Timestamp - sample.Timestamp);
            if (elapsed is >= MinimumWindowMilliseconds and <= MaximumWindowMilliseconds)
                return sample;
        }
        return null;
    }

    private void PruneSamples(MotionSample current)
    {
        var removeCount = 0;
        foreach (var sample in samples)
        {
            if (unchecked(current.Timestamp - sample.Timestamp) <= MaximumWindowMilliseconds) break;
            removeCount++;
        }
        if (removeCount > 0) samples.RemoveRange(0, removeCount);
        if (samples.Count > 32) samples.RemoveRange(0, samples.Count - 32);
    }

    private bool HasRecentSmashableEvidence() => samples.Any(sample =>
        sample.SmashableVelDiff >= .2 || sample.SmashableMass >= .5);

    private void Expire(DateTimeOffset now)
    {
        if (pending is not null && now - pending.DetectedAt > EvidenceLifetime)
            pending = null;
    }

    private static Vector3F ToWorldVelocity(Vector3F local, float yaw)
    {
        var cosine = Math.Cos(yaw);
        var sine = Math.Sin(yaw);
        return new Vector3F(
            (float)(local.X * cosine + local.Z * sine),
            local.Y,
            (float)(-local.X * sine + local.Z * cosine));
    }

    private static double HorizontalLength(Vector3F value) =>
        Math.Sqrt(value.X * value.X + value.Z * value.Z);

    private static double DirectionChangeDegrees(Vector3F left, Vector3F right)
    {
        var leftLength = HorizontalLength(left);
        var rightLength = HorizontalLength(right);
        if (leftLength < .5 || rightLength < .5) return 0;
        var cosine = Math.Clamp(
            (left.X * right.X + left.Z * right.Z) / (leftLength * rightLength),
            -1,
            1);
        return Math.Acos(cosine) * 180 / Math.PI;
    }

    private static bool Finite(Vector3F value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct MotionSample(
        uint Timestamp,
        Vector3F Position,
        Vector3F WorldVelocity,
        double SmashableVelDiff,
        double SmashableMass);

    private sealed record PendingEvidence(
        long Sequence,
        DateTimeOffset DetectedAt,
        Vector3F Position,
        Vector3F WorldVelocity,
        double Magnitude,
        double SpeedLoss,
        double SmashableVelDiff,
        double SmashableMass);
}

internal readonly record struct EstateCollisionTelemetry(
    Vector3F WorldPosition,
    Vector3F Velocity,
    Vector3F WorldVelocity,
    long ImpactSequence,
    double ImpactMagnitudeMps,
    double ImpactSpeedLossMps,
    Vector3F ImpactPosition,
    Vector3F ImpactWorldVelocity,
    double ImpactSmashableVelDiff,
    double ImpactSmashableMass,
    int ImpactAgeMilliseconds);
