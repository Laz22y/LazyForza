using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

/// <summary>
/// Extracts a short-lived, local impact candidate from the player's own
/// official UDP telemetry. It deliberately does not classify responsibility;
/// the server must correlate matching evidence from another nearby driver.
/// </summary>
internal sealed class EstateCollisionEvidenceDetector
{
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan EventCooldown = TimeSpan.FromMilliseconds(850);
    private Vector3F? previousVelocity;
    private uint? previousTimestamp;
    private DateTimeOffset? lastEventAt;
    private PendingEvidence? pending;
    private long sequence;

    public EstateCollisionTelemetry Observe(TelemetryFrame frame, bool telemetryValid)
    {
        var now = frame.ArrivalTime;
        Expire(now);
        var raw = frame.Raw;
        if (!telemetryValid || !Finite(raw.Position) || !Finite(raw.Velocity))
        {
            previousVelocity = null;
            previousTimestamp = null;
            pending = null;
            return Snapshot(raw, now);
        }

        if (previousVelocity is Vector3F prior && previousTimestamp is uint priorTimestamp)
        {
            var elapsedMilliseconds = unchecked(raw.TimestampMS - priorTimestamp);
            if (elapsedMilliseconds is >= 5 and <= 150)
            {
                var deltaX = raw.Velocity.X - prior.X;
                var deltaY = raw.Velocity.Y - prior.Y;
                var deltaZ = raw.Velocity.Z - prior.Z;
                var horizontalDelta = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
                var previousHorizontalSpeed = Math.Sqrt(prior.X * prior.X + prior.Z * prior.Z);
                var currentHorizontalSpeed = Math.Sqrt(
                    raw.Velocity.X * raw.Velocity.X + raw.Velocity.Z * raw.Velocity.Z);
                var speedLoss = Math.Max(0, previousHorizontalSpeed - currentHorizontalSpeed);
                var directionChangeDegrees = DirectionChangeDegrees(prior, raw.Velocity);
                var horizontalCandidate = horizontalDelta >= 2.4 &&
                                          previousHorizontalSpeed >= 3 &&
                                          (speedLoss >= 1.35 ||
                                           directionChangeDegrees >= 11 ||
                                           horizontalDelta >= 4.2);
                var likelyVerticalLanding = Math.Abs(deltaY) > Math.Max(3.2, horizontalDelta * 1.35);
                if (horizontalCandidate && !likelyVerticalLanding)
                    Register(now, raw.Position, horizontalDelta, speedLoss);
            }
        }

        previousVelocity = raw.Velocity;
        previousTimestamp = raw.TimestampMS;
        return Snapshot(raw, now);
    }

    public void Reset()
    {
        previousVelocity = null;
        previousTimestamp = null;
        lastEventAt = null;
        pending = null;
    }

    private void Register(
        DateTimeOffset now,
        Vector3F position,
        double magnitude,
        double speedLoss)
    {
        if (lastEventAt is DateTimeOffset last && now - last < EventCooldown)
        {
            if (pending is not null && magnitude > pending.Magnitude)
                pending = pending with
                {
                    Position = position,
                    Magnitude = magnitude,
                    SpeedLoss = Math.Max(speedLoss, pending.SpeedLoss)
                };
            return;
        }

        lastEventAt = now;
        sequence = Math.Max(sequence + 1, now.ToUnixTimeMilliseconds());
        pending = new PendingEvidence(sequence, now, position, magnitude, speedLoss);
    }

    private EstateCollisionTelemetry Snapshot(Fh6RawTelemetry raw, DateTimeOffset now)
    {
        Expire(now);
        return new EstateCollisionTelemetry(
            raw.Position,
            raw.Velocity,
            pending?.Sequence ?? sequence,
            pending?.Magnitude ?? 0,
            pending?.SpeedLoss ?? 0,
            pending?.Position ?? raw.Position,
            pending is null
                ? 0
                : Math.Clamp((int)Math.Round((now - pending.DetectedAt).TotalMilliseconds), 0, 2_000));
    }

    private void Expire(DateTimeOffset now)
    {
        if (pending is not null && now - pending.DetectedAt > EvidenceLifetime)
            pending = null;
    }

    private static double DirectionChangeDegrees(Vector3F left, Vector3F right)
    {
        var leftLength = Math.Sqrt(left.X * left.X + left.Z * left.Z);
        var rightLength = Math.Sqrt(right.X * right.X + right.Z * right.Z);
        if (leftLength < 0.5 || rightLength < 0.5) return 0;
        var cosine = Math.Clamp(
            (left.X * right.X + left.Z * right.Z) / (leftLength * rightLength),
            -1,
            1);
        return Math.Acos(cosine) * 180 / Math.PI;
    }

    private static bool Finite(Vector3F value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed record PendingEvidence(
        long Sequence,
        DateTimeOffset DetectedAt,
        Vector3F Position,
        double Magnitude,
        double SpeedLoss);
}

internal readonly record struct EstateCollisionTelemetry(
    Vector3F WorldPosition,
    Vector3F Velocity,
    long ImpactSequence,
    double ImpactMagnitudeMps,
    double ImpactSpeedLossMps,
    Vector3F ImpactPosition,
    int ImpactAgeMilliseconds);
