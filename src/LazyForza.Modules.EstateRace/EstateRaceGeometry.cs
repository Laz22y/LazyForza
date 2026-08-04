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
