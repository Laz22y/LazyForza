using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal readonly record struct EstateRaceProjection(
    double Progress,
    double LateralOffsetMeters,
    double MapX,
    double MapY,
    int SegmentIndex = -1,
    double ProgressMeters = 0,
    double DistanceMeters = double.PositiveInfinity,
    double ElevationErrorMeters = double.PositiveInfinity,
    bool IsAmbiguous = false)
{
    public bool IsValid => SegmentIndex >= 0 &&
                           double.IsFinite(DistanceMeters) &&
                           double.IsFinite(ProgressMeters);
}

internal readonly record struct EstatePitRouteProjection(
    double DistanceMeters,
    double ProgressMeters,
    double TotalLengthMeters);

internal static class EstateRaceGeometry
{
    public static EstateRaceProjection Project(
        TrackTemplate track,
        Vector3F position)
    {
        if (track.Points.Count < 2 || track.LengthMeters <= 0)
            return new EstateRaceProjection(0, 0, 0.5, 0.5);

        var best = ProjectionCandidate.Invalid;
        for (var index = 0; index < track.Points.Count - 1; index++)
        {
            var candidate = ProjectSegment(track.Points, position, index, score: 0);
            if (candidate.IsValid && (!best.IsValid || candidate.DistanceMeters < best.DistanceMeters))
                best = candidate;
        }

        return CreateProjection(track, position, best, isAmbiguous: false);
    }

    public static EstateRaceProjection ProjectContinuous(
        TrackTemplate track,
        Vector3F position,
        EstateRaceProjection previous,
        double expectedAdvanceMeters)
    {
        if (!previous.IsValid || track.Points.Count < 2 || track.LengthMeters <= 0)
            return Project(track, position);

        var expectedAdvance = Math.Clamp(expectedAdvanceMeters, 0, 250);
        var slack = Math.Max(8, expectedAdvance * 1.75 + 4);
        var best = ProjectionCandidate.Invalid;
        for (var index = 0; index < track.Points.Count - 1; index++)
        {
            var candidate = ProjectSegment(track.Points, position, index, score: 0);
            if (!candidate.IsValid) continue;
            var progressDelta = ContinuousProgressDelta(
                previous.ProgressMeters,
                candidate.ProgressMeters,
                track.LengthMeters);
            var continuityPenalty = progressDelta < -slack
                ? (-progressDelta - slack) * 1.4
                : Math.Max(0, Math.Abs(progressDelta - expectedAdvance) - slack) * 0.2;
            var extremeAdvance = Math.Max(250, expectedAdvance * 8 + 80);
            if (progressDelta > extremeAdvance)
                continuityPenalty += (progressDelta - extremeAdvance) * 0.5;
            candidate = candidate with
            {
                Score = candidate.DistanceMeters + candidate.ElevationErrorMeters * 0.15 + continuityPenalty
            };
            if (!best.IsValid || candidate.Score < best.Score) best = candidate;
        }

        if (!best.IsValid) return Project(track, position);
        var secondScore = double.PositiveInfinity;
        var ambiguitySeparation = Math.Max(40, track.LengthMeters * 0.035);
        for (var index = 0; index < track.Points.Count - 1; index++)
        {
            var candidate = ProjectSegment(track.Points, position, index, score: 0);
            if (!candidate.IsValid || Math.Abs(candidate.ProgressMeters - best.ProgressMeters) < ambiguitySeparation)
                continue;
            var progressDelta = ContinuousProgressDelta(
                previous.ProgressMeters,
                candidate.ProgressMeters,
                track.LengthMeters);
            var continuityPenalty = progressDelta < -slack
                ? (-progressDelta - slack) * 1.4
                : Math.Max(0, Math.Abs(progressDelta - expectedAdvance) - slack) * 0.2;
            var extremeAdvance = Math.Max(250, expectedAdvance * 8 + 80);
            if (progressDelta > extremeAdvance)
                continuityPenalty += (progressDelta - extremeAdvance) * 0.5;
            secondScore = Math.Min(
                secondScore,
                candidate.DistanceMeters + candidate.ElevationErrorMeters * 0.15 + continuityPenalty);
        }

        var ambiguous = secondScore - best.Score <= Math.Max(1.5, track.MatchingToleranceMeters * 0.12);
        return CreateProjection(track, position, best, ambiguous);
    }

