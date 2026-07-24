using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.App;

internal static class LapSeriesPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(32, 184, 207),
        Color.FromRgb(242, 184, 39),
        Color.FromRgb(192, 92, 255),
        Color.FromRgb(24, 167, 101)
    ];
    private static readonly SolidColorBrush[] Brushes = Colors
        .Select(color =>
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        })
        .ToArray();

    public static Color At(int index) => Colors[Math.Abs(index) % Colors.Length];

    public static SolidColorBrush BrushAt(int index) => Brushes[Math.Abs(index) % Brushes.Length];
}

internal sealed record LapVisualHit(
    LapRecord Lap,
    LapSample Sample,
    int SeriesIndex,
    Point ScreenPoint);

internal sealed class LapTelemetryChart : FrameworkElement
{
    private readonly LapRecord[] laps;
    private readonly double maximumSpeed;
    private readonly double progressExtent;
    private readonly ToolTip hoverToolTip;
    private LapVisualHit? hover;

    public LapTelemetryChart(
        IReadOnlyList<LapRecord> laps,
        double? trackLengthMeters = null)
    {
        this.laps = laps.Where(lap => lap.Samples.Count >= 2).Take(4).ToArray();
        maximumSpeed = Math.Max(
            1,
            this.laps
                .SelectMany(lap => lap.Samples)
                .Select(sample => sample.SpeedMps)
                .Where(double.IsFinite)
                .DefaultIfEmpty(1)
                .Max());
        progressExtent = ChartInteractionAlgorithms.ResolveProgressExtent(
            this.laps.Select(lap => lap.Samples),
            trackLengthMeters);
        hoverToolTip = CreateToolTip(this);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MouseMove += (_, eventArgs) => UpdateHover(eventArgs.GetPosition(this));
        MouseLeave += (_, _) => ClearHover();
        Unloaded += (_, _) => hoverToolTip.IsOpen = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryMetrics(out var metrics)) return;
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(18, 23, 30)),
            new Pen(new SolidColorBrush(Color.FromRgb(48, 58, 72)), 1),
            metrics.Bounds);
        DrawChartGrid(drawingContext, metrics.Bounds);
        for (var index = 0; index < laps.Length; index++)
            DrawSeries(drawingContext, laps[index].Samples, LapSeriesPalette.At(index), metrics);

        if (hover is not null)
        {
            var point = ChartPoint(hover.Sample, metrics);
            var color = LapSeriesPalette.At(hover.SeriesIndex);
            drawingContext.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), 1)
                {
                    DashStyle = DashStyles.Dash
                },
                new Point(point.X, metrics.Bounds.Top),
                new Point(point.X, metrics.Bounds.Bottom));
            DrawHoverMarker(drawingContext, point, color);
        }
    }

    private void UpdateHover(Point pointer)
    {
        if (!TryMetrics(out var metrics) || !metrics.Bounds.Contains(pointer))
        {
            ClearHover();
            return;
        }

        var progress = Math.Clamp(
            (pointer.X - metrics.Bounds.Left) / metrics.Bounds.Width * metrics.MaxProgress,
            0,
            metrics.MaxProgress);
        LapVisualHit? best = null;
        var bestDistanceSquared = 12d * 12d;
        for (var seriesIndex = laps.Length - 1; seriesIndex >= 0; seriesIndex--)
        {
            var lap = laps[seriesIndex];
            var nearest = ChartInteractionAlgorithms.FindNearestProgressSample(lap.Samples, progress);
            for (var index = Math.Max(0, nearest - 2);
                 index <= Math.Min(lap.Samples.Count - 1, nearest + 2);
                 index++)
            {
                var sample = lap.Samples[index];
                var point = ChartPoint(sample, metrics);
                var distanceSquared = DistanceSquared(pointer, point);
                if (distanceSquared >= bestDistanceSquared) continue;
                bestDistanceSquared = distanceSquared;
                best = new LapVisualHit(lap, sample, seriesIndex, point);
            }
        }

        SetHover(best, pointer, includePosition: false);
    }

    private bool TryMetrics(out ChartMetrics metrics)
    {
        if (laps.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            metrics = default;
            return false;
        }

        var bounds = new Rect(42, 18, Math.Max(1, ActualWidth - 58), Math.Max(1, ActualHeight - 42));
        metrics = new ChartMetrics(
            bounds,
            maximumSpeed,
            progressExtent);
        return true;
    }

    private void SetHover(LapVisualHit? next, Point pointer, bool includePosition)
    {
        if (next is null)
        {
            ClearHover();
            return;
        }

        var changed = hover?.Lap.Id != next.Lap.Id ||
                      !ReferenceEquals(hover?.Sample, next.Sample);
        hover = next;
        if (changed)
        {
            hoverToolTip.BorderBrush = LapSeriesPalette.BrushAt(next.SeriesIndex);
            hoverToolTip.Content = TooltipText(next.Lap, next.Sample, includePosition);
        }
        hoverToolTip.HorizontalOffset = Math.Min(pointer.X + 14, Math.Max(8, ActualWidth - 245));
        hoverToolTip.VerticalOffset = Math.Min(pointer.Y + 14, Math.Max(8, ActualHeight - 145));
        hoverToolTip.IsOpen = true;
        Cursor = Cursors.Cross;
        if (changed) InvalidateVisual();
    }

    private void ClearHover()
    {
        if (hover is null && !hoverToolTip.IsOpen) return;
        hover = null;
        hoverToolTip.IsOpen = false;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    private static void DrawSeries(
        DrawingContext drawingContext,
        IReadOnlyList<LapSample> samples,
        Color color,
        ChartMetrics metrics)
    {
        var pen = new Pen(new SolidColorBrush(color), 2);
        Point? previous = null;
        foreach (var sample in Downsample(samples, (int)Math.Max(64, metrics.Bounds.Width)))
        {
            var point = ChartPoint(sample, metrics);
            if (previous is Point old) drawingContext.DrawLine(pen, old, point);
            previous = point;
        }
    }

    private static Point ChartPoint(LapSample sample, ChartMetrics metrics) => new(
        metrics.Bounds.Left + metrics.Bounds.Width * sample.S / metrics.MaxProgress,
        metrics.Bounds.Bottom -
        metrics.Bounds.Height * Math.Clamp(sample.SpeedMps / metrics.MaxSpeed, 0, 1));

    private static IEnumerable<LapSample> Downsample(IReadOnlyList<LapSample> source, int maximum)
    {
        if (source.Count <= maximum) return source;
        var step = source.Count / (double)maximum;
        return Enumerable.Range(0, maximum)
            .Select(index => source[Math.Min(source.Count - 1, (int)(index * step))]);
    }

    private static void DrawChartGrid(DrawingContext drawingContext, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(22, 122, 155, 174)), 1);
        for (var index = 1; index < 5; index++)
        {
            var x = bounds.Left + bounds.Width * index / 5;
            drawingContext.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
        for (var index = 1; index < 4; index++)
        {
            var y = bounds.Top + bounds.Height * index / 4;
            drawingContext.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
    }

    private readonly record struct ChartMetrics(Rect Bounds, double MaxSpeed, double MaxProgress);

    internal static ToolTip CreateToolTip(FrameworkElement target) => new()
    {
        Placement = PlacementMode.Relative,
        PlacementTarget = target,
        Background = new SolidColorBrush(Color.FromRgb(14, 21, 29)),
        Foreground = new SolidColorBrush(Color.FromRgb(244, 247, 250)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(32, 184, 207)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(11, 8, 11, 8),
        HasDropShadow = true,
        StaysOpen = true,
        IsOpen = false
    };

    internal static TextBlock TooltipText(
        LapRecord lap,
        LapSample sample,
        bool includePosition)
    {
        var performanceIndex = lap.Vehicle.PerformanceIndex >= 0
            ? lap.Vehicle.PerformanceIndex.ToString()
            : "—";
        var validity = lap.IsValid ? "有效" : $"无效 · {lap.InvalidReason ?? "原因未知"}";
        var position = includePosition
            ? $"\n位置 X {sample.X:0.0} · Z {sample.Z:0.0}"
            : string.Empty;
        return new TextBlock
        {
            Text =
                $"圈速 {FormatTime(lap.TotalSeconds)}  ·  {PerformanceClassCatalog.Name(lap.Vehicle.CarClass)} {performanceIndex}\n" +
                $"{lap.StartedAt.ToLocalTime():MM-dd HH:mm:ss}  ·  {validity}\n" +
                $"当前 {FormatTime(sample.ElapsedSeconds)}  ·  距离 {sample.S / 1000:0.000} km\n" +
                $"速度 {sample.SpeedMps * 3.6:0.0} km/h  ·  {GearText(sample.Gear)} 挡  ·  {sample.Rpm:0} RPM\n" +
                $"油门 {sample.Accel:P0}  ·  制动 {sample.Brake:P0}{position}",
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 247, 250)),
            LineHeight = 18
        };
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private static string GearText(byte gear) => gear == 0 ? "R" : gear.ToString();
    private static double DistanceSquared(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    internal static void DrawHoverMarker(DrawingContext drawingContext, Point point, Color color)
    {
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(14, 21, 29)),
            new Pen(Brushes.White, 1.5),
            point,
            5.5,
            5.5);
        drawingContext.DrawEllipse(new SolidColorBrush(color), null, point, 3.2, 3.2);
    }
}

