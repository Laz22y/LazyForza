using System.Windows;
using System.Windows.Media;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed class EstateRaceMapGeometryCache
{
    private readonly Dictionary<IReadOnlyList<EstateRaceMapPoint>, StreamGeometry> sectorGeometries =
        new(ReferenceEqualityComparer.Instance);
    private Rect bounds = Rect.Empty;
    private IReadOnlyList<EstateRaceMapPoint>? trackPoints;
    private IReadOnlyList<EstateRaceMapPoint>? pitPoints;
    private StreamGeometry? trackGeometry;
    private StreamGeometry? pitGeometry;

    internal int BuildCount { get; private set; }

    public StreamGeometry Track(IReadOnlyList<EstateRaceMapPoint> points, Rect map)
    {
        EnsureBounds(map);
        if (!ReferenceEquals(trackPoints, points) || trackGeometry is null)
        {
            trackPoints = points;
            trackGeometry = Build(points, map);
        }
        return trackGeometry;
    }

    public StreamGeometry Pit(IReadOnlyList<EstateRaceMapPoint> points, Rect map)
    {
        EnsureBounds(map);
        if (!ReferenceEquals(pitPoints, points) || pitGeometry is null)
        {
            pitPoints = points;
            pitGeometry = Build(points, map);
        }
        return pitGeometry;
    }

    public StreamGeometry Sector(IReadOnlyList<EstateRaceMapPoint> points, Rect map)
    {
        EnsureBounds(map);
        if (sectorGeometries.TryGetValue(points, out var geometry)) return geometry;
        geometry = Build(points, map);
        sectorGeometries[points] = geometry;
        return geometry;
    }

    private void EnsureBounds(Rect map)
    {
        if (bounds == map) return;
        bounds = map;
        trackPoints = null;
        pitPoints = null;
        trackGeometry = null;
        pitGeometry = null;
        sectorGeometries.Clear();
    }

    private StreamGeometry Build(IReadOnlyList<EstateRaceMapPoint> points, Rect map)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = points[0];
            context.BeginFigure(Point(first, map), false, false);
            for (var index = 1; index < points.Count; index++)
                context.LineTo(Point(points[index], map), true, false);
        }
        geometry.Freeze();
        BuildCount++;
        return geometry;
    }

    private static Point Point(EstateRaceMapPoint point, Rect map) => new(
        map.Left + point.X * map.Width,
        map.Top + point.Y * map.Height);
}
