using System.Windows;
using System.Windows.Media;

namespace LazyForza.App;

/// <summary>A small route silhouette. It uses supplied route geometry and has no telemetry subscription.</summary>
internal sealed class OverviewTrackMap : FrameworkElement
{
    private Point[] points = [];
    private object? routeKey;

    internal void SetRoute(object? key, IEnumerable<Point> route)
    {
        if (Equals(routeKey, key)) return;
        routeKey = key;
        var raw = route.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
        var step = Math.Max(1, (int)Math.Ceiling(raw.Length / 512d));
        points = raw.Where((_, index) => index % step == 0 || index == raw.Length - 1).ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (points.Length < 2 || ActualWidth < 32 || ActualHeight < 32) return;
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var spanX = Math.Max(1, points.Max(p => p.X) - minX);
        var spanY = Math.Max(1, points.Max(p => p.Y) - minY);
        var scale = Math.Min((ActualWidth - 24) / spanX, (ActualHeight - 24) / spanY);
        Point Project(Point point) => new((ActualWidth - spanX * scale) / 2 + (point.X - minX) * scale,
            (ActualHeight - spanY * scale) / 2 + (point.Y - minY) * scale);
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(Project(points[0]), false, false);
            path.PolyLineTo(points.Skip(1).Select(Project).ToArray(), true, false);
        }
        shape.Freeze();
        var basePen = new Pen(new SolidColorBrush(Color.FromRgb(43, 62, 76)), 8) { LineJoin = PenLineJoin.Round };
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(124, 160, 181)), 2.5) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, basePen, shape);
        dc.DrawGeometry(null, linePen, shape);
        dc.DrawEllipse((Brush)FindResource("AccentBrush"), null, Project(points[0]), 3.5, 3.5);
    }
}
