using LazyForza.Domain;

namespace LazyForza.Analysis;

/// <summary>
/// Immutable X/Z grid over three-dimensional route segments. The grid only
/// narrows the segment set; the final projection still uses X/Y/Z so stacked
/// roads remain distinguishable.
/// </summary>
public sealed class TrackSpatialIndex
{
    [ThreadStatic]
    private static HashSet<int>? reusableSegmentBuffer;

    private readonly IReadOnlyList<TrackPoint> route;
    private readonly Dictionary<GridCell, int[]> segmentsByCell;
    private readonly double cellSizeMeters;

    public TrackSpatialIndex(
        IReadOnlyList<TrackPoint> route,
        double cellSizeMeters = 80,
        IEnumerable<int>? includedSegmentIndices = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!double.IsFinite(cellSizeMeters) || cellSizeMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters));

        this.route = route;
        this.cellSizeMeters = cellSizeMeters;
        var segmentCount = Math.Max(0, route.Count - 1);
        var included = includedSegmentIndices?.Distinct().ToArray() ??
                       Enumerable.Range(0, segmentCount).ToArray();
        var mutable = new Dictionary<GridCell, List<int>>();
        foreach (var segmentIndex in included)
        {
            if (segmentIndex < 0 || segmentIndex >= segmentCount) continue;
            var start = route[segmentIndex];
            var end = route[segmentIndex + 1];
            var minX = Cell(Math.Min(start.X, end.X));
            var maxX = Cell(Math.Max(start.X, end.X));
            var minZ = Cell(Math.Min(start.Z, end.Z));
            var maxZ = Cell(Math.Max(start.Z, end.Z));
            for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
            {
                var cell = new GridCell(x, z);
                if (!mutable.TryGetValue(cell, out var segments))
                {
                    segments = [];
                    mutable[cell] = segments;
                }
                segments.Add(segmentIndex);
            }
        }

        segmentsByCell = mutable.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct().Order().ToArray());
        IndexedSegmentCount = included.Count(index => index >= 0 && index < segmentCount);
    }

    public int IndexedSegmentCount { get; }

    public ProjectionResult ProjectNearest(
        double x,
        double y,
        double z,
        double searchRadiusMeters)
    {
        ValidateSearchRadius(searchRadiusMeters);
        var segmentIndices = reusableSegmentBuffer ??= [];
        segmentIndices.Clear();
        CollectSegmentIndices(x, z, searchRadiusMeters, segmentIndices);
        return segmentIndices.Count == 0
            ? ProjectionResult.Invalid
            : TrackAlgorithms.ProjectSegments(route, x, y, z, segmentIndices);
    }

    public IReadOnlyList<int> QuerySegmentIndices(
        double x,
        double z,
        double searchRadiusMeters)
    {
        ValidateSearchRadius(searchRadiusMeters);
        var result = new HashSet<int>();
        CollectSegmentIndices(x, z, searchRadiusMeters, result);
        return result.Order().ToArray();
    }

    private void CollectSegmentIndices(
        double x,
        double z,
        double searchRadiusMeters,
        HashSet<int> result)
    {
        var minX = Cell(x - searchRadiusMeters);
        var maxX = Cell(x + searchRadiusMeters);
        var minZ = Cell(z - searchRadiusMeters);
        var maxZ = Cell(z + searchRadiusMeters);
        for (var cellX = minX; cellX <= maxX; cellX++)
        for (var cellZ = minZ; cellZ <= maxZ; cellZ++)
        {
            if (!segmentsByCell.TryGetValue(new GridCell(cellX, cellZ), out var segments)) continue;
            foreach (var segment in segments) result.Add(segment);
        }
    }

    private static void ValidateSearchRadius(double searchRadiusMeters)
    {
        if (!double.IsFinite(searchRadiusMeters) || searchRadiusMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(searchRadiusMeters));
    }

    private int Cell(double coordinate) => (int)Math.Floor(coordinate / cellSizeMeters);

    private readonly record struct GridCell(int X, int Z);
}