    private static EstateRaceProjection CreateProjection(
        TrackTemplate track,
        Vector3F position,
        ProjectionCandidate candidate,
        bool isAmbiguous)
    {
        if (!candidate.IsValid) return new EstateRaceProjection(0, 0, 0.5, 0.5);

        var width = Math.Max(1, track.MaxX - track.MinX);
        var depth = Math.Max(1, track.MaxZ - track.MinZ);
        return new EstateRaceProjection(
            Math.Clamp(candidate.ProgressMeters / track.LengthMeters, 0, 1),
            Math.Clamp(candidate.SignedOffsetMeters, -500, 500),
            Math.Clamp((position.X - track.MinX) / width, 0, 1),
            Math.Clamp(1 - (position.Z - track.MinZ) / depth, 0, 1),
            candidate.SegmentIndex,
            candidate.ProgressMeters,
            candidate.DistanceMeters,
            candidate.ElevationErrorMeters,
            isAmbiguous);
    }

    private static ProjectionCandidate ProjectSegment(
        IReadOnlyList<TrackPoint> points,
        Vector3F position,
        int index,
        double score)
    {
        var start = points[index];
        var end = points[index + 1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dy * dy + dz * dz;
        if (lengthSquared < 0.0001) return ProjectionCandidate.Invalid;
        var amount = Math.Clamp(
            ((position.X - start.X) * dx + (position.Y - start.Y) * dy +
             (position.Z - start.Z) * dz) / lengthSquared,
            0,
            1);
        var projectedX = start.X + dx * amount;
        var projectedY = start.Y + dy * amount;
        var projectedZ = start.Z + dz * amount;
        var offsetX = position.X - projectedX;
        var offsetY = position.Y - projectedY;
        var offsetZ = position.Z - projectedZ;
        var distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY + offsetZ * offsetZ);
        var horizontalLength = Math.Sqrt(dx * dx + dz * dz);
        var cross = dx * offsetZ - dz * offsetX;
        var signedOffset = horizontalLength < 0.001
            ? 0
            : Math.Sqrt(offsetX * offsetX + offsetZ * offsetZ) * Math.Sign(cross);
        return new ProjectionCandidate(
            index,
            start.S + (end.S - start.S) * amount,
            distance,
            Math.Abs(offsetY),
            signedOffset,
            score);
    }

    private static double ContinuousProgressDelta(double previous, double current, double length)
    {
        var delta = current - previous;
        if (previous >= length * 0.75 && current <= length * 0.25) delta += length;
        else if (previous <= length * 0.25 && current >= length * 0.75) delta -= length;
        return delta;
    }

    private readonly record struct ProjectionCandidate(
        int SegmentIndex,
        double ProgressMeters,
        double DistanceMeters,
        double ElevationErrorMeters,
        double SignedOffsetMeters,
        double Score)
    {
        public bool IsValid => SegmentIndex >= 0;
        public static ProjectionCandidate Invalid => new(
            -1,
            0,
            double.PositiveInfinity,
            double.PositiveInfinity,
            0,
            double.PositiveInfinity);
    }

    public static IReadOnlyList<EstateRaceMapPoint> NormalizeOutline(
        TrackTemplate track,
        int maximumPoints = 360)
    {
        if (track.Points.Count == 0) return [];
        var step = Math.Max(1, (int)Math.Ceiling(track.Points.Count / (double)maximumPoints));
        var width = Math.Max(1, track.MaxX - track.MinX);
        var depth = Math.Max(1, track.MaxZ - track.MinZ);
        return track.Points
            .Where((_, index) => index % step == 0 || index == track.Points.Count - 1)
            .Select(point => new EstateRaceMapPoint(
                Math.Clamp((point.X - track.MinX) / width, 0, 1),
                Math.Clamp(1 - (point.Z - track.MinZ) / depth, 0, 1)))
            .ToArray();
    }

