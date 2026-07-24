using LazyForza.Domain;

namespace LazyForza.Analysis;

public static class TrackAlgorithms
{
    public const string SectorAlgorithmVersion = "sector-v1.1.0-start-line";
    public const int SectorSchemaVersion = 2;

    public static IReadOnlyList<TrackPoint> Resample(IReadOnlyList<TrackPoint> input, double spacingMeters = 5)
    {
        if (input.Count < 2 || spacingMeters <= 0) return input.ToArray();
        var cumulative = new double[input.Count];
        for (var index = 1; index < input.Count; index++)
        {
            cumulative[index] = cumulative[index - 1] + Math.Sqrt(input[index].DistanceSquaredTo(input[index - 1]));
        }

        var length = cumulative[^1];
        if (length <= spacingMeters) return input.ToArray();
        var result = new List<TrackPoint>();
        var segment = 1;
        for (var distance = 0d; distance < length; distance += spacingMeters)
        {
            while (segment < cumulative.Length - 1 && cumulative[segment] < distance) segment++;
            var lower = segment - 1;
            var range = cumulative[segment] - cumulative[lower];
            var amount = range <= 0 ? 0 : (distance - cumulative[lower]) / range;
            var x = Lerp(input[lower].X, input[segment].X, amount);
            var y = Lerp(input[lower].Y, input[segment].Y, amount);
            var z = Lerp(input[lower].Z, input[segment].Z, amount);
            var dx = input[segment].X - input[lower].X;
            var dz = input[segment].Z - input[lower].Z;
            var magnitude = Math.Sqrt(dx * dx + dz * dz);
            result.Add(new TrackPoint(x, y, z, distance, magnitude > 0 ? dx / magnitude : 0, magnitude > 0 ? dz / magnitude : 0));
        }

        var last = input[^1];
        result.Add(last with { S = length });
        return Smooth(result);
    }

