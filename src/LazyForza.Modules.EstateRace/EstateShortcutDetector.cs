using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

[Flags]
internal enum EstateShortcutEvidenceFlags
{
    None = 0,
    DistanceGain = 1,
    ProtectedArc = 2,
    MissedCriticalGate = 4,
    ProjectionAmbiguous = 8
}

internal readonly record struct EstateShortcutObservation(
    EstateRaceProjection Projection,
    RaceShortcutEvidence? Evidence);

internal sealed class EstateShortcutDetector
{
    private const int MaximumWindowMilliseconds = 4_000;
    private const int EvidenceLifetimeMilliseconds = 10_000;
    private const int EmissionCooldownMilliseconds = 1_500;
    private readonly Queue<MotionSlice> slices = [];
    private TrackIdentity? trackIdentity;
    private IReadOnlyList<ProtectedArc> protectedArcs = [];
    private EstateRaceProjection? previousProjection;
    private Vector3F? previousPosition;
    private double previousSpeedMetersPerSecond;
    private long previousMonotonicMilliseconds;
    private long lastEmissionMonotonicMilliseconds = long.MinValue;
    private RaceShortcutEvidence? latestEvidence;

    internal int ProtectedArcCount => protectedArcs.Count;

    public EstateShortcutObservation Observe(
        TelemetryFrame frame,
        TrackTemplate track,
        EstatePitDefinition? pit,
        bool telemetryValid,
        long monotonicMilliseconds)
    {
        EnsureTrack(track);
        var position = frame.Raw.Position;
        var legalPitRoute = telemetryValid &&
                            (EstateRaceGeometry.IsInPitLane(pit, position) ||
                             EstateRaceGeometry.IsInServiceZone(pit, position) ||
                             EstateRaceGeometry.IsApproachingPitEntry(pit, position));
        if (!telemetryValid || legalPitRoute)
        {
            ResetTracking();
            return new EstateShortcutObservation(
                EstateRaceGeometry.Project(track, position),
                CurrentEvidence(monotonicMilliseconds));
        }

        var actualAdvance = 0d;
        var elapsedSeconds = 0d;
        if (previousPosition is Vector3F priorPosition && previousMonotonicMilliseconds > 0)
        {
            elapsedSeconds = (monotonicMilliseconds - previousMonotonicMilliseconds) / 1000d;
            actualAdvance = Distance(priorPosition, position);
        }
        var projection = previousProjection is EstateRaceProjection priorProjection && priorProjection.IsValid
            ? EstateRaceGeometry.ProjectContinuous(track, position, priorProjection, actualAdvance)
            : EstateRaceGeometry.Project(track, position);

        if (previousProjection is not EstateRaceProjection previous ||
            previousPosition is not Vector3F previousWorld ||
            previousMonotonicMilliseconds <= 0)
        {
            SetPrevious(frame, projection, monotonicMilliseconds);
            return new EstateShortcutObservation(projection, CurrentEvidence(monotonicMilliseconds));
        }

        var maximumProjectionDistance = Math.Max(80, track.MatchingToleranceMeters * 6);
        var plausibleTravel = Math.Max(
            12,
            Math.Max(previousSpeedMetersPerSecond, Math.Max(0, frame.Raw.Speed)) * elapsedSeconds * 3 + 5);
        if (elapsedSeconds is <= 0 or > 0.75 ||
            actualAdvance > plausibleTravel ||
            !projection.IsValid ||
            projection.DistanceMeters > maximumProjectionDistance)
        {
            ResetWindow();
            SetPrevious(frame, projection, monotonicMilliseconds);
            return new EstateShortcutObservation(projection, CurrentEvidence(monotonicMilliseconds));
        }

        var wrappedAtFinish = previous.ProgressMeters >= track.LengthMeters * 0.75 &&
                              projection.ProgressMeters <= track.LengthMeters * 0.25;
        if (wrappedAtFinish)
        {
            ResetWindow();
            SetPrevious(frame, projection, monotonicMilliseconds);
            return new EstateShortcutObservation(projection, CurrentEvidence(monotonicMilliseconds));
        }

        var routeAdvance = ContinuousProgressDelta(
            previous.ProgressMeters,
            projection.ProgressMeters,
            track.LengthMeters);
        if (routeAdvance < -2 || routeAdvance > Math.Min(track.LengthMeters * 0.5, 500))
        {
            ResetWindow();
            SetPrevious(frame, projection, monotonicMilliseconds);
            return new EstateShortcutObservation(projection, CurrentEvidence(monotonicMilliseconds));
        }

        routeAdvance = Math.Max(0, routeAdvance);
        var protectedRoute = 0d;
        var theoreticalSaving = 0d;
        var missedGates = 0;
        if (routeAdvance > 0)
        {
            foreach (var arc in protectedArcs)
            {
                var overlap = Math.Max(
                    0,
                    Math.Min(projection.ProgressMeters, arc.EndProgressMeters) -
                    Math.Max(previous.ProgressMeters, arc.StartProgressMeters));
                if (overlap > 0)
                {
                    protectedRoute += overlap;
                    theoreticalSaving = Math.Max(theoreticalSaving, arc.TheoreticalSavingMeters);
                }
                if (previous.ProgressMeters < arc.GateProgressMeters &&
                    projection.ProgressMeters >= arc.GateProgressMeters &&
                    DistanceToSegment(arc.GatePosition, previousWorld, position) > arc.GateRadiusMeters)
                    missedGates++;
            }
        }

        slices.Enqueue(new MotionSlice(
            monotonicMilliseconds,
            previous.ProgressMeters,
            projection.ProgressMeters,
            routeAdvance,
            actualAdvance,
            Math.Max(Math.Abs(previous.LateralOffsetMeters), Math.Abs(projection.LateralOffsetMeters)),
            Math.Min(routeAdvance, protectedRoute),
            theoreticalSaving,
            missedGates,
            projection.IsAmbiguous));
        TrimWindow(monotonicMilliseconds);
        TryEmitEvidence(track, monotonicMilliseconds);
        SetPrevious(frame, projection, monotonicMilliseconds);
        return new EstateShortcutObservation(projection, CurrentEvidence(monotonicMilliseconds));
    }