internal sealed class TrackMapView : FrameworkElement
{
    private const double MinimumZoom = 1;
    private const double MaximumZoom = 24;
    private const double HoverRadius = 11;
    private const double HoverCellSize = 24;
    private readonly LapRecord[] laps;
    private readonly TrackPoint[] trackPoints;
    private readonly TrackLayoutKind layoutKind;
    private readonly TrackEndpointSummary? endpoints;
    private readonly ToolTip hoverToolTip;
    private readonly double mapMinimumX;
    private readonly double mapMinimumZ;
    private readonly double mapSpanX;
    private readonly double mapSpanZ;
    private ChartViewport viewport = new(MinimumZoom, 0, 0);
    private LapVisualHit? hover;
    private ScreenHitGrid? hoverGrid;
    private MapViewportKey? hoverGridKey;
    private DrawingGroup? baseDrawing;
    private MapDrawingKey? baseDrawingKey;
    private bool dragging;
    private Point dragStart;

    public TrackMapView(IReadOnlyList<LapRecord> laps, TrackTemplate? track)
    {
        this.laps = laps.Where(lap => lap.Samples.Count >= 2).Take(4).ToArray();
        trackPoints = track?.Points.ToArray() ?? [];
        layoutKind = track?.LayoutKind ?? TrackLayoutKind.Circuit;
        endpoints = ChartInteractionAlgorithms.SummarizeTrackEndpoints(trackPoints) ??
                    ChartInteractionAlgorithms.SummarizeTrackEndpoints(
                        this.laps.Select(lap => lap.Samples));
        (mapMinimumX, mapMinimumZ, mapSpanX, mapSpanZ) =
            ResolveMapExtents(this.laps, trackPoints);
        hoverToolTip = LapTelemetryChart.CreateToolTip(this);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
        MouseWheel += OnMouseWheel;
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseLeave += (_, _) =>
        {
            if (!dragging) ClearHover();
        };
        LostMouseCapture += (_, _) => dragging = false;
        Unloaded += (_, _) => hoverToolTip.IsOpen = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryMetrics(out var metrics)) return;
        viewport = ClampViewport(viewport, metrics);
        EnsureBaseDrawing(metrics);
        if (baseDrawing is not null) drawingContext.DrawDrawing(baseDrawing);