    public static IReadOnlyList<EstateRaceMapPoint> NormalizePitLane(
        TrackTemplate track,
        EstatePitDefinition? pit,
        int maximumPoints = 180)
    {
        if (pit?.CenterLine is not { Count: >= 2 } line) return [];
        var step = Math.Max(1, (int)Math.Ceiling(line.Count / (double)maximumPoints));
        var width = Math.Max(1, track.MaxX - track.MinX);
        var depth = Math.Max(1, track.MaxZ - track.MinZ);
        return line
            .Where((_, index) => index % step == 0 || index == line.Count - 1)
            .Select(point => new EstateRaceMapPoint(
                Math.Clamp((point.X - track.MinX) / width, 0, 1),
                Math.Clamp(1 - (point.Z - track.MinZ) / depth, 0, 1)))
            .ToArray();
    }

    public static EstateRaceMapGate NormalizeGate(
        TrackTemplate track,
        EstateTimingGate gate)
    {
        var width = Math.Max(1, track.MaxX - track.MinX);
        var depth = Math.Max(1, track.MaxZ - track.MinZ);
        EstateRaceMapPoint Normalize(EstateGatePoint point) => new(
            Math.Clamp((point.X - track.MinX) / width, 0, 1),
            Math.Clamp(1 - (point.Z - track.MinZ) / depth, 0, 1));
        return new EstateRaceMapGate(Normalize(gate.Left), Normalize(gate.Right));
    }

    public static IReadOnlyList<EstateRaceMapSector> NormalizeSectors(
        TrackTemplate track,
        IReadOnlyList<SectorDefinition>? sectors,
        int maximumPoints = 360)
    {
        if (track.Points.Count < 2 || sectors is not { Count: > 0 }) return [];
        var width = Math.Max(1, track.MaxX - track.MinX);
        var depth = Math.Max(1, track.MaxZ - track.MinZ);
        EstateRaceMapPoint Normalize(TrackPoint point) => new(
            Math.Clamp((point.X - track.MinX) / width, 0, 1),
            Math.Clamp(1 - (point.Z - track.MinZ) / depth, 0, 1));

        var result = new List<EstateRaceMapSector>(sectors.Count);
        var budget = Math.Max(4, maximumPoints / sectors.Count);
        foreach (var sector in sectors.OrderBy(item => item.Index))
        {
            var startIndex = 0;
            while (startIndex + 1 < track.Points.Count &&
                   track.Points[startIndex + 1].S <= sector.StartS)
                startIndex++;
            var endIndex = startIndex + 1;
            while (endIndex + 1 < track.Points.Count &&
                   track.Points[endIndex].S < sector.EndS)
                endIndex++;
            endIndex = Math.Min(track.Points.Count - 1, endIndex);
            if (endIndex <= startIndex) continue;
            var count = endIndex - startIndex + 1;
            var step = Math.Max(1, (int)Math.Ceiling(count / (double)budget));
            var points = Enumerable.Range(startIndex, count)
                .Where((_, index) => index % step == 0 || index == count - 1)
                .Select(index => Normalize(track.Points[index]))
                .ToArray();
            if (points.Length >= 2)
                result.Add(new EstateRaceMapSector(sector.Index, points));
        }
        return result;
    }

    public static bool IsInPitLane(EstatePitDefinition? pit, Vector3F position)
    {
        if (pit is null || pit.CenterLine.Count < 2) return false;
        var halfWidth = Math.Clamp(pit.LaneHalfWidthMeters, 1, 20);
        return DistanceToPitLane(pit, position) <= halfWidth;
    }

    public static double DistanceToPitLane(EstatePitDefinition? pit, Vector3F position) =>
        ProjectPitRoute(pit, position).DistanceMeters;

    public static EstatePitRouteProjection ProjectPitRoute(
        EstatePitDefinition? pit,
        Vector3F position) =>
        pit?.CenterLine is { Count: >= 2 } line
            ? ProjectPolyline(line, position.X, position.Y, position.Z)
            : new EstatePitRouteProjection(double.PositiveInfinity, 0, 0);

