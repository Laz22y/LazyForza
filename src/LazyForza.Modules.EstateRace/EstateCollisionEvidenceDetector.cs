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
    private static readonly TimeSpan CandidateConfirmationDelay = TimeSpan.FromMilliseconds(180);
    private const int MinimumImpactWindowMilliseconds = 32;
    private const int TargetImpactWindowMilliseconds = 60;
    private const int MaximumImpactWindowMilliseconds = 90;
    private const int SmashableEvidenceWindowMilliseconds = 350;
    private const double MinimumHorizontalImpulseMps = 2.4;
    private const double MinimumHorizontalAccelerationMps2 = 32;
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
        if (!telemetryValid || !Finite(raw.Position) || !Finite(raw.Velocity) || !Finite(raw.Acceleration) ||
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
            HorizontalLength(raw.Acceleration),
            Math.Max(0, raw.SmashableVelDiff),
            Math.Max(0, raw.SmashableMass));
        samples.Add(current);
        PruneSamples(current);

        if (HasRecentSmashableEvidence())
        {
            if (pending is not null && now - pending.DetectedAt <=
                TimeSpan.FromMilliseconds(SmashableEvidenceWindowMilliseconds))
                pending = null;
            return Snapshot(raw, worldVelocity, now);
        }

        var baseline = FindBaseline(current);
        if (baseline is MotionSample prior)
        {
            var deltaX = current.WorldVelocity.X - prior.WorldVelocity.X;
            var deltaY = current.WorldVelocity.Y - prior.WorldVelocity.Y;
            var deltaZ = current.WorldVelocity.Z - prior.WorldVelocity.Z;
            var horizontalDelta = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            var previousHorizontalSpeed = HorizontalLength(prior.WorldVelocity);
            var currentHorizontalSpeed = HorizontalLength(current.WorldVelocity);
            var speedLoss = Math.Max(0, previousHorizontalSpeed - currentHorizontalSpeed);
            var horizontalCandidate = horizontalDelta >= MinimumHorizontalImpulseMps &&
                                      previousHorizontalSpeed >= 2.5 &&
                                      PeakHorizontalAcceleration(prior, current) >=
                                      MinimumHorizontalAccelerationMps2;
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
        pending = new PendingEvidence(
            0,
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
        if (pending is { Sequence: 0 } candidate && now - candidate.DetectedAt >= CandidateConfirmationDelay)
        {
            sequence = Math.Max(sequence + 1, candidate.DetectedAt.ToUnixTimeMilliseconds());
            pending = candidate with { Sequence = sequence };
        }
        var visible = pending is { Sequence: > 0 } ? pending : null;
        return new EstateCollisionTelemetry(
            raw.Position,
            raw.Velocity,
            worldVelocity,
            visible?.Sequence ?? sequence,
            visible?.Magnitude ?? 0,
            visible?.SpeedLoss ?? 0,
            visible?.Position ?? raw.Position,
            visible?.WorldVelocity ?? worldVelocity,
            visible?.SmashableVelDiff ?? Math.Max(0, raw.SmashableVelDiff),
            visible?.SmashableMass ?? Math.Max(0, raw.SmashableMass),
            visible is null
                ? 0
                : Math.Clamp((int)Math.Round((now - visible.DetectedAt).TotalMilliseconds), 0, 2_000));
    }

    private MotionSample? FindBaseline(MotionSample current)
    {
        MotionSample? nearest = null;
        var nearestDifference = int.MaxValue;
        foreach (var sample in samples)
        {
            var elapsed = unchecked(current.Timestamp - sample.Timestamp);
            if (elapsed is < MinimumImpactWindowMilliseconds or > MaximumImpactWindowMilliseconds)
                continue;
            var difference = Math.Abs((int)elapsed - TargetImpactWindowMilliseconds);
            if (difference >= nearestDifference) continue;
            nearest = sample;
            nearestDifference = difference;
        }
        return nearest;
    }

    private void PruneSamples(MotionSample current)
    {
        var removeCount = 0;
        foreach (var sample in samples)
        {
            if (unchecked(current.Timestamp - sample.Timestamp) <= SmashableEvidenceWindowMilliseconds) break;
            removeCount++;
        }
        if (removeCount > 0) samples.RemoveRange(0, removeCount);
        if (samples.Count > 32) samples.RemoveRange(0, samples.Count - 32);
    }

    private bool HasRecentSmashableEvidence() => samples.Any(sample =>
        sample.SmashableVelDiff >= .2 || sample.SmashableMass >= .5);

    private double PeakHorizontalAcceleration(MotionSample prior, MotionSample current)
    {
        var window = unchecked(current.Timestamp - prior.Timestamp);
        var maximum = 0d;
        foreach (var sample in samples)
        {
            if (unchecked(current.Timestamp - sample.Timestamp) > window) continue;
            maximum = Math.Max(maximum, sample.HorizontalAcceleration);
        }
        return maximum;
    }

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

    private static bool Finite(Vector3F value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct MotionSample(
        uint Timestamp,
        Vector3F Position,
        Vector3F WorldVelocity,
        double HorizontalAcceleration,
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