        if (hover is not null)
        {
            var point = MapPoint(hover.Sample, metrics, viewport);
            drawingContext.PushClip(new RectangleGeometry(metrics.Bounds));
            LapTelemetryChart.DrawHoverMarker(
                drawingContext,
                point,
                LapSeriesPalette.At(hover.SeriesIndex));
            drawingContext.Pop();
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (!TryMetrics(out var metrics)) return;
        var pointer = eventArgs.GetPosition(this);
        viewport = ChartInteractionAlgorithms.ZoomAroundCursor(
            viewport,
            pointer.X,
            pointer.Y,
            metrics.Bounds.Left + metrics.Bounds.Width / 2,
            metrics.Bounds.Top + metrics.Bounds.Height / 2,
            eventArgs.Delta,
            MinimumZoom,
            MaximumZoom);
        viewport = ClampViewport(viewport, metrics);
        UpdateHover(pointer);
        InvalidateVisual();
        eventArgs.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs eventArgs)
    {
        var pointer = eventArgs.GetPosition(this);
        if (dragging && eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            if (!TryMetrics(out var metrics)) return;
            viewport = ClampViewport(
                viewport with
                {
                    OffsetX = viewport.OffsetX + pointer.X - dragStart.X,
                    OffsetY = viewport.OffsetY + pointer.Y - dragStart.Y
                },
                metrics);
            dragStart = pointer;
            hoverToolTip.IsOpen = false;
            hover = null;
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        UpdateHover(pointer);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        Focus();
        if (eventArgs.ClickCount >= 2)
        {
            viewport = new ChartViewport(MinimumZoom, 0, 0);
            ClearHover();
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        dragging = true;
        dragStart = eventArgs.GetPosition(this);
        CaptureMouse();
        hoverToolTip.IsOpen = false;
        Cursor = Cursors.SizeAll;
        eventArgs.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        UpdateHover(eventArgs.GetPosition(this));
        eventArgs.Handled = true;
    }

    private void UpdateHover(Point pointer)
    {
        if (dragging || !TryMetrics(out var metrics) || !metrics.Bounds.Contains(pointer))
        {
            ClearHover();
            return;
        }

        EnsureHoverGrid(metrics);
        if (hoverGrid is null ||
            !hoverGrid.TryFindNearest(
                pointer.X,
                pointer.Y,
                HoverRadius,
                out var nearest))
        {
            ClearHover();
            return;
        }

        var lap = laps[nearest.SeriesIndex];
        var sample = lap.Samples[nearest.SampleIndex];
        var best = new LapVisualHit(
            lap,
            sample,
            nearest.SeriesIndex,
            new Point(nearest.X, nearest.Y));
        var changed = hover?.Lap.Id != best.Lap.Id ||
                      !ReferenceEquals(hover?.Sample, best.Sample);
        hover = best;
        if (changed)
        {
            hoverToolTip.BorderBrush = LapSeriesPalette.BrushAt(best.SeriesIndex);
            hoverToolTip.Content = LapTelemetryChart.TooltipText(
                best.Lap,
                best.Sample,
                includePosition: true);
        }
        hoverToolTip.HorizontalOffset = Math.Min(pointer.X + 14, Math.Max(8, ActualWidth - 245));
        hoverToolTip.VerticalOffset = Math.Min(pointer.Y + 14, Math.Max(8, ActualHeight - 165));
        hoverToolTip.IsOpen = true;
        Cursor = Cursors.None;
        if (changed) InvalidateVisual();
    }

    private void ClearHover()
    {
        if (hover is null && !hoverToolTip.IsOpen) return;
        hover = null;
        hoverToolTip.IsOpen = false;
        if (!dragging) Cursor = viewport.Zoom > MinimumZoom ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    private bool TryMetrics(out MapMetrics metrics)
    {
        if (laps.Length == 0 || ActualWidth <= 1 || ActualHeight <= 1)
        {
            metrics = default;
            return false;
        }

        var bounds = new Rect(0.5, 0.5, Math.Max(1, ActualWidth - 1), Math.Max(1, ActualHeight - 1));
        const double padding = 18;
        var baseScale = Math.Min(
            Math.Max(1, bounds.Width - padding * 2) / mapSpanX,
            Math.Max(1, bounds.Height - padding * 2) / mapSpanZ);
        var drawnWidth = mapSpanX * baseScale;
        var drawnHeight = mapSpanZ * baseScale;
        metrics = new MapMetrics(
            bounds,
            mapMinimumX,
            mapMinimumZ,
            baseScale,
            (bounds.Width - drawnWidth) / 2,
            (bounds.Height - drawnHeight) / 2,
            drawnWidth,
            drawnHeight);
        return true;
    }

    private void EnsureBaseDrawing(MapMetrics metrics)
    {
        var viewportKey = CreateViewportKey(metrics);
        var drawingKey = new MapDrawingKey(
            viewportKey,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (baseDrawing is not null &&
            baseDrawingKey is { } cachedKey &&
            cachedKey.Equals(drawingKey))
            return;

        var drawing = new DrawingGroup();
        using (var drawingContext = drawing.Open())
        {
            drawingContext.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(18, 23, 30)),
                new Pen(new SolidColorBrush(Color.FromRgb(48, 58, 72)), 1),
                metrics.Bounds);
            drawingContext.PushClip(new RectangleGeometry(metrics.Bounds));
            DrawMapGrid(drawingContext, metrics.Bounds);
            for (var index = 0; index < laps.Length; index++)
                DrawRoute(drawingContext, laps[index].Samples, index, metrics);
            DrawTrackEndpoints(drawingContext, metrics);
            drawingContext.Pop();
            DrawZoomBadge(drawingContext);
        }

        if (drawing.CanFreeze) drawing.Freeze();
        baseDrawing = drawing;
        baseDrawingKey = drawingKey;
    }

    private void DrawRoute(
        DrawingContext drawingContext,
        IReadOnlyList<LapSample> samples,
        int seriesIndex,
        MapMetrics metrics)
    {
        if (samples.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(
                MapPoint(samples[0], metrics, viewport),
                isFilled: false,
                isClosed: false);
            for (var index = 1; index < samples.Count; index++)
            {
                geometryContext.LineTo(
                    MapPoint(samples[index], metrics, viewport),
                    isStroked: true,
                    isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        var pen = new Pen(LapSeriesPalette.BrushAt(seriesIndex), 2.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private void EnsureHoverGrid(MapMetrics metrics)
    {
        var key = CreateViewportKey(metrics);
        if (hoverGrid is not null &&
            hoverGridKey is { } cachedKey &&
            cachedKey.Equals(key))
            return;

        var capacity = laps.Sum(lap => lap.Samples.Count);
        var points = new List<ScreenHitPoint>(capacity);
        var hitBounds = metrics.Bounds;
        hitBounds.Inflate(HoverRadius, HoverRadius);
        for (var seriesIndex = laps.Length - 1; seriesIndex >= 0; seriesIndex--)
        {
            var samples = laps[seriesIndex].Samples;
            for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
            {
                var point = MapPoint(samples[sampleIndex], metrics, viewport);
                if (!hitBounds.Contains(point)) continue;
                points.Add(new ScreenHitPoint(
                    seriesIndex,
                    sampleIndex,
                    point.X,
                    point.Y));
            }
        }

        hoverGrid = new ScreenHitGrid(points, HoverCellSize);
        hoverGridKey = key;
    }

    private MapViewportKey CreateViewportKey(MapMetrics metrics) =>
        new(metrics.Bounds, viewport);

    private static (double MinimumX, double MinimumZ, double SpanX, double SpanZ)
        ResolveMapExtents(
            IReadOnlyList<LapRecord> laps,
            IReadOnlyList<TrackPoint> trackPoints)
    {
        var minimumX = double.PositiveInfinity;
        var maximumX = double.NegativeInfinity;
        var minimumZ = double.PositiveInfinity;
        var maximumZ = double.NegativeInfinity;

        void Include(double x, double z)
        {
            if (!double.IsFinite(x) || !double.IsFinite(z)) return;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            minimumZ = Math.Min(minimumZ, z);
            maximumZ = Math.Max(maximumZ, z);
        }

        foreach (var lap in laps)
        foreach (var sample in lap.Samples)
            Include(sample.X, sample.Z);
        foreach (var point in trackPoints)
            Include(point.X, point.Z);

        if (!double.IsFinite(minimumX) ||
            !double.IsFinite(maximumX) ||
            !double.IsFinite(minimumZ) ||
            !double.IsFinite(maximumZ))
            return (0, 0, 1, 1);

        return (
            minimumX,
            minimumZ,
            Math.Max(1, maximumX - minimumX),
            Math.Max(1, maximumZ - minimumZ));
    }

    private static Point MapPoint(
        LapSample sample,
        MapMetrics metrics,
        ChartViewport viewport) =>
        MapPoint(sample.X, sample.Z, metrics, viewport);

    private static Point MapPoint(
        double x,
        double z,
        MapMetrics metrics,
        ChartViewport viewport)
    {
        var basePoint = new Point(
            metrics.Bounds.Left + metrics.BaseOffsetX + (x - metrics.MinX) * metrics.BaseScale,
            metrics.Bounds.Top + metrics.BaseOffsetY + metrics.DrawnHeight -
            (z - metrics.MinZ) * metrics.BaseScale);
        var center = new Point(
            metrics.Bounds.Left + metrics.Bounds.Width / 2,
            metrics.Bounds.Top + metrics.Bounds.Height / 2);
        return new Point(
            center.X + (basePoint.X - center.X) * viewport.Zoom + viewport.OffsetX,
            center.Y + (basePoint.Y - center.Y) * viewport.Zoom + viewport.OffsetY);
    }

    private void DrawTrackEndpoints(DrawingContext drawingContext, MapMetrics metrics)
    {
        if (endpoints is not { } value) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var start = MapPoint(value.StartX, value.StartZ, metrics, viewport);
        var finish = MapPoint(value.FinishX, value.FinishZ, metrics, viewport);
        if (layoutKind == TrackLayoutKind.Circuit)
        {
            var direction = StartDirection(metrics);
            DrawCombinedEndpoint(
                drawingContext,
                new Point((start.X + finish.X) / 2, (start.Y + finish.Y) / 2),
                metrics.Bounds,
                pixelsPerDip);
            if (direction is Vector directionVector)
                DrawDirectionArrow(
                    drawingContext,
                    new Point((start.X + finish.X) / 2, (start.Y + finish.Y) / 2),
                    directionVector);
            return;
        }

        DrawStartEndpoint(drawingContext, start, metrics.Bounds, pixelsPerDip);
        DrawFinishEndpoint(drawingContext, finish, metrics.Bounds, pixelsPerDip);
    }

    private static void DrawStartEndpoint(
        DrawingContext drawingContext,
        Point point,
        Rect bounds,
        double pixelsPerDip)
    {
        var accent = Color.FromRgb(62, 224, 151);
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(14, 21, 29)),
            new Pen(new SolidColorBrush(Color.FromRgb(242, 247, 250)), 2),
            point,
            8,
            8);
        drawingContext.DrawEllipse(new SolidColorBrush(accent), null, point, 4.5, 4.5);
        DrawEndpointLabel(drawingContext, point, bounds, "起点", accent, pixelsPerDip);
    }

    private static void DrawFinishEndpoint(
        DrawingContext drawingContext,
        Point point,
        Rect bounds,
        double pixelsPerDip)
    {
        DrawRoundCheckeredMarker(drawingContext, point);
        DrawEndpointLabel(
            drawingContext,
            point,
            bounds,
            "终点",
            Color.FromRgb(242, 184, 39),
            pixelsPerDip);
    }

    private static void DrawCombinedEndpoint(
        DrawingContext drawingContext,
        Point point,
        Rect bounds,
        double pixelsPerDip)
    {
        DrawSquareCheckeredMarker(drawingContext, point, Color.FromRgb(62, 224, 151));
        DrawEndpointLabel(
            drawingContext,
            point,
            bounds,
            "起/终",
            Color.FromRgb(62, 224, 151),
            pixelsPerDip);
    }

    private static void DrawSquareCheckeredMarker(
        DrawingContext drawingContext,
        Point point,
        Color? outerAccent)
    {
        if (outerAccent is Color accent)
        {
            drawingContext.DrawEllipse(
                null,
                new Pen(new SolidColorBrush(accent), 2),
                point,
                10,
                10);
        }

        var marker = new Rect(point.X - 7, point.Y - 7, 14, 14);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(14, 21, 29)),
            new Pen(new SolidColorBrush(Color.FromRgb(242, 247, 250)), 1.5),
            marker,
            2,
            2);
        var light = new SolidColorBrush(Color.FromRgb(242, 247, 250));
        drawingContext.DrawRectangle(light, null, new Rect(marker.Left + 2, marker.Top + 2, 5, 5));
        drawingContext.DrawRectangle(light, null, new Rect(marker.Left + 7, marker.Top + 7, 5, 5));
    }

    private static void DrawRoundCheckeredMarker(DrawingContext drawingContext, Point point)
    {
        const double radius = 8;
        var marker = new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2);
        var clip = new EllipseGeometry(point, radius - 1, radius - 1);
        drawingContext.PushClip(clip);
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(14, 21, 29)),
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
            new Pen(new SolidColorBrush(Color.FromRgb(242, 184, 39)), 2),
            point,
            radius,
            radius);
    }