    public static double PitGateProgress(
        EstatePitDefinition? pit,
        EstateTimingGate gate)
    {
        var center = new Vector3F(
            (float)((gate.Left.X + gate.Right.X) / 2),
            (float)((gate.Left.Y + gate.Right.Y) / 2),
            (float)((gate.Left.Z + gate.Right.Z) / 2));
        return ProjectPitRoute(pit, center).ProgressMeters;
    }

    public static bool IsInServiceZone(
        EstatePitDefinition? pit,
        Vector3F position,
        double boundaryToleranceMeters = 0)
    {
        if (pit is null) return false;
        var tolerance = Math.Clamp(boundaryToleranceMeters, 0, 3);
        if (pit.ServiceZoneBoundary is { Count: >= 3 } polygon)
            return Math.Abs(position.Y - polygon.Average(point => point.Y)) <= 3 &&
                   (PointInPolygon(polygon, position.X, position.Z) ||
                    tolerance > 0 && DistanceToPolygon(polygon, position.X, position.Z) <= tolerance);
        var dx = position.X - pit.ServiceCenter.X;
        var dz = position.Z - pit.ServiceCenter.Z;
        var radius = Math.Max(0, pit.ServiceRadiusMeters) + tolerance;
        return dx * dx + dz * dz <= radius * radius &&
               Math.Abs(position.Y - pit.ServiceCenter.Y) <= 3;
    }

    public static bool CrossesForwardGate(
        EstateTimingGate gate,
        Vector3F previous,
        Vector3F current)
    {
        if (!gate.HasDirection) return false;
        var magnitude = Math.Sqrt(gate.ForwardX * gate.ForwardX + gate.ForwardZ * gate.ForwardZ);
        var forwardX = gate.ForwardX / magnitude;
        var forwardZ = gate.ForwardZ / magnitude;
        var previousSide = (previous.X - gate.Left.X) * forwardX +
                           (previous.Z - gate.Left.Z) * forwardZ;
        var currentSide = (current.X - gate.Left.X) * forwardX +
                          (current.Z - gate.Left.Z) * forwardZ;
        if (previousSide > 0.05 || currentSide < -0.05 || currentSide <= previousSide)
            return false;

        var interpolation = -previousSide / (currentSide - previousSide);
        if (interpolation is < 0 or > 1) return false;
        var x = previous.X + (current.X - previous.X) * interpolation;
        var y = previous.Y + (current.Y - previous.Y) * interpolation;
        var z = previous.Z + (current.Z - previous.Z) * interpolation;
        var tangentX = gate.Right.X - gate.Left.X;
        var tangentZ = gate.Right.Z - gate.Left.Z;
        var width = Math.Sqrt(tangentX * tangentX + tangentZ * tangentZ);
        if (width < 0.1) return false;
        tangentX /= width;
        tangentZ /= width;
        var along = (x - gate.Left.X) * tangentX + (z - gate.Left.Z) * tangentZ;
        if (along < -gate.EndpointMarginMeters || along > width + gate.EndpointMarginMeters)
            return false;
        var expectedY = gate.Left.Y + (gate.Right.Y - gate.Left.Y) * Math.Clamp(along / width, 0, 1);
        return Math.Abs(y - expectedY) <= gate.HeightToleranceMeters;
    }

    public static bool IsApproachingGate(
        EstateTimingGate gate,
        Vector3F position,
        double maximumDistanceMeters = 30,
        double lateralMarginMeters = 3)
    {
        if (!gate.HasDirection) return false;
        var magnitude = Math.Sqrt(gate.ForwardX * gate.ForwardX + gate.ForwardZ * gate.ForwardZ);
        var forwardX = gate.ForwardX / magnitude;
        var forwardZ = gate.ForwardZ / magnitude;
        var signedDistance = (position.X - gate.Left.X) * forwardX +
                             (position.Z - gate.Left.Z) * forwardZ;
        if (signedDistance is > 4 || signedDistance < -maximumDistanceMeters) return false;

        var tangentX = gate.Right.X - gate.Left.X;
        var tangentZ = gate.Right.Z - gate.Left.Z;
        var width = Math.Sqrt(tangentX * tangentX + tangentZ * tangentZ);
        if (width < 0.1) return false;
        tangentX /= width;
        tangentZ /= width;
        var along = (position.X - gate.Left.X) * tangentX +
                    (position.Z - gate.Left.Z) * tangentZ;
        var margin = Math.Clamp(lateralMarginMeters, 0.5, 10);
        return along >= -margin && along <= width + margin;
    }