    public void Reset(bool clearEvidence = true)
    {
        ResetTracking();
        trackIdentity = null;
        protectedArcs = [];
        if (clearEvidence) latestEvidence = null;
    }

    private void EnsureTrack(TrackTemplate track)
    {
        var identity = new TrackIdentity(track.Id, track.UpdatedAt, track.Points.Count, track.LengthMeters);
        if (trackIdentity == identity) return;
        ResetTracking();
        latestEvidence = null;
        trackIdentity = identity;
        protectedArcs = BuildProtectedArcs(track);
    }

    private void TryEmitEvidence(TrackTemplate track, long now)
    {
        if (slices.Count == 0 ||
            (lastEmissionMonotonicMilliseconds != long.MinValue &&
             now - lastEmissionMonotonicMilliseconds < EmissionCooldownMilliseconds))
            return;
        var routeDistance = slices.Sum(item => item.RouteDistanceMeters);
        var worldDistance = slices.Sum(item => item.WorldDistanceMeters);
        if (routeDistance <= 0) return;
        var rawGain = routeDistance - worldDistance;
        var noiseAllowance = Math.Max(1.25, routeDistance * 0.018);
        var gain = rawGain - noiseAllowance;
        var minimumGain = Math.Max(5, Math.Min(8, track.MatchingToleranceMeters * 0.3));
        if (gain < minimumGain) return;

        var protectedRoute = Math.Min(routeDistance, slices.Sum(item => item.ProtectedRouteMeters));
        var theoreticalSaving = slices.Max(item => item.TheoreticalSavingMeters);
        var missedGates = slices.Sum(item => item.MissedCriticalGates);
        var ambiguousRoute = slices.Where(item => item.ProjectionAmbiguous)
            .Sum(item => item.RouteDistanceMeters);
        var ambiguityRatio = routeDistance <= 0 ? 0 : ambiguousRoute / routeDistance;
        var curvatureSupport = protectedRoute >= Math.Min(20, routeDistance * 0.25) &&
                               theoreticalSaving >= 3;
        if (missedGates == 0 && !curvatureSupport) return;
        if (ambiguityRatio > 0.55 && (missedGates == 0 || gain < minimumGain * 2)) return;

        var confidence = 0.58 +
                         (missedGates > 0 ? 0.22 : 0) +
                         (curvatureSupport ? 0.12 : 0) +
                         Math.Min(0.12, gain / Math.Max(1, minimumGain) * 0.04) -
                         ambiguityRatio * 0.25;
        confidence = Math.Clamp(confidence, 0, 1);
        if (confidence < 0.72) return;

        var flags = EstateShortcutEvidenceFlags.DistanceGain;
        if (curvatureSupport) flags |= EstateShortcutEvidenceFlags.ProtectedArc;
        if (missedGates > 0) flags |= EstateShortcutEvidenceFlags.MissedCriticalGate;
        if (ambiguityRatio > 0.2) flags |= EstateShortcutEvidenceFlags.ProjectionAmbiguous;
        var first = slices.Peek();
        var last = slices.Last();
        latestEvidence = new RaceShortcutEvidence(
            Guid.NewGuid(),
            now,
            Math.Clamp(first.StartProgressMeters / track.LengthMeters, 0, 1),
            Math.Clamp(last.EndProgressMeters / track.LengthMeters, 0, 1),
            routeDistance,
            worldDistance,
            Math.Max(0, rawGain),
            slices.Max(item => item.MaximumLateralOffsetMeters),
            protectedRoute,
            theoreticalSaving,
            missedGates,
            confidence,
            (int)flags);
        lastEmissionMonotonicMilliseconds = now;
        slices.Clear();
    }

    private void TrimWindow(long now)
    {
        while (slices.Count > 0 && now - slices.Peek().AtMonotonicMilliseconds > MaximumWindowMilliseconds)
            slices.Dequeue();
        while (slices.Count > 1 && slices.Sum(item => item.RouteDistanceMeters) > 300)
            slices.Dequeue();
    }

