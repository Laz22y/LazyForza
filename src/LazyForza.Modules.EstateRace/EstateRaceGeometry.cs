using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal readonly record struct EstateRaceProjection(
    double Progress,
    double LateralOffsetMeters,
    double MapX,
    double MapY);

internal static class EstateRaceGeometry
{
    public static EstateRaceProjection Project(
        TrackTemplate track,
        Vector3F position)
    {
        if (track.Points.Count < 2 || track.LengthMeters <= 0)
            return new EstateRaceProjection(0, 0, 0.5, 0.5);

        var bestDistanceSquared = double.MaxValue;
        var bestProgress = 0d;
        var bestSignedOffset = 0d;
        for (var index = 0; index < track.Points.Count - 1; index++)
        {
            var start = track.Points[index];
            var end = track.Points[index + 1];
            var dx = end.X - start.X;
            var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dz * dz;
            if (lengthSquared < 0.0001) continue;
            var t = Math.Clamp(
                ((position.X - start.X) * dx + (position.Z - start.Z) * dz) /
                lengthSquared,
                0,
                1);
            var projectedX = start.X + dx * t;
            var projectedZ = start.Z + dz * t;
            var offsetX = position.X - projectedX;
            var offsetZ = position.Z - projectedZ;
            var distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            bestProgress = start.S + (end.S - start.S) * t;
            var cross = dx * offsetZ - dz * offsetX;
            bestSignedOffset = Math.Sqrt(distanceSquared) * Math.Sign(cross);
        }

        var width = Math.Max(1, track.MaxX - track.MinX);
        var depth = Math.Max(1, track.MaxZ - track.MinZ);
        return new EstateRaceProjection(
            Math.Clamp(bestProgress / track.LengthMeters, 0, 1),
            Math.Clamp(bestSignedOffset, -500, 500),
            Math.Clamp((position.X - track.MinX) / width, 0, 1),
            Math.Clamp(1 - (position.Z - track.MinZ) / depth, 0, 1));
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

    public static bool IsInPitLane(EstatePitDefinition? pit, Vector3F position)
    {
        if (pit is null || pit.CenterLine.Count < 2) return false;
        var halfWidth = Math.Clamp(pit.LaneHalfWidthMeters, 1, 20);
        return DistanceToPolyline(pit.CenterLine, position.X, position.Y, position.Z) <= halfWidth;
    }

    public static bool IsInServiceZone(EstatePitDefinition? pit, Vector3F position)
    {
        if (pit is null) return false;
        if (pit.ServiceZoneBoundary is { Count: >= 3 } polygon)
            return PointInPolygon(polygon, position.X, position.Z) &&
                   Math.Abs(position.Y - polygon.Average(point => point.Y)) <= 3;
        var dx = position.X - pit.ServiceCenter.X;
        var dz = position.Z - pit.ServiceCenter.Z;
        return dx * dx + dz * dz <= pit.ServiceRadiusMeters * pit.ServiceRadiusMeters &&
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
        // Keep the limiter cue close to the deterministic entry line. A longer
        // gate projection can intersect the racing line before a final corner
        // and show the cue to drivers who are not actually entering the pits.
        var lookAhead = Math.Clamp(pit.SpeedLimitKph * 0.20, 12, 20);
        return IsApproachingGate(
            pit.EntryGate,
            position,
            lookAhead,
            Math.Clamp(pit.LaneHalfWidthMeters * 0.55, 1.5, 2.5));
    }

    private static double DistanceToPolyline(
        IReadOnlyList<EstateGatePoint> line,
        double x,
        double y,
        double z)
    {
        var best = double.MaxValue;
        for (var index = 0; index < line.Count - 1; index++)
        {
            var start = line[index];
            var end = line[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared < 0.0001) continue;
            var t = Math.Clamp(((x - start.X) * dx + (y - start.Y) * dy + (z - start.Z) * dz) / lengthSquared, 0, 1);
            var distance = Math.Sqrt(
                Math.Pow(x - (start.X + dx * t), 2) +
                Math.Pow(y - (start.Y + dy * t), 2) +
                Math.Pow(z - (start.Z + dz * t), 2));
            best = Math.Min(best, distance);
        }
        return best;
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
}