    public static bool IsApproachingPitEntry(EstatePitDefinition? pit, Vector3F position)
    {
        if (pit is null) return false;
        return IsApproachingPitEntry(
            pit,
            position,
            ProjectPitRoute(pit, position),
            PitGateProgress(pit, pit.EntryGate));
    }

    public static bool IsApproachingPitEntry(
        EstatePitDefinition pit,
        Vector3F position,
        EstatePitRouteProjection route,
        double entryProgress)
    {
        // The recorded centre line starts at the racing-line split, while the
        // deterministic entry gate defines where enforcement begins. Following
        // the recorded branch avoids lighting the limiter on the final corner,
        // but still gives the driver the cue before reaching the entry line.
        var routeApproach = route.TotalLengthMeters > 0 &&
                            route.DistanceMeters <= Math.Clamp(pit.LaneHalfWidthMeters, 1, 20) * 1.35 + 0.75 &&
                            route.ProgressMeters <= entryProgress + 0.75;
        if (routeApproach) return true;

        // Compatibility fallback for older pit definitions whose captured
        // centre line starts at the entry gate rather than at the split.
        var lookAhead = Math.Clamp(pit.SpeedLimitKph * 0.20, 12, 20);
        return IsApproachingGate(
            pit.EntryGate,
            position,
            lookAhead,
            Math.Clamp(pit.LaneHalfWidthMeters * 0.55, 1.5, 2.5));
    }

    private static EstatePitRouteProjection ProjectPolyline(
        IReadOnlyList<EstateGatePoint> line,
        double x,
        double y,
        double z)
    {
        var best = double.MaxValue;
        var bestProgress = 0d;
        var progress = 0d;
        for (var index = 0; index < line.Count - 1; index++)
        {
            var start = line[index];
            var end = line[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared < 0.0001) continue;
            var length = Math.Sqrt(lengthSquared);
            var t = Math.Clamp(((x - start.X) * dx + (y - start.Y) * dy + (z - start.Z) * dz) / lengthSquared, 0, 1);
            var distance = Math.Sqrt(
                Math.Pow(x - (start.X + dx * t), 2) +
                Math.Pow(y - (start.Y + dy * t), 2) +
                Math.Pow(z - (start.Z + dz * t), 2));
            if (distance < best)
            {
                best = distance;
                bestProgress = progress + length * t;
            }
            progress += length;
        }
        return new EstatePitRouteProjection(best, bestProgress, progress);
    }

    private static bool PointInPolygon(
        IReadOnlyList<EstateGatePoint> polygon,
        double x,
        double z)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var currentPoint = polygon[index];
            var previousPoint = polygon[previous];
            if ((currentPoint.Z > z) == (previousPoint.Z > z)) continue;
            var crossingX = (previousPoint.X - currentPoint.X) * (z - currentPoint.Z) /
                            (previousPoint.Z - currentPoint.Z) + currentPoint.X;
            if (x < crossingX) inside = !inside;
        }
        return inside;
    }

    private static double DistanceToPolygon(
        IReadOnlyList<EstateGatePoint> polygon,
        double x,
        double z)
    {
        var bestSquared = double.PositiveInfinity;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var start = polygon[previous];
            var end = polygon[index];
            var dx = end.X - start.X;
            var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dz * dz;
            var amount = lengthSquared <= 1e-9
                ? 0
                : Math.Clamp(((x - start.X) * dx + (z - start.Z) * dz) / lengthSquared, 0, 1);
            var offsetX = x - (start.X + dx * amount);
            var offsetZ = z - (start.Z + dz * amount);
            bestSquared = Math.Min(bestSquared, offsetX * offsetX + offsetZ * offsetZ);
        }
        return Math.Sqrt(bestSquared);
    }
}