    public static TrackTemplate BuildTemplate(
        string name,
        IReadOnlyList<TrackPoint> rawPoints,
        int direction = 1,
        TrackLayoutKind layoutKind = TrackLayoutKind.Circuit)
    {
        var points = Resample(rawPoints);
        if (points.Count < 4) throw new ArgumentException("A route template needs at least four spatial samples.", nameof(rawPoints));
        var now = DateTimeOffset.UtcNow;
        return new TrackTemplate(
            Guid.NewGuid(), name, Math.Sign(direction) == 0 ? 1 : Math.Sign(direction), "user_learned", null,
            points, points[^1].S,
            points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z),
            points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z),
            18, 0.55, 1, now, now)
        {
            LayoutKind = layoutKind
        };
    }

    public static TrackLayoutKind InferLayout(IReadOnlyList<TrackPoint> points, double circuitClosureMeters = 40)
    {
        if (points.Count < 2) return TrackLayoutKind.Circuit;
        var closureDistance = Math.Sqrt(points[0].DistanceSquaredTo(points[^1]));
        return closureDistance <= circuitClosureMeters
            ? TrackLayoutKind.Circuit
            : TrackLayoutKind.PointToPoint;
    }

    public static IReadOnlyList<SectorDefinition> CreateSectors(TrackTemplate track, IReadOnlyList<LapSample>? validLap = null)
    {
        var targetCount = Math.Clamp((int)Math.Round(track.LengthMeters / 350), 4, 16);
        var candidates = new SortedSet<double>();
        if (validLap is not null && validLap.Count > 0)
        {
            for (var index = 1; index < validLap.Count; index++)
            {
                if (validLap[index - 1].Brake < 0.15 && validLap[index].Brake >= 0.35)
                {
                    candidates.Add(validLap[index].S);
                }
            }
        }

        var boundaries = new List<double> { 0 };
        foreach (var candidate in candidates)
        {
            if (candidate - boundaries[^1] >= 120 && track.LengthMeters - candidate >= 120)
            {
                boundaries.Add(candidate);
                if (boundaries.Count == targetCount) break;
            }
        }

        while (boundaries.Count < targetCount)
        {
            var ideal = track.LengthMeters * boundaries.Count / targetCount;
            if (boundaries.All(existing => Math.Abs(existing - ideal) >= 80)) boundaries.Add(ideal);
            else boundaries.Add(track.LengthMeters * (boundaries.Count + 0.35) / targetCount);
        }

        boundaries = boundaries.Order().Take(targetCount).ToList();
        var sectors = new List<SectorDefinition>();
        for (var index = 0; index < targetCount; index++)
        {
            var start = boundaries[index];
            var end = index + 1 < boundaries.Count ? boundaries[index + 1] : track.LengthMeters;
            sectors.Add(new SectorDefinition(track.Id, SectorSchemaVersion, index, start, end,
                candidates.Any(candidate => Math.Abs(candidate - start) < 10) ? SectorFeatureType.Braking : SectorFeatureType.EqualDistance,
                SectorAlgorithmVersion));
        }

        return sectors;
    }

    public static ProjectionResult ProjectConstrained(
        IReadOnlyList<TrackPoint> route,
        double x,
        double y,
        double z,
        int previousSegmentIndex,
        int searchBehind = 4,
        int searchAhead = 20)
    {
        if (route.Count < 2) return ProjectionResult.Invalid;
        var start = Math.Clamp(previousSegmentIndex - searchBehind, 0, route.Count - 2);
        var end = Math.Clamp(previousSegmentIndex + searchAhead, 0, route.Count - 2);
        return ProjectRange(route, x, y, z, start, end);
    }

    public static ProjectionResult ProjectRange(
        IReadOnlyList<TrackPoint> route,
        double x,
        double y,
        double z,
        int startSegmentIndex,
        int endSegmentIndex)
    {
        if (route.Count < 2) return ProjectionResult.Invalid;
        var start = Math.Clamp(startSegmentIndex, 0, route.Count - 2);
        var end = Math.Clamp(endSegmentIndex, start, route.Count - 2);
        var best = ProjectionResult.Invalid;
        for (var index = start; index <= end; index++)
        {
            var a = route[index];
            var b = route[index + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared <= 0) continue;
            var amount = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy + (z - a.Z) * dz) / lengthSquared, 0, 1);
            var px = a.X + amount * dx;
            var py = a.Y + amount * dy;
            var pz = a.Z + amount * dz;
            var distanceSquared = (x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz);
            if (!best.IsValid || distanceSquared < best.DistanceMeters * best.DistanceMeters)
            {
                best = new ProjectionResult(true, index, a.S + amount * (b.S - a.S), Math.Sqrt(distanceSquared), Math.Abs(y - py));
            }
        }

        return best;
    }

    public static double MinimumDistanceMeters(
        IReadOnlyList<TrackPoint> route,
        double x,
        double y,
        double z) =>
        ProjectRange(route, x, y, z, 0, route.Count - 2).DistanceMeters;

    private static IReadOnlyList<TrackPoint> Smooth(IReadOnlyList<TrackPoint> input)
    {
        if (input.Count < 5) return input.ToArray();
        var result = input.ToArray();
        for (var index = 2; index < input.Count - 2; index++)
        {
            result[index] = input[index] with
            {
                X = (input[index - 1].X + 2 * input[index].X + input[index + 1].X) / 4,
                Y = (input[index - 1].Y + 2 * input[index].Y + input[index + 1].Y) / 4,
                Z = (input[index - 1].Z + 2 * input[index].Z + input[index + 1].Z) / 4
            };
        }

        return result;
    }

    private static double Lerp(double left, double right, double amount) => left + ((right - left) * amount);
}

public readonly record struct ProjectionResult(bool IsValid, int SegmentIndex, double S, double DistanceMeters, double ElevationErrorMeters)
{
    public static ProjectionResult Invalid => new(false, -1, 0, double.PositiveInfinity, double.PositiveInfinity);
}

public static class SectorColorClassifier
{
    public const string DatasetBestExplanation = "颜色：灰 未跑；黄 较慢；绿 本场最快；紫 本机历史最快（不是在线世界纪录）。";

    public static SectorColorState Classify(
        double? currentSeconds,
        bool currentValid,
        double? currentSessionBestSeconds,
        double? allTimeBestSeconds,
        bool considerCurrentSessionBest = true)
    {
        if (!currentValid || currentSeconds is null || currentSeconds <= 0)
            return SectorColorState.Gray;
        const double epsilon = 0.001;
        if (allTimeBestSeconds is null)
            return SectorColorState.Purple;
        if (allTimeBestSeconds is not null && currentSeconds <= allTimeBestSeconds + epsilon)
            return SectorColorState.Purple;
        if (!considerCurrentSessionBest)
            return SectorColorState.Yellow;
        if (currentSessionBestSeconds is null || currentSeconds <= currentSessionBestSeconds + epsilon)
            return SectorColorState.Green;
        return SectorColorState.Yellow;
    }
}
