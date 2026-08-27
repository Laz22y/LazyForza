using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class EstateGeometryPreview : FrameworkElement
{
    private IReadOnlyList<TrackPoint> route = [];
    private IReadOnlyList<TrackPoint> captureRoute = [];
    private IReadOnlyList<EstateGatePoint> firstTrace = [];
    private IReadOnlyList<EstateGatePoint> secondTrace = [];
    private IReadOnlyList<EstateGatePoint> directionTrace = [];
    private IReadOnlyList<EstateGatePoint> pitLane = [];
    private IReadOnlyList<EstateGatePoint> serviceZone = [];
    private IReadOnlyList<EstateCheckpoint> checkpoints = [];
    private EstateTimingGate? startFinish;
    private EstateTimingGate? pitEntry;
    private EstateTimingGate? pitExit;
    private EstateGatePoint? current;

    public EstateGeometryPreview()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MinHeight = 260;
    }

    public void Update(EstateEnrollmentPreview preview)
    {
        route = preview.ReferenceRoute;
        captureRoute = preview.CaptureRoute;
        firstTrace = preview.FirstTrace;
        secondTrace = preview.SecondTrace;
        directionTrace = preview.DirectionTrace;
        checkpoints = preview.Checkpoints;
        startFinish = preview.Gate;
        current = preview.CurrentPosition;
        pitLane = [];
        serviceZone = [];
        pitEntry = null;
        pitExit = null;
        InvalidateVisual();
    }

    public void Update(TrackTemplate track, EstateTrackDefinition definition)
    {
        route = track.Points;
        captureRoute = [];
        firstTrace = [];
        secondTrace = [];
        directionTrace = [];
        checkpoints = definition.Checkpoints;
        startFinish = definition.StartFinishGate;
        pitLane = definition.Pit?.CenterLine ?? [];
        serviceZone = definition.Pit?.ServiceZoneBoundary ?? [];
        pitEntry = definition.Pit?.EntryGate;
        pitExit = definition.Pit?.ExitGate;
        current = null;
        InvalidateVisual();
    }

    public void Update(TrackTemplate track, EstateTrackDefinition definition, EstatePitEnrollmentPreview preview)
    {
        Update(track, definition);
        pitLane = preview.CenterLine;
        serviceZone = preview.ServiceZoneBoundary;
        pitEntry = preview.EntryGate;
        pitExit = preview.ExitGate;
        current = preview.CurrentPosition;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(10, 18, 26)),
            new Pen(new SolidColorBrush(Color.FromRgb(39, 58, 72)), 1),
            bounds,
            10,
            10);
        DrawGrid(drawingContext, bounds);

        var points = AllPoints().ToArray();
        if (points.Length < 2)
        {
            DrawEmptyState(drawingContext, bounds);
            return;
        }

        var transform = CreateTransform(points, bounds);
        var accent = Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush accentBrush
            ? accentBrush.Color
            : Color.FromRgb(32, 184, 207);
        DrawTrackPoints(drawingContext, route, transform,
            Color.FromArgb(72, 94, 125, 145), 7, Color.FromRgb(90, 174, 197), 2.3);
        DrawTrackPoints(drawingContext, captureRoute, transform,
            Color.FromArgb(70, accent.R, accent.G, accent.B), 7, accent, 2.6);
        DrawGatePoints(drawingContext, firstTrace, transform, Color.FromRgb(54, 190, 229), 2);
        DrawGatePoints(drawingContext, secondTrace, transform, Color.FromRgb(206, 103, 230), 2);
        DrawGatePoints(drawingContext, directionTrace, transform, Color.FromRgb(74, 214, 143), 2.2);
        DrawGatePoints(drawingContext, pitLane, transform, Color.FromRgb(169, 115, 232), 3);
        DrawPolygon(drawingContext, serviceZone, transform);

        foreach (var checkpoint in checkpoints.Where((_, index) => index % Math.Max(1, checkpoints.Count / 18) == 0))
            DrawGate(drawingContext, checkpoint.Gate, transform, Color.FromArgb(90, 116, 155, 176), 1);
        if (startFinish is not null) DrawGate(drawingContext, startFinish, transform, Color.FromRgb(242, 184, 39), 3);
        if (pitEntry is not null) DrawGate(drawingContext, pitEntry, transform, Color.FromRgb(57, 217, 138), 2.4);
        if (pitExit is not null) DrawGate(drawingContext, pitExit, transform, Color.FromRgb(255, 132, 78), 2.4);
        if (current is EstateGatePoint car)
        {
            var point = transform(car);
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(246, 250, 252)),
                new Pen(new SolidColorBrush(Color.FromRgb(16, 24, 31)), 2), point, 5, 5);
        }
    }

    private IEnumerable<EstateGatePoint> AllPoints()
    {
        foreach (var point in route) yield return new EstateGatePoint(point.X, point.Y, point.Z);
        foreach (var point in captureRoute) yield return new EstateGatePoint(point.X, point.Y, point.Z);
        foreach (var point in firstTrace) yield return point;
        foreach (var point in secondTrace) yield return point;
        foreach (var point in directionTrace) yield return point;
        foreach (var point in pitLane) yield return point;
        foreach (var point in serviceZone) yield return point;
        if (startFinish is not null) { yield return startFinish.Left; yield return startFinish.Right; }
        if (pitEntry is not null) { yield return pitEntry.Left; yield return pitEntry.Right; }
        if (pitExit is not null) { yield return pitExit.Left; yield return pitExit.Right; }
        if (current is EstateGatePoint car) yield return car;
    }

    private static Func<EstateGatePoint, Point> CreateTransform(IReadOnlyList<EstateGatePoint> points, Rect bounds)
    {
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minZ = points.Min(point => point.Z);
        var maxZ = points.Max(point => point.Z);
        var spanX = Math.Max(1, maxX - minX);
        var spanZ = Math.Max(1, maxZ - minZ);
        const double padding = 24;
        var width = Math.Max(1, bounds.Width - padding * 2);
        var height = Math.Max(1, bounds.Height - padding * 2);
        var scale = Math.Min(width / spanX, height / spanZ);
        var drawnWidth = spanX * scale;
        var drawnHeight = spanZ * scale;
        var offsetX = (bounds.Width - drawnWidth) / 2;
        var offsetY = (bounds.Height - drawnHeight) / 2;
        return point => new Point(
            offsetX + (point.X - minX) * scale,
            offsetY + drawnHeight - (point.Z - minZ) * scale);
    }

    private static void DrawTrackPoints(
        DrawingContext drawingContext,
        IReadOnlyList<TrackPoint> points,
        Func<EstateGatePoint, Point> transform,
        Color glowColor,
        double glowWidth,
        Color lineColor,
        double lineWidth)
    {
        if (points.Count < 2) return;
        var projected = points.Select(point => transform(new EstateGatePoint(point.X, point.Y, point.Z))).ToArray();
        DrawPolyline(drawingContext, projected, glowColor, glowWidth);
        DrawPolyline(drawingContext, projected, lineColor, lineWidth);
    }

    private static void DrawGatePoints(
        DrawingContext drawingContext,
        IReadOnlyList<EstateGatePoint> points,
        Func<EstateGatePoint, Point> transform,
        Color color,
        double width)
    {
        if (points.Count < 2) return;
        DrawPolyline(drawingContext, points.Select(transform).ToArray(), color, width);
    }

    private static void DrawPolyline(DrawingContext context, IReadOnlyList<Point> points, Color color, double width)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], false, false);
            path.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }
        geometry.Freeze();
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        }, geometry);
    }

    private static void DrawPolygon(
        DrawingContext context,
        IReadOnlyList<EstateGatePoint> points,
        Func<EstateGatePoint, Point> transform)
    {
        if (points.Count < 3) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(transform(points[0]), true, true);
            path.PolyLineTo(points.Skip(1).Select(transform).ToArray(), true, true);
        }
        geometry.Freeze();
        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(44, 57, 217, 138)),
            new Pen(new SolidColorBrush(Color.FromRgb(57, 217, 138)), 1.8),
            geometry);
    }

    private static void DrawGate(
        DrawingContext context,
        EstateTimingGate gate,
        Func<EstateGatePoint, Point> transform,
        Color color,
        double width) =>
        context.DrawLine(new Pen(new SolidColorBrush(color), width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, transform(gate.Left), transform(gate.Right));

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(18, 122, 155, 174)), 1);
        const double spacing = 28;
        for (var x = spacing; x < bounds.Width; x += spacing)
            context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        for (var y = spacing; y < bounds.Height; y += spacing)
            context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
    }

    private static void DrawEmptyState(DrawingContext context, Rect bounds)
    {
        var text = new FormattedText(
            AppLocalization.Literal("开始录入后，这里会实时显示采样轨迹"),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            13,
            new SolidColorBrush(Color.FromRgb(139, 157, 171)),
            1.0);
        context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
    }
}