    private RaceShortcutEvidence? CurrentEvidence(long now) =>
        latestEvidence is { } evidence &&
        now - evidence.DetectedAtMonotonicMilliseconds <= EvidenceLifetimeMilliseconds
            ? evidence
            : null;

    private void SetPrevious(
        TelemetryFrame frame,
        EstateRaceProjection projection,
        long monotonicMilliseconds)
    {
        previousPosition = frame.Raw.Position;
        previousProjection = projection.IsValid ? projection : null;
        previousSpeedMetersPerSecond = Math.Max(0, frame.Raw.Speed);
        previousMonotonicMilliseconds = monotonicMilliseconds;
    }

    private void ResetTracking()
    {
        ResetWindow();
        previousProjection = null;
        previousPosition = null;
        previousSpeedMetersPerSecond = 0;
        previousMonotonicMilliseconds = 0;
    }

    private void ResetWindow() => slices.Clear();

    private static IReadOnlyList<ProtectedArc> BuildProtectedArcs(TrackTemplate track)
    {
        if (track.Points.Count < 10) return [];
        var candidates = new List<ProtectedArc>();
        const int radius = 4;
        for (var center = radius; center < track.Points.Count - radius; center++)
        {
            var start = track.Points[center - radius];
            var gate = track.Points[center];
            var end = track.Points[center + radius];
            var incomingX = gate.X - start.X;
            var incomingY = gate.Y - start.Y;
            var incomingZ = gate.Z - start.Z;
            var outgoingX = end.X - gate.X;
            var outgoingY = end.Y - gate.Y;
            var outgoingZ = end.Z - gate.Z;
            var incomingLength = Math.Sqrt(incomingX * incomingX + incomingY * incomingY + incomingZ * incomingZ);
            var outgoingLength = Math.Sqrt(outgoingX * outgoingX + outgoingY * outgoingY + outgoingZ * outgoingZ);
            if (incomingLength < 1 || outgoingLength < 1) continue;
            var dot = (incomingX * outgoingX + incomingY * outgoingY + incomingZ * outgoingZ) /
                      (incomingLength * outgoingLength);
            var turnDegrees = Math.Acos(Math.Clamp(dot, -1, 1)) * 180 / Math.PI;
            var arcLength = end.S - start.S;
            var chordLength = Distance(
                new Vector3F((float)start.X, (float)start.Y, (float)start.Z),
                new Vector3F((float)end.X, (float)end.Y, (float)end.Z));
            var saving = arcLength - chordLength;
            if (turnDegrees < 32 || saving < 2.5) continue;
            candidates.Add(new ProtectedArc(
                start.S,
                end.S,
                gate.S,
                new Vector3F((float)gate.X, (float)gate.Y, (float)gate.Z),
                Math.Clamp(track.MatchingToleranceMeters * 0.35, 3.5, 7),
                saving));
        }

        var selected = new List<ProtectedArc>();
        foreach (var candidate in candidates.OrderByDescending(item => item.TheoreticalSavingMeters))
        {
            if (selected.Any(existing =>
                    Math.Abs(existing.GateProgressMeters - candidate.GateProgressMeters) < 20))
                continue;
            selected.Add(candidate);
        }
        return selected.OrderBy(item => item.GateProgressMeters).ToArray();
    }

    private static double ContinuousProgressDelta(double previous, double current, double length)
    {
        var delta = current - previous;
        if (previous >= length * 0.75 && current <= length * 0.25) return 0;
        if (previous <= length * 0.25 && current >= length * 0.75) return -1;
        return delta;
    }

    private static double Distance(Vector3F left, Vector3F right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double DistanceToSegment(Vector3F point, Vector3F start, Vector3F end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dy * dy + dz * dz;
        if (lengthSquared < 0.0001) return Distance(point, start);
        var amount = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy + (point.Z - start.Z) * dz) /
            lengthSquared,
            0,
            1);
        return Distance(point, new Vector3F(
            (float)(start.X + dx * amount),
            (float)(start.Y + dy * amount),
            (float)(start.Z + dz * amount)));
    }

    private readonly record struct TrackIdentity(
        Guid Id,
        DateTimeOffset UpdatedAt,
        int PointCount,
        double LengthMeters);

    private readonly record struct ProtectedArc(
        double StartProgressMeters,
        double EndProgressMeters,
        double GateProgressMeters,
        Vector3F GatePosition,
        double GateRadiusMeters,
        double TheoreticalSavingMeters);

    private readonly record struct MotionSlice(
        long AtMonotonicMilliseconds,
        double StartProgressMeters,
        double EndProgressMeters,
        double RouteDistanceMeters,
        double WorldDistanceMeters,
        double MaximumLateralOffsetMeters,
        double ProtectedRouteMeters,
        double TheoreticalSavingMeters,
        int MissedCriticalGates,
        bool ProjectionAmbiguous);
}
