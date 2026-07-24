using System.Windows;
using System.Windows.Media;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed class TrackMapPreview : FrameworkElement
{
    private const int MaximumPreviewPoints = 520;
    private readonly IReadOnlyList<TrackPoint> points;
    private readonly TrackLayoutKind layoutKind;
    private readonly Color accentColor;

    public TrackMapPreview(TrackTemplate track)
    {
        points = Downsample(track.Points);
        layoutKind = track.LayoutKind;
        accentColor = CategoryColor(track.Category);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (points.Count < 2 || ActualWidth <= 1 || ActualHeight <= 1) return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(13, 22, 31)),
            null,
            bounds);
        DrawGrid(drawingContext, bounds);

        const double padding = 18;
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minZ = points.Min(point => point.Z);
        var maxZ = points.Max(point => point.Z);
        var spanX = Math.Max(maxX - minX, 1);
        var spanZ = Math.Max(maxZ - minZ, 1);
        var scale = Math.Min(
            Math.Max(1, ActualWidth - (padding * 2)) / spanX,
            Math.Max(1, ActualHeight - (padding * 2)) / spanZ);
        var drawnWidth = spanX * scale;
        var drawnHeight = spanZ * scale;
        var offsetX = (ActualWidth - drawnWidth) / 2;
        var offsetY = (ActualHeight - drawnHeight) / 2;

        Point Transform(TrackPoint point) => new(
            offsetX + ((point.X - minX) * scale),
            offsetY + drawnHeight - ((point.Z - minZ) * scale));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Transform(points[0]), false, false);
            context.PolyLineTo(points.Skip(1).Select(Transform).ToArray(), true, true);
        }
        geometry.Freeze();

        var glow = new Pen(new SolidColorBrush(Color.FromArgb(68, accentColor.R, accentColor.G, accentColor.B)), 7)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var route = new Pen(new SolidColorBrush(accentColor), 2.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        glow.Freeze();
        route.Freeze();
        drawingContext.DrawGeometry(null, glow, geometry);
        drawingContext.DrawGeometry(null, route, geometry);

        var start = Transform(points[0]);
        var finish = Transform(points[^1]);
        drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(235, 242, 247)), null, start, 3.5, 3.5);
        if (layoutKind == TrackLayoutKind.PointToPoint)
        {
            DrawRoundCheckeredMarker(drawingContext, finish);
        }
        else
        {
            drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(accentColor), 1.8), start, 6.5, 6.5);
            if (StartDirection(Transform, start) is Vector direction)
                DrawDirectionArrow(drawingContext, start, direction);
        }
    }

    public static Color CategoryColor(string? category) => category switch
    {
        "公路" => Color.FromRgb(3, 151, 252),
        "街头" => Color.FromRgb(181, 45, 174),
        "泥地" => Color.FromRgb(255, 91, 1),
        "越野" => Color.FromRgb(25, 145, 73),
        "山道" => Color.FromRgb(18, 216, 211),
        "直线" => Color.FromRgb(229, 31, 121),
        _ => Color.FromRgb(104, 197, 220)
    };

    private static IReadOnlyList<TrackPoint> Downsample(IReadOnlyList<TrackPoint> source)
    {
        if (source.Count <= MaximumPreviewPoints) return source;
        var step = (double)(source.Count - 1) / (MaximumPreviewPoints - 1);
        return Enumerable.Range(0, MaximumPreviewPoints)
            .Select(index => source[Math.Min(source.Count - 1, (int)Math.Round(index * step))])
            .ToArray();
    }

    private Vector? StartDirection(Func<TrackPoint, Point> transform, Point start)
    {
        for (var index = 1; index < Math.Min(points.Count, 16); index++)
        {
            var direction = transform(points[index]) - start;
            if (direction.Length < 3) continue;
            direction.Normalize();
            return direction;
        }

        return null;
    }

    private static void DrawDirectionArrow(
        DrawingContext drawingContext,
        Point endpoint,
        Vector direction)
    {
        var start = endpoint + direction * 9;
        var tip = endpoint + direction * 20;
        var perpendicular = new Vector(-direction.Y, direction.X);
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(190, 8, 14, 20)), 4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(242, 247, 250)), 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawLine(shadow, start, tip);
        drawingContext.DrawLine(pen, start, tip);
        drawingContext.DrawLine(pen, tip, tip - direction * 4.5 + perpendicular * 3);
        drawingContext.DrawLine(pen, tip, tip - direction * 4.5 - perpendicular * 3);
    }

    private static void DrawRoundCheckeredMarker(DrawingContext drawingContext, Point point)
    {
        const double radius = 5.5;
        var marker = new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2);
        drawingContext.PushClip(new EllipseGeometry(point, radius - 0.75, radius - 0.75));
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(10, 16, 22)),
            null,
            point,
            radius,
            radius);
        var light = new SolidColorBrush(Color.FromRgb(242, 247, 250));
        drawingContext.DrawRectangle(light, null, new Rect(marker.Left, marker.Top, radius, radius));
        drawingContext.DrawRectangle(light, null, new Rect(point.X, point.Y, radius, radius));
        drawingContext.Pop();
        drawingContext.DrawEllipse(
            null,
            new Pen(new SolidColorBrush(Color.FromRgb(242, 184, 39)), 1.4),
            point,
            radius,
            radius);
    }

    private static void DrawGrid(DrawingContext drawingContext, Rect bounds)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 122, 155, 174)), 1);
        gridPen.Freeze();
        const double spacing = 24;
        for (var x = spacing; x < bounds.Width; x += spacing)
            drawingContext.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
        for (var y = spacing; y < bounds.Height; y += spacing)
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
    }
}