    private Vector? StartDirection(MapMetrics metrics)
    {
        if (trackPoints.Length < 2) return null;
        var start = MapPoint(trackPoints[0].X, trackPoints[0].Z, metrics, viewport);
        for (var index = 1; index < Math.Min(trackPoints.Length, 16); index++)
        {
            var next = MapPoint(trackPoints[index].X, trackPoints[index].Z, metrics, viewport);
            var direction = next - start;
            if (direction.Length < 4) continue;
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
        var start = endpoint + direction * 13;
        var tip = endpoint + direction * 29;
        var perpendicular = new Vector(-direction.Y, direction.X);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(242, 247, 250)), 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(180, 14, 21, 29)), 5),
            start,
            tip);
        drawingContext.DrawLine(pen, start, tip);
        drawingContext.DrawLine(pen, tip, tip - direction * 6 + perpendicular * 4);
        drawingContext.DrawLine(pen, tip, tip - direction * 6 - perpendicular * 4);
    }

    private static void DrawEndpointLabel(
        DrawingContext drawingContext,
        Point point,
        Rect bounds,
        string label,
        Color accent,
        double pixelsPerDip)
    {
        var text = new FormattedText(
            label,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal),
            10,
            new SolidColorBrush(Color.FromRgb(244, 247, 250)),
            pixelsPerDip);
        var width = text.Width + 14;
        var height = text.Height + 8;
        var left = point.X + 12;
        if (left + width > bounds.Right - 4) left = point.X - width - 12;
        left = Math.Clamp(left, bounds.Left + 4, Math.Max(bounds.Left + 4, bounds.Right - width - 4));
        var top = Math.Clamp(
            point.Y - height / 2,
            bounds.Top + 4,
            Math.Max(bounds.Top + 4, bounds.Bottom - height - 4));
        var badge = new Rect(left, top, width, height);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(232, 14, 21, 29)),
            new Pen(new SolidColorBrush(accent), 1),
            badge,
            6,
            6);
        drawingContext.DrawText(text, new Point(badge.Left + 7, badge.Top + 4));
    }

    private static ChartViewport ClampViewport(ChartViewport current, MapMetrics metrics)
    {
        if (current.Zoom <= MinimumZoom + 0.000_001)
            return new ChartViewport(MinimumZoom, 0, 0);
        var visibleWidth = Math.Max(1, metrics.Bounds.Width - 36);
        var visibleHeight = Math.Max(1, metrics.Bounds.Height - 36);
        var maxOffsetX = Math.Max(0, (metrics.DrawnWidth * current.Zoom - visibleWidth) / 2);
        var maxOffsetY = Math.Max(0, (metrics.DrawnHeight * current.Zoom - visibleHeight) / 2);
        return current with
        {
            Zoom = Math.Clamp(current.Zoom, MinimumZoom, MaximumZoom),
            OffsetX = Math.Clamp(current.OffsetX, -maxOffsetX, maxOffsetX),
            OffsetY = Math.Clamp(current.OffsetY, -maxOffsetY, maxOffsetY)
        };
    }

    private void DrawZoomBadge(DrawingContext drawingContext)
    {
        var text = new FormattedText(
            $"{viewport.Zoom:0.00}×",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10,
            new SolidColorBrush(Color.FromRgb(230, 237, 242)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var badge = new Rect(8, 8, text.Width + 16, text.Height + 8);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(210, 14, 21, 29)),
            new Pen(new SolidColorBrush(Color.FromRgb(62, 79, 94)), 1),
            badge,
            8,
            8);
        drawingContext.DrawText(text, new Point(badge.Left + 8, badge.Top + 4));
    }

    private static void DrawMapGrid(DrawingContext drawingContext, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(20, 122, 155, 174)), 1);
        const double spacing = 28;
        for (var x = bounds.Left + spacing; x < bounds.Right; x += spacing)
            drawingContext.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        for (var y = bounds.Top + spacing; y < bounds.Bottom; y += spacing)
            drawingContext.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
    }

    private readonly record struct MapMetrics(
        Rect Bounds,
        double MinX,
        double MinZ,
        double BaseScale,
        double BaseOffsetX,
        double BaseOffsetY,
        double DrawnWidth,
        double DrawnHeight);

    private readonly record struct MapViewportKey(
        Rect Bounds,
        ChartViewport Viewport);

    private readonly record struct MapDrawingKey(
        MapViewportKey ViewportKey,
        double PixelsPerDip);
}
