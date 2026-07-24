using LazyForza.Domain;

namespace LazyForza.Analysis;

public readonly record struct ChartViewport(double Zoom, double OffsetX, double OffsetY);

public readonly record struct ScreenHitPoint(
    int SeriesIndex,
    int SampleIndex,
    double X,
    double Y);

public sealed class ScreenHitGrid
{
    private readonly Dictionary<long, List<ScreenHitPoint>> cells = [];
    private readonly double cellSize;

    public ScreenHitGrid(IEnumerable<ScreenHitPoint> points, double cellSize = 24)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        this.cellSize = cellSize;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            var key = CellKey(CellCoordinate(point.X), CellCoordinate(point.Y));
            if (!cells.TryGetValue(key, out var bucket))
            {
                bucket = [];
                cells.Add(key, bucket);
            }

            bucket.Add(point);
        }
    }

    public bool TryFindNearest(
        double x,
        double y,
        double radius,
        out ScreenHitPoint nearest)
    {
        nearest = default;
        if (!double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(radius) ||
            radius <= 0)
            return false;

        var minimumCellX = CellCoordinate(x - radius);
        var maximumCellX = CellCoordinate(x + radius);
        var minimumCellY = CellCoordinate(y - radius);
        var maximumCellY = CellCoordinate(y + radius);
        var bestDistanceSquared = radius * radius;
        var found = false;

        for (var cellY = minimumCellY; cellY <= maximumCellY; cellY++)
        {
            for (var cellX = minimumCellX; cellX <= maximumCellX; cellX++)
            {
                if (!cells.TryGetValue(CellKey(cellX, cellY), out var bucket)) continue;
                foreach (var candidate in bucket)
                {
                    var deltaX = candidate.X - x;
                    var deltaY = candidate.Y - y;
                    var distanceSquared = deltaX * deltaX + deltaY * deltaY;
                    if (distanceSquared >= bestDistanceSquared) continue;
                    bestDistanceSquared = distanceSquared;
                    nearest = candidate;
                    found = true;
                }
            }
        }

        return found;
    }

    private int CellCoordinate(double value) => (int)Math.Floor(value / cellSize);

    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;
}

public readonly record struct TrackEndpointSummary(
    double StartX,
    double StartZ,
    double FinishX,
    double FinishZ);

public static class ChartInteractionAlgorithms
{
    public static TrackEndpointSummary? SummarizeTrackEndpoints(
        IReadOnlyList<TrackPoint> points)
    {
        if (points.Count < 2) return null;
        return new TrackEndpointSummary(
            points[0].X,
            points[0].Z,
            points[^1].X,
            points[^1].Z);
    }

    public static TrackEndpointSummary? SummarizeTrackEndpoints(
        IEnumerable<IReadOnlyList<LapSample>> series)
    {
        var endpoints = series
            .Where(samples => samples.Count >= 2)
            .Select(samples => (
                Start: samples[0],
                Finish: samples[^1]))
            .ToArray();
        if (endpoints.Length == 0) return null;

        return new TrackEndpointSummary(
            endpoints.Average(endpoint => endpoint.Start.X),
            endpoints.Average(endpoint => endpoint.Start.Z),
            endpoints.Average(endpoint => endpoint.Finish.X),
            endpoints.Average(endpoint => endpoint.Finish.Z));
    }

    public static double ResolveProgressExtent(
        IEnumerable<IReadOnlyList<LapSample>> series,
        double? trackLengthMeters = null)
    {
        var sampleMaximum = series
            .Where(samples => samples.Count > 0)
            .SelectMany(samples => samples)
            .Select(sample => sample.S)
            .Where(double.IsFinite)
            .DefaultIfEmpty(0)
            .Max();
        var templateMaximum = trackLengthMeters is > 0 && double.IsFinite(trackLengthMeters.Value)
            ? trackLengthMeters.Value
            : 0;
        return Math.Max(1, Math.Max(sampleMaximum, templateMaximum));
    }

    public static int FindNearestProgressSample(IReadOnlyList<LapSample> samples, double progress)
    {
        if (samples.Count == 0) return -1;
        var low = 0;
        var high = samples.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (samples[middle].S < progress) low = middle + 1;
            else high = middle - 1;
        }

        if (low <= 0) return 0;
        if (low >= samples.Count) return samples.Count - 1;
        return Math.Abs(samples[low].S - progress) < Math.Abs(samples[low - 1].S - progress)
            ? low
            : low - 1;
    }

    public static ChartViewport ZoomAroundCursor(
        ChartViewport current,
        double cursorX,
        double cursorY,
        double centerX,
        double centerY,
        double wheelDelta,
        double minimumZoom = 1,
        double maximumZoom = 24)
    {
        minimumZoom = Math.Max(0.01, minimumZoom);
        maximumZoom = Math.Max(minimumZoom, maximumZoom);
        var oldZoom = Math.Clamp(
            double.IsFinite(current.Zoom) ? current.Zoom : minimumZoom,
            minimumZoom,
            maximumZoom);
        var factor = Math.Exp(Math.Clamp(wheelDelta, -1_200, 1_200) * 0.001);
        var nextZoom = Math.Clamp(oldZoom * factor, minimumZoom, maximumZoom);
        if (nextZoom <= minimumZoom + 0.000_001)
            return new ChartViewport(minimumZoom, 0, 0);

        var appliedFactor = nextZoom / oldZoom;
        var offsetX = current.OffsetX +
                      (cursorX - centerX - current.OffsetX) * (1 - appliedFactor);
        var offsetY = current.OffsetY +
                      (cursorY - centerY - current.OffsetY) * (1 - appliedFactor);
        return new ChartViewport(nextZoom, offsetX, offsetY);
    }
}
