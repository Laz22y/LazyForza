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

internal sealed record LapSeriesLegendEntry(
    string PrimaryText,
    string SecondaryText);

internal sealed record CornerMapAnnotation(
    Guid LapId,
    CornerWindow Window,
    string Context,
    string Title,
    string Details,
    string Hint,
    string Footer,
    Color Accent);

internal enum AnalysisLegendCorner
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft
}

internal static class LapAnalysisVisualLayout
{
    public static double AdaptiveMapHeight(double windowHeight)
    {
        var safeWindowHeight = double.IsFinite(windowHeight) && windowHeight > 0
            ? windowHeight
            : 800;
        var preferred = Math.Clamp(safeWindowHeight * 0.72, 420, 900);
        var available = Math.Max(320, safeWindowHeight - 48);
        return Math.Min(preferred, available);
    }
}

internal static class AnalysisOverlayDrawing
{
    private static readonly FontFamily Font = new("Microsoft YaHei UI");

    public static Rect SelectSeriesLegendBounds(
        Rect renderBounds,
        int entryCount,
        IReadOnlyList<Point> seriesPoints,
        IReadOnlyList<Rect>? reservedBounds,
        params AnalysisLegendCorner[] preferredCorners)
    {
        if (entryCount <= 0 || renderBounds.Width < 120 || renderBounds.Height < 80)
            return Rect.Empty;

        var corners = preferredCorners.Length == 0
            ?
            [
                AnalysisLegendCorner.BottomLeft,
                AnalysisLegendCorner.BottomRight,
                AnalysisLegendCorner.TopRight,
                AnalysisLegendCorner.TopLeft
            ]
            : preferredCorners;
        var best = Rect.Empty;
        var bestScore = double.PositiveInfinity;
        for (var index = 0; index < corners.Length; index++)
        {
            var candidate = SeriesLegendBounds(renderBounds, entryCount, corners[index]);
            var probe = candidate;
            probe.Inflate(7, 7);
            var score = index * 0.025;
            if (reservedBounds is not null)
            {
                foreach (var reserved in reservedBounds)
                {
                    if (probe.IntersectsWith(reserved))
                        score += 10_000;
                }
            }

            foreach (var point in seriesPoints)
            {
                if (probe.Contains(point)) score += 1;
            }

            if (score >= bestScore) continue;
            bestScore = score;
            best = candidate;
        }

        return best;
    }

    public static void DrawSeriesLegend(
        DrawingContext drawingContext,
        Rect chrome,
        IReadOnlyList<LapSeriesLegendEntry> entries,
        double pixelsPerDip)
    {
        if (entries.Count == 0 || chrome.IsEmpty) return;

        const double entryHeight = 30;
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(194, 14, 21, 29)),
            new Pen(new SolidColorBrush(Color.FromArgb(190, 67, 84, 101)), 1),
            chrome,
            8,
            8);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var color = LapSeriesPalette.At(index);
            var centerY = chrome.Top + 4 + index * entryHeight + entryHeight / 2;
            var linePen = new Pen(new SolidColorBrush(color), 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawingContext.DrawLine(
                linePen,
                new Point(chrome.Left + 9, centerY),
                new Point(chrome.Left + 27, centerY));

            var textWidth = Math.Max(40, chrome.Width - 46);
            var primary = OneLineText(
                entry.PrimaryText,
                9.5,
                FontWeights.SemiBold,
                Color.FromRgb(244, 247, 250),
                textWidth,
                pixelsPerDip);
            var secondary = OneLineText(
                entry.SecondaryText,
                8,
                FontWeights.Normal,
                Color.FromRgb(160, 174, 188),
                textWidth,
                pixelsPerDip);
            drawingContext.DrawText(
                primary,
                new Point(chrome.Left + 35, centerY - 13));
            drawingContext.DrawText(
                secondary,
                new Point(chrome.Left + 35, centerY + 0.5));
        }
    }

    private static Rect SeriesLegendBounds(
        Rect renderBounds,
        int entryCount,
        AnalysisLegendCorner corner)
    {
        var width = Math.Min(
            entryCount == 1 ? 258 : 286,
            Math.Max(176, renderBounds.Width * 0.34));
        var height = 8 + Math.Min(4, entryCount) * 30;
        const double margin = 9;
        var left = corner is AnalysisLegendCorner.TopRight or AnalysisLegendCorner.BottomRight
            ? renderBounds.Right - width - margin
            : renderBounds.Left + margin;
        var top = corner is AnalysisLegendCorner.BottomLeft or AnalysisLegendCorner.BottomRight
            ? renderBounds.Bottom - height - margin
            : renderBounds.Top + margin;
        return new Rect(left, top, width, height);
    }

    private static FormattedText OneLineText(
        string text,
        double fontSize,
        FontWeight fontWeight,
        Color color,
        double maximumWidth,
        double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            new SolidColorBrush(color),
            pixelsPerDip)
        {
            MaxTextWidth = maximumWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        return formatted;
    }
}

internal sealed record LapVisualHit(
    LapRecord Lap,
    LapSample Sample,
    int SeriesIndex,
    Point ScreenPoint);

internal readonly record struct LapAnalysisCursorPosition(Guid LapId, double ProgressMeters);

internal sealed class LapAnalysisCursor
{
    public event Action<object, LapAnalysisCursorPosition?>? Changed;
    public event Action<object, LapAnalysisCursorPosition>? CommitRequested;

    public LapAnalysisCursorPosition? Position { get; private set; }

    public void Set(object source, Guid lapId, double progressMeters)
    {
        var next = new LapAnalysisCursorPosition(lapId, Math.Max(0, progressMeters));
        if (Position == next) return;
        Position = next;
        Changed?.Invoke(source, next);
    }

    public void Clear(object source)
    {
        if (Position is null) return;
        Position = null;
        Changed?.Invoke(source, null);
    }

    public void Commit(object source)
    {
        if (Position is { } position)
            CommitRequested?.Invoke(source, position);
    }
}

internal sealed class LapTelemetryChart : FrameworkElement
{
    private readonly LapRecord[] laps;
    private readonly LapSeriesLegendEntry[] legendEntries;
    private readonly double maximumSpeed;
    private readonly double progressExtent;
    private readonly ToolTip hoverToolTip;
    private readonly LapAnalysisCursor? linkedCursor;
    private LapVisualHit? hover;
    private DrawingGroup? baseDrawing;
    private ChartDrawingKey? baseDrawingKey;

    public LapTelemetryChart(
        IReadOnlyList<LapRecord> laps,
        double? trackLengthMeters = null,
        IReadOnlyList<LapSeriesLegendEntry>? legendEntries = null,
        LapAnalysisCursor? linkedCursor = null)
    {
        this.laps = laps.Where(lap => lap.Samples.Count >= 2).Take(4).ToArray();
        this.legendEntries = (legendEntries ?? [])
            .Take(this.laps.Length)
            .ToArray();
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
        this.linkedCursor = linkedCursor;
        if (linkedCursor is not null) linkedCursor.Changed += OnLinkedCursorChanged;
        hoverToolTip = CreateToolTip(this);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MouseMove += (_, eventArgs) => UpdateHover(eventArgs.GetPosition(this));
        MouseLeave += (_, _) => ClearHover();
        PreviewMouseLeftButtonDown += (_, _) => linkedCursor?.Commit(this);
        Unloaded += (_, _) =>
        {
            hoverToolTip.IsOpen = false;
            if (this.linkedCursor is not null)
                this.linkedCursor.Changed -= OnLinkedCursorChanged;
        };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryMetrics(out var metrics)) return;
        EnsureBaseDrawing(metrics);
        if (baseDrawing is not null) drawingContext.DrawDrawing(baseDrawing);

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

    private void EnsureBaseDrawing(ChartMetrics metrics)
    {
        var key = new ChartDrawingKey(metrics.Bounds, metrics.MaxSpeed, metrics.MaxProgress);
        if (baseDrawing is not null &&
            baseDrawingKey is { } cachedKey &&
            cachedKey.Equals(key))
            return;

        var drawing = new DrawingGroup();
        using (var drawingContext = drawing.Open())
        {
            drawingContext.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(18, 23, 30)),
                new Pen(new SolidColorBrush(Color.FromRgb(48, 58, 72)), 1),
                metrics.Bounds);
            DrawChartGrid(drawingContext, metrics.Bounds);
            for (var index = 0; index < laps.Length; index++)
                DrawSeries(drawingContext, laps[index].Samples, index, metrics);
            var legendBounds = AnalysisOverlayDrawing.SelectSeriesLegendBounds(
                metrics.Bounds,
                legendEntries.Length,
                ChartLegendPoints(metrics),
                reservedBounds: null,
                AnalysisLegendCorner.BottomLeft,
                AnalysisLegendCorner.BottomRight,
                AnalysisLegendCorner.TopRight,
                AnalysisLegendCorner.TopLeft);
            AnalysisOverlayDrawing.DrawSeriesLegend(
                drawingContext,
                legendBounds,
                legendEntries,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        if (drawing.CanFreeze) drawing.Freeze();
        baseDrawing = drawing;
        baseDrawingKey = key;
    }

    private IReadOnlyList<Point> ChartLegendPoints(ChartMetrics metrics)
    {
        var points = new List<Point>(laps.Length * 256);
        foreach (var lap in laps)
        {
            var samples = ChartInteractionAlgorithms.DownsampleSpeedEnvelope(
                lap.Samples,
                256);
            foreach (var sample in samples)
                points.Add(ChartPoint(sample, metrics));
        }

        return points;
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
        linkedCursor?.Set(this, next.Lap.Id, next.Sample.S);
        if (changed) InvalidateVisual();
    }

    private void ClearHover(bool publish = true)
    {
        if (hover is null && !hoverToolTip.IsOpen) return;
        hover = null;
        hoverToolTip.IsOpen = false;
        Cursor = Cursors.Arrow;
        if (publish) linkedCursor?.Clear(this);
        InvalidateVisual();
    }

    private void OnLinkedCursorChanged(object source, LapAnalysisCursorPosition? position)
    {
        if (ReferenceEquals(source, this)) return;
        if (position is null)
        {
            ClearHover(publish: false);
            return;
        }

        var seriesIndex = Array.FindIndex(laps, lap => lap.Id == position.Value.LapId);
        if (seriesIndex < 0) seriesIndex = 0;
        if (seriesIndex < 0 || seriesIndex >= laps.Length) return;
        var lap = laps[seriesIndex];
        var sampleIndex = ChartInteractionAlgorithms.FindNearestProgressSample(
            lap.Samples,
            position.Value.ProgressMeters);
        hover = new LapVisualHit(
            lap,
            lap.Samples[sampleIndex],
            seriesIndex,
            default);
        hoverToolTip.IsOpen = false;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    private static void DrawSeries(
        DrawingContext drawingContext,
        IReadOnlyList<LapSample> samples,
        int seriesIndex,
        ChartMetrics metrics)
    {
        var visibleSamples = ChartInteractionAlgorithms.DownsampleSpeedEnvelope(
            samples,
            (int)Math.Max(64, metrics.Bounds.Width * 2));
        if (visibleSamples.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(
                ChartPoint(visibleSamples[0], metrics),
                isFilled: false,
                isClosed: false);
            for (var index = 1; index < visibleSamples.Count; index++)
                geometryContext.LineTo(
                    ChartPoint(visibleSamples[index], metrics),
                    isStroked: true,
                    isSmoothJoin: true);
        }

        geometry.Freeze();
        var pen = new Pen(LapSeriesPalette.BrushAt(seriesIndex), 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Point ChartPoint(LapSample sample, ChartMetrics metrics) => new(
        metrics.Bounds.Left + metrics.Bounds.Width * sample.S / metrics.MaxProgress,
        metrics.Bounds.Bottom -
        metrics.Bounds.Height * Math.Clamp(sample.SpeedMps / metrics.MaxSpeed, 0, 1));

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
    private readonly record struct ChartDrawingKey(Rect Bounds, double MaxSpeed, double MaxProgress);

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
        var validity = lap.IsValid
            ? AppLocalization.Literal("有效")
            : AppLocalization.Format(
                "analysis.tooltip.invalid",
                "无效 · {0}",
                AppLocalization.Literal(lap.InvalidReason ?? "原因未知"));
        var position = includePosition
            ? AppLocalization.Format(
                "analysis.tooltip.position",
                "\n位置 X {0:0.0} · Z {1:0.0}",
                sample.X,
                sample.Z)
            : string.Empty;
        return new TextBlock
        {
            Text = AppLocalization.Format(
                "analysis.tooltip.lapSample",
                "圈速 {0}  ·  {1} {2}\n{3:MM-dd HH:mm:ss}  ·  {4}\n当前 {5}  ·  距离 {6:0.000} km\n速度 {7:0.0} km/h  ·  {8} 挡  ·  {9:0} RPM\n油门 {10:P0}  ·  制动 {11:P0}{12}",
                FormatTime(lap.TotalSeconds),
                PerformanceClassCatalog.Name(lap.Vehicle.CarClass),
                performanceIndex,
                lap.StartedAt.ToLocalTime(),
                validity,
                FormatTime(sample.ElapsedSeconds),
                sample.S / 1000,
                sample.SpeedMps * 3.6,
                GearText(sample.Gear),
                sample.Rpm,
                sample.Accel,
                sample.Brake,
                position),
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

internal sealed class LapInputChart : FrameworkElement
{
    private static readonly Color ThrottleColor = Color.FromRgb(57, 217, 138);
    private static readonly Color BrakeColor = Color.FromRgb(255, 104, 117);
    private static readonly Color SteeringColor = Color.FromRgb(32, 184, 207);
    private readonly LapRecord lap;
    private readonly double progressExtent;
    private readonly ToolTip hoverToolTip;
    private readonly LapAnalysisCursor linkedCursor;
    private LapSample? hover;
    private DrawingGroup? baseDrawing;
    private Rect baseBounds;

    public LapInputChart(
        LapRecord lap,
        double? trackLengthMeters,
        LapAnalysisCursor linkedCursor)
    {
        this.lap = lap;
        this.linkedCursor = linkedCursor;
        progressExtent = ChartInteractionAlgorithms.ResolveProgressExtent(
            [lap.Samples],
            trackLengthMeters);
        hoverToolTip = LapTelemetryChart.CreateToolTip(this);
        linkedCursor.Changed += OnLinkedCursorChanged;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MouseMove += (_, eventArgs) => UpdateHover(eventArgs.GetPosition(this));
        MouseLeave += (_, _) => ClearHover();
        PreviewMouseLeftButtonDown += (_, _) => linkedCursor.Commit(this);
        Unloaded += (_, _) =>
        {
            hoverToolTip.IsOpen = false;
            linkedCursor.Changed -= OnLinkedCursorChanged;
        };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryBounds(out var bounds)) return;
        EnsureBaseDrawing(bounds);
        if (baseDrawing is not null) drawingContext.DrawDrawing(baseDrawing);
        if (hover is null) return;

        var x = XForProgress(hover.S, bounds);
        drawingContext.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(105, 238, 244, 248)), 1)
            {
                DashStyle = DashStyles.Dash
            },
            new Point(x, bounds.Top),
            new Point(x, bounds.Bottom));
        DrawValueMarker(drawingContext, x, ValueY(hover.Accel, bounds), ThrottleColor);
        DrawValueMarker(drawingContext, x, ValueY(hover.Brake, bounds), BrakeColor);
        if (hover.Dynamics is { } dynamics)
            DrawValueMarker(
                drawingContext,
                x,
                ValueY((dynamics.Steering + 1) / 2, bounds),
                SteeringColor);
    }

    private void EnsureBaseDrawing(Rect bounds)
    {
        if (baseDrawing is not null && baseBounds == bounds) return;
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(18, 23, 30)),
                new Pen(new SolidColorBrush(Color.FromRgb(48, 58, 72)), 1),
                bounds);
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(24, 122, 155, 174)), 1);
            for (var index = 1; index < 5; index++)
            {
                var x = bounds.Left + bounds.Width * index / 5;
                context.DrawLine(gridPen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            }
            for (var index = 1; index < 4; index++)
            {
                var y = bounds.Top + bounds.Height * index / 4;
                context.DrawLine(gridPen, new Point(bounds.Left, y), new Point(bounds.Right, y));
            }
            context.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(90, SteeringColor.R, SteeringColor.G, SteeringColor.B)), 1)
                {
                    DashStyle = DashStyles.Dash
                },
                new Point(bounds.Left, ValueY(0.5, bounds)),
                new Point(bounds.Right, ValueY(0.5, bounds)));
            DrawCurve(context, bounds, sample => sample.Accel, ThrottleColor, 2.1);
            DrawCurve(context, bounds, sample => sample.Brake, BrakeColor, 2.1);
            if (lap.Samples.Any(sample => sample.Dynamics is not null))
                DrawCurve(
                    context,
                    bounds,
                    sample => sample.Dynamics is { } dynamics ? (dynamics.Steering + 1) / 2 : 0.5,
                    SteeringColor,
                    1.7);
            DrawLegend(context, bounds);
        }
        if (drawing.CanFreeze) drawing.Freeze();
        baseDrawing = drawing;
        baseBounds = bounds;
    }

    private void DrawCurve(
        DrawingContext context,
        Rect bounds,
        Func<LapSample, double> value,
        Color color,
        double thickness)
    {
        if (lap.Samples.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(
                new Point(
                    XForProgress(lap.Samples[0].S, bounds),
                    ValueY(value(lap.Samples[0]), bounds)),
                isFilled: false,
                isClosed: false);
            for (var index = 1; index < lap.Samples.Count; index++)
            {
                path.LineTo(
                    new Point(
                        XForProgress(lap.Samples[index].S, bounds),
                        ValueY(value(lap.Samples[index]), bounds)),
                    isStroked: true,
                    isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawLegend(DrawingContext context, Rect bounds)
    {
        var pixelsPerDip = 1d;
        var items = new[]
        {
            ("油门", ThrottleColor),
            ("制动", BrakeColor),
            ("方向（中线为回正）", SteeringColor)
        };
        var x = bounds.Left + 10;
        foreach (var (text, color) in items)
        {
            context.DrawLine(
                new Pen(new SolidColorBrush(color), 2.5),
                new Point(x, bounds.Top + 14),
                new Point(x + 17, bounds.Top + 14));
            var formatted = new FormattedText(
                AppLocalization.Literal(text),
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                10,
                new SolidColorBrush(Color.FromRgb(214, 224, 232)),
                pixelsPerDip);
            context.DrawText(formatted, new Point(x + 23, bounds.Top + 6));
            x += 23 + formatted.Width + 18;
        }
    }

    private void UpdateHover(Point pointer)
    {
        if (!TryBounds(out var bounds) || !bounds.Contains(pointer))
        {
            ClearHover();
            return;
        }
        var progress = Math.Clamp(
            (pointer.X - bounds.Left) / bounds.Width * progressExtent,
            0,
            progressExtent);
        var sampleIndex = ChartInteractionAlgorithms.FindNearestProgressSample(lap.Samples, progress);
        SetHover(lap.Samples[sampleIndex], pointer, publish: true);
    }

    private void SetHover(LapSample sample, Point pointer, bool publish)
    {
        var changed = !ReferenceEquals(hover, sample);
        hover = sample;
        if (publish)
        {
            var steering = sample.Dynamics is { } dynamics
                ? $"{dynamics.Steering:+0.00;-0.00;0.00}"
                : AppLocalization.Literal("旧圈无数据");
            hoverToolTip.Content = new TextBlock
            {
                Text = AppLocalization.Format(
                    "analysis.tooltip.inputs",
                    "距离 {0:0.000} km · 时间 {1:m\\:ss\\.fff}\n速度 {2:0.0} km/h · 油门 {3:P0} · 制动 {4:P0}\n方向 {5}",
                    sample.S / 1000,
                    TimeSpan.FromSeconds(sample.ElapsedSeconds),
                    sample.SpeedMps * 3.6,
                    sample.Accel,
                    sample.Brake,
                    steering),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(244, 247, 250)),
                LineHeight = 18
            };
            hoverToolTip.HorizontalOffset = Math.Min(pointer.X + 14, Math.Max(8, ActualWidth - 270));
            hoverToolTip.VerticalOffset = Math.Min(pointer.Y + 14, Math.Max(8, ActualHeight - 110));
            hoverToolTip.IsOpen = true;
            Cursor = Cursors.Cross;
            linkedCursor.Set(this, lap.Id, sample.S);
        }
        else
        {
            hoverToolTip.IsOpen = false;
            Cursor = Cursors.Arrow;
        }
        if (changed) InvalidateVisual();
    }

    private void ClearHover(bool publish = true)
    {
        if (hover is null && !hoverToolTip.IsOpen) return;
        hover = null;
        hoverToolTip.IsOpen = false;
        Cursor = Cursors.Arrow;
        if (publish) linkedCursor.Clear(this);
        InvalidateVisual();
    }

    private void OnLinkedCursorChanged(object source, LapAnalysisCursorPosition? position)
    {
        if (ReferenceEquals(source, this)) return;
        if (position is null)
        {
            ClearHover(publish: false);
            return;
        }
        var sampleIndex = ChartInteractionAlgorithms.FindNearestProgressSample(
            lap.Samples,
            position.Value.ProgressMeters);
        SetHover(lap.Samples[sampleIndex], default, publish: false);
    }

    private bool TryBounds(out Rect bounds)
    {
        if (lap.Samples.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            bounds = Rect.Empty;
            return false;
        }
        bounds = new Rect(42, 10, Math.Max(1, ActualWidth - 58), Math.Max(1, ActualHeight - 24));
        return true;
    }

    private double XForProgress(double progress, Rect bounds) =>
        bounds.Left + bounds.Width * Math.Clamp(progress / Math.Max(1, progressExtent), 0, 1);

    private static double ValueY(double value, Rect bounds) =>
        bounds.Bottom - bounds.Height * Math.Clamp(value, 0, 1);

    private static void DrawValueMarker(
        DrawingContext context,
        double x,
        double y,
        Color color)
    {
        context.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(14, 21, 29)),
            new Pen(Brushes.White, 1.2),
            new Point(x, y),
            4.5,
            4.5);
        context.DrawEllipse(new SolidColorBrush(color), null, new Point(x, y), 2.5, 2.5);
    }
}

internal sealed class TrackMapView : FrameworkElement
{
    private const double MinimumZoom = 1;
    private const double MaximumZoom = 24;
    private const double HoverRadius = 11;
    private const double HoverCellSize = 24;
    private readonly LapRecord[] laps;
    private readonly LapSeriesLegendEntry[] legendEntries;
    private readonly CornerMapAnnotation[] cornerAnnotations;
    private readonly TrackPoint[] trackPoints;
    private readonly TrackLayoutKind layoutKind;
    private readonly TrackEndpointSummary? endpoints;
    private readonly Guid dynamicsLapId;
    private readonly ToolTip hoverToolTip;
    private readonly LapAnalysisCursor? linkedCursor;
    private readonly double mapMinimumX;
    private readonly double mapMinimumZ;
    private readonly double mapSpanX;
    private readonly double mapSpanZ;
    private ChartViewport viewport = new(MinimumZoom, 0, 0);
    private LapVisualHit? hover;
    private CornerMapAnnotation? cornerHover;
    private ScreenHitGrid? hoverGrid;
    private MapViewportKey? hoverGridKey;
    private CornerMarkerLayout[] cornerMarkerLayouts = [];
    private CornerMarkerLayoutKey? cornerMarkerLayoutKey;
    private DrawingGroup? baseDrawing;
    private MapDrawingKey? baseDrawingKey;
    private bool dragging;
    private Point dragStart;
    private bool showEndpoints = true;
    private bool showCornerAnnotations = true;
    private bool showLegend = true;
    private DrivingDynamicsLayer dynamicsLayer;
    private double? playbackElapsedSeconds;

    public TrackMapView(
        IReadOnlyList<LapRecord> laps,
        TrackTemplate? track,
        IReadOnlyList<LapSeriesLegendEntry>? legendEntries = null,
        IReadOnlyList<CornerMapAnnotation>? cornerAnnotations = null,
        Guid? dynamicsLapId = null,
        LapAnalysisCursor? linkedCursor = null)
    {
        this.laps = laps.Where(lap => lap.Samples.Count >= 2).Take(4).ToArray();
        this.legendEntries = (legendEntries ?? [])
            .Take(this.laps.Length)
            .ToArray();
        this.cornerAnnotations = (cornerAnnotations ?? [])
            .Where(annotation => this.laps.Any(lap => lap.Id == annotation.LapId))
            .ToArray();
        this.dynamicsLapId = dynamicsLapId ??
                             this.laps.FirstOrDefault()?.Id ??
                             Guid.Empty;
        this.linkedCursor = linkedCursor;
        if (linkedCursor is not null) linkedCursor.Changed += OnLinkedCursorChanged;
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
        PreviewMouseLeftButtonDown += (_, _) => linkedCursor?.Commit(this);
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseLeave += (_, _) =>
        {
            if (!dragging) ClearHover();
        };
        LostMouseCapture += (_, _) => dragging = false;
        Unloaded += (_, _) =>
        {
            hoverToolTip.IsOpen = false;
            if (this.linkedCursor is not null)
                this.linkedCursor.Changed -= OnLinkedCursorChanged;
        };
    }

    public bool ShowEndpoints
    {
        get => showEndpoints;
        set
        {
            if (showEndpoints == value) return;
            showEndpoints = value;
            baseDrawing = null;
            baseDrawingKey = null;
            InvalidateVisual();
        }
    }

    public bool ShowCornerAnnotations
    {
        get => showCornerAnnotations;
        set
        {
            if (showCornerAnnotations == value) return;
            showCornerAnnotations = value;
            if (!value && cornerHover is not null) ClearHover();
            InvalidateVisual();
        }
    }

    public bool ShowLegend
    {
        get => showLegend;
        set
        {
            if (showLegend == value) return;
            showLegend = value;
            cornerMarkerLayouts = [];
            cornerMarkerLayoutKey = null;
            InvalidateVisual();
        }
    }

    public int CornerAnnotationCount => cornerAnnotations.Length;

    public DrivingDynamicsLayer DynamicsLayer
    {
        get => dynamicsLayer;
        set
        {
            if (dynamicsLayer == value) return;
            dynamicsLayer = value;
            baseDrawing = null;
            baseDrawingKey = null;
            ClearHover();
            InvalidateVisual();
        }
    }

    public double? PlaybackElapsedSeconds
    {
        get => playbackElapsedSeconds;
        set
        {
            if (playbackElapsedSeconds == value) return;
            playbackElapsedSeconds = value;
            InvalidateVisual();
        }
    }

    public bool HasExtendedDynamics =>
        laps.FirstOrDefault(lap => lap.Id == dynamicsLapId)?
            .Samples.Any(sample => sample.Dynamics is not null) == true;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryMetrics(out var metrics)) return;
        viewport = ClampViewport(viewport, metrics);
        EnsureBaseDrawing(metrics);
        if (baseDrawing is not null) drawingContext.DrawDrawing(baseDrawing);
        var legendBounds = showLegend
            ? MapLegendBounds(metrics)
            : Rect.Empty;
        if (showCornerAnnotations)
            DrawCornerAnnotations(drawingContext, metrics, legendBounds);
        if (showLegend && dynamicsLayer == DrivingDynamicsLayer.Default)
        {
            AnalysisOverlayDrawing.DrawSeriesLegend(
                drawingContext,
                legendBounds,
                legendEntries,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
        else if (showLegend)
        {
            DrawDynamicsLegend(drawingContext, legendBounds);
        }
        if (dynamicsLayer != DrivingDynamicsLayer.Default &&
            DrivingDynamicsAnalyzer.RequiresExtendedTelemetry(dynamicsLayer) &&
            !HasExtendedDynamics)
        {
            DrawUnavailableNotice(drawingContext, metrics.Bounds);
        }
        DrawPlaybackMarker(drawingContext, metrics);
        DrawZoomBadge(drawingContext, metrics.Bounds);

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
            cornerHover = null;
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

        if (showCornerAnnotations &&
            TryFindCornerAnnotation(pointer, metrics, out var annotation))
        {
            SetCornerHover(annotation, pointer);
            return;
        }

        cornerHover = null;
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
            var tooltip = LapTelemetryChart.TooltipText(
                best.Lap,
                best.Sample,
                includePosition: true);
            if (dynamicsLayer != DrivingDynamicsLayer.Default &&
                best.Lap.Id == dynamicsLapId)
            {
                tooltip.Text += "\n" + DynamicsTooltip(best.Sample, best.Lap.Vehicle);
            }
            hoverToolTip.Content = tooltip;
        }
        hoverToolTip.HorizontalOffset = Math.Min(pointer.X + 14, Math.Max(8, ActualWidth - 245));
        hoverToolTip.VerticalOffset = Math.Min(pointer.Y + 14, Math.Max(8, ActualHeight - 165));
        hoverToolTip.IsOpen = true;
        Cursor = Cursors.None;
        linkedCursor?.Set(this, best.Lap.Id, best.Sample.S);
        if (changed) InvalidateVisual();
    }

    private void ClearHover(bool publish = true)
    {
        if (hover is null && cornerHover is null && !hoverToolTip.IsOpen) return;
        hover = null;
        cornerHover = null;
        hoverToolTip.IsOpen = false;
        if (!dragging) Cursor = viewport.Zoom > MinimumZoom ? Cursors.Hand : Cursors.Arrow;
        if (publish) linkedCursor?.Clear(this);
        InvalidateVisual();
    }

    private void OnLinkedCursorChanged(object source, LapAnalysisCursorPosition? position)
    {
        if (ReferenceEquals(source, this)) return;
        if (position is null)
        {
            ClearHover(publish: false);
            return;
        }

        var seriesIndex = Array.FindIndex(laps, lap => lap.Id == position.Value.LapId);
        if (seriesIndex < 0) seriesIndex = 0;
        if (seriesIndex < 0 || seriesIndex >= laps.Length) return;
        var lap = laps[seriesIndex];
        var sampleIndex = ChartInteractionAlgorithms.FindNearestProgressSample(
            lap.Samples,
            position.Value.ProgressMeters);
        hover = new LapVisualHit(
            lap,
            lap.Samples[sampleIndex],
            seriesIndex,
            default);
        cornerHover = null;
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
            VisualTreeHelper.GetDpi(this).PixelsPerDip,
            dynamicsLayer);
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
                DrawRoute(drawingContext, laps[index], index, metrics);
            if (showEndpoints) DrawTrackEndpoints(drawingContext, metrics);
            drawingContext.Pop();
        }

        if (drawing.CanFreeze) drawing.Freeze();
        baseDrawing = drawing;
        baseDrawingKey = drawingKey;
    }

    private void DrawRoute(
        DrawingContext drawingContext,
        LapRecord lap,
        int seriesIndex,
        MapMetrics metrics)
    {
        var samples = lap.Samples;
        if (samples.Count < 2) return;
        if (dynamicsLayer != DrivingDynamicsLayer.Default)
        {
            if (lap.Id == dynamicsLapId)
                DrawDynamicsRoute(drawingContext, lap, metrics);
            else
                DrawDimmedRoute(drawingContext, samples, metrics);
            return;
        }

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

    private void DrawDimmedRoute(
        DrawingContext drawingContext,
        IReadOnlyList<LapSample> samples,
        MapMetrics metrics)
    {
        var geometry = RouteGeometry(samples, metrics);
        var pen = new Pen(
            new SolidColorBrush(Color.FromArgb(100, 145, 158, 170)),
            1.4);
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private void DrawDynamicsRoute(
        DrawingContext drawingContext,
        LapRecord lap,
        MapMetrics metrics)
    {
        var samples = lap.Samples;
        var outline = new Pen(
            new SolidColorBrush(Color.FromArgb(230, 7, 11, 16)),
            5.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        outline.Freeze();
        drawingContext.DrawGeometry(null, outline, RouteGeometry(samples, metrics));

        var segments = new Dictionary<int, List<(Point Start, Point End)>>();
        for (var index = 1; index < samples.Count; index++)
        {
            var point = DrivingDynamicsAnalyzer.Evaluate(
                samples[index],
                lap.Vehicle,
                dynamicsLayer);
            var bucket = DynamicsBucket(point);
            if (!segments.TryGetValue(bucket, out var bucketSegments))
            {
                bucketSegments = [];
                segments[bucket] = bucketSegments;
            }
            bucketSegments.Add((
                MapPoint(samples[index - 1], metrics, viewport),
                MapPoint(samples[index], metrics, viewport)));
        }

        foreach (var (bucket, bucketSegments) in segments)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                foreach (var segment in bucketSegments)
                {
                    context.BeginFigure(segment.Start, isFilled: false, isClosed: false);
                    context.LineTo(segment.End, isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            var pen = new Pen(
                new SolidColorBrush(DynamicsColor(bucket)),
                3.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }

    private StreamGeometry RouteGeometry(
        IReadOnlyList<LapSample> samples,
        MapMetrics metrics)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(
                MapPoint(samples[0], metrics, viewport),
                isFilled: false,
                isClosed: false);
            for (var index = 1; index < samples.Count; index++)
                context.LineTo(
                    MapPoint(samples[index], metrics, viewport),
                    isStroked: true,
                    isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawCornerAnnotations(
        DrawingContext drawingContext,
        MapMetrics metrics,
        Rect legendBounds)
    {
        if (cornerAnnotations.Length == 0) return;
        drawingContext.PushClip(new RectangleGeometry(metrics.Bounds));
        foreach (var layout in ResolveCornerMarkerLayouts(metrics, legendBounds))
        {
            DrawCornerAnalysisPoint(
                drawingContext,
                layout.Marker,
                layout.Annotation,
                ReferenceEquals(cornerHover, layout.Annotation),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
        drawingContext.Pop();
    }

    private bool TryFindCornerAnnotation(
        Point pointer,
        MapMetrics metrics,
        out CornerMapAnnotation annotation)
    {
        var legendBounds = showLegend
            ? MapLegendBounds(metrics)
            : Rect.Empty;
        var layouts = ResolveCornerMarkerLayouts(metrics, legendBounds);
        for (var index = layouts.Count - 1; index >= 0; index--)
        {
            var candidate = layouts[index];
            var hitArea = candidate.Marker.Bounds;
            hitArea.Inflate(6, 6);
            if (!hitArea.Contains(pointer)) continue;
            annotation = candidate.Annotation;
            return true;
        }

        annotation = null!;
        return false;
    }

    private Rect MapLegendBounds(MapMetrics metrics)
    {
        var reserved = new[]
        {
            MapControlsReservedBounds(metrics.Bounds),
            MapZoomReservedBounds(metrics.Bounds)
        };
        var selected = AnalysisOverlayDrawing.SelectSeriesLegendBounds(
            metrics.Bounds,
            dynamicsLayer == DrivingDynamicsLayer.Default
                ? legendEntries.Length
                : 1,
            MapLegendPoints(metrics),
            reserved,
            AnalysisLegendCorner.TopLeft,
            AnalysisLegendCorner.BottomRight,
            AnalysisLegendCorner.TopRight);
        if (dynamicsLayer == DrivingDynamicsLayer.Default || selected.IsEmpty)
            return selected;

        var targetWidth = Math.Min(
            dynamicsLayer == DrivingDynamicsLayer.HandlingBalance ? 206 : 220,
            Math.Max(176, metrics.Bounds.Width - 24));
        const double targetHeight = 44;
        var alignRight = selected.Left + selected.Width / 2 >
                         metrics.Bounds.Left + metrics.Bounds.Width / 2;
        var alignBottom = selected.Top + selected.Height / 2 >
                          metrics.Bounds.Top + metrics.Bounds.Height / 2;
        return new Rect(
            alignRight ? selected.Right - targetWidth : selected.Left,
            alignBottom ? selected.Bottom - targetHeight : selected.Top,
            targetWidth,
            targetHeight);
    }

    private void DrawDynamicsLegend(
        DrawingContext drawingContext,
        Rect bounds)
    {
        if (bounds.IsEmpty) return;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(205, 14, 21, 29)),
            new Pen(new SolidColorBrush(Color.FromArgb(205, 67, 84, 101)), 1),
            bounds,
            8,
            8);
        var title = new FormattedText(
            AppLocalization.Literal(DrivingDynamicsAnalyzer.LayerName(dynamicsLayer)),
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal),
            10,
            new SolidColorBrush(Color.FromRgb(242, 247, 250)),
            pixelsPerDip);
        drawingContext.DrawText(title, new Point(bounds.Left + 10, bounds.Top + 6));

        if (dynamicsLayer == DrivingDynamicsLayer.HandlingBalance)
        {
            DrawLegendChip(
                drawingContext,
                bounds.Left + 10,
                bounds.Top + 25,
                Color.FromRgb(242, 184, 39),
                "疑似不足",
                pixelsPerDip);
            DrawLegendChip(
                drawingContext,
                bounds.Left + 91,
                bounds.Top + 25,
                Color.FromRgb(222, 90, 220),
                "疑似过度",
                pixelsPerDip);
            return;
        }

        var gradientBounds = new Rect(
            bounds.Left + 10,
            bounds.Top + 28,
            Math.Max(60, bounds.Width - 64),
            7);
        var brush = new LinearGradientBrush(
            DynamicsColor(0),
            DynamicsColor(15),
            new Point(0, 0),
            new Point(1, 0));
        drawingContext.DrawRoundedRectangle(brush, null, gradientBounds, 3, 3);
        var scale = new FormattedText(
            AppLocalization.Literal(
                dynamicsLayer == DrivingDynamicsLayer.Steering ? "左  ↔  右" : "低  →  高"),
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal),
            8.5,
            new SolidColorBrush(Color.FromRgb(166, 181, 191)),
            pixelsPerDip);
        drawingContext.DrawText(
            scale,
            new Point(bounds.Right - scale.Width - 9, bounds.Top + 24));
    }

    private static void DrawLegendChip(
        DrawingContext drawingContext,
        double x,
        double y,
        Color color,
        string text,
        double pixelsPerDip)
    {
        drawingContext.DrawEllipse(new SolidColorBrush(color), null, new Point(x + 4, y + 6), 4, 4);
        var formatted = new FormattedText(
            AppLocalization.Literal(text),
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal),
            8.5,
            new SolidColorBrush(Color.FromRgb(207, 217, 224)),
            pixelsPerDip);
        drawingContext.DrawText(formatted, new Point(x + 11, y));
    }

    private static void DrawUnavailableNotice(
        DrawingContext drawingContext,
        Rect bounds)
    {
        var text = new FormattedText(
            AppLocalization.Literal("该圈由旧版本记录，未包含此图层所需的动态遥测。"),
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal),
            11,
            new SolidColorBrush(Color.FromRgb(242, 184, 39)),
            1);
        var chrome = new Rect(
            bounds.Left + (bounds.Width - text.Width - 28) / 2,
            bounds.Top + (bounds.Height - text.Height - 20) / 2,
            text.Width + 28,
            text.Height + 20);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(235, 14, 21, 29)),
            new Pen(new SolidColorBrush(Color.FromRgb(151, 112, 28)), 1),
            chrome,
            8,
            8);
        drawingContext.DrawText(text, new Point(chrome.Left + 14, chrome.Top + 10));
    }

    private string DynamicsTooltip(
        LapSample sample,
        VehicleProfileFingerprint vehicle)
    {
        var point = DrivingDynamicsAnalyzer.Evaluate(sample, vehicle, dynamicsLayer);
        if (!point.IsAvailable) return AppLocalization.Literal("当前圈没有此图层所需的动态遥测");
        return dynamicsLayer switch
        {
            DrivingDynamicsLayer.Throttle => AppLocalization.Format(
                "analysis.layer.throttle", "图层 · 油门 {0:P0}", sample.Accel),
            DrivingDynamicsLayer.Brake => AppLocalization.Format(
                "analysis.layer.brake", "图层 · 制动 {0:P0}", sample.Brake),
            DrivingDynamicsLayer.Steering =>
                AppLocalization.Format(
                    "analysis.layer.steering", "图层 · 方向输入 {0:+0.00;-0.00;0.00}", point.SignedValue),
            DrivingDynamicsLayer.TireSlip =>
                AppLocalization.Format(
                    "analysis.layer.tireSlip", "图层 · 轮胎滑移强度 {0:P0}", point.Intensity),
            DrivingDynamicsLayer.HandlingBalance => point.Balance switch
            {
                HandlingBalanceState.SuspectedUndersteer =>
                    AppLocalization.Format(
                        "analysis.layer.understeer", "图层 · 疑似转向不足 · 证据强度 {0:P0}", point.Intensity),
                HandlingBalanceState.SuspectedOversteer =>
                    AppLocalization.Format(
                        "analysis.layer.oversteer", "图层 · 疑似转向过度 · 证据强度 {0:P0}", point.Intensity),
                _ => AppLocalization.Literal("图层 · 未发现明显转向平衡异常")
            },
            DrivingDynamicsLayer.ExitWheelspin =>
                AppLocalization.Format(
                    "analysis.layer.wheelspin", "图层 · 出弯空转证据 {0:P0}", point.Intensity),
            DrivingDynamicsLayer.BrakingInstability =>
                AppLocalization.Format(
                    "analysis.layer.braking", "图层 · 制动轮胎失稳证据 {0:P0}", point.Intensity),
            _ => AppLocalization.Literal(DrivingDynamicsAnalyzer.LayerName(dynamicsLayer))
        };
    }

    private int DynamicsBucket(DrivingDynamicsPoint point)
    {
        if (!point.IsAvailable) return -1;
        var intensity = Math.Clamp((int)Math.Round(point.Intensity * 15), 0, 15);
        if (dynamicsLayer == DrivingDynamicsLayer.Steering)
            return Math.Clamp((int)Math.Round((point.SignedValue + 1) * 7.5), 0, 15);
        if (dynamicsLayer == DrivingDynamicsLayer.HandlingBalance)
            return point.Balance switch
            {
                HandlingBalanceState.SuspectedUndersteer => 100 + intensity,
                HandlingBalanceState.SuspectedOversteer => 200 + intensity,
                _ => 0
            };
        return intensity;
    }

    private Color DynamicsColor(int bucket)
    {
        if (bucket < 0) return Color.FromRgb(91, 103, 113);
        if (dynamicsLayer == DrivingDynamicsLayer.HandlingBalance)
        {
            if (bucket >= 200)
                return Blend(Color.FromRgb(92, 60, 102), Color.FromRgb(246, 76, 219), (bucket - 200) / 15d);
            if (bucket >= 100)
                return Blend(Color.FromRgb(91, 78, 45), Color.FromRgb(255, 188, 35), (bucket - 100) / 15d);
            return Color.FromRgb(76, 91, 101);
        }
        var amount = Math.Clamp(bucket / 15d, 0, 1);
        return dynamicsLayer switch
        {
            DrivingDynamicsLayer.Throttle =>
                Blend(Color.FromRgb(54, 78, 78), Color.FromRgb(51, 232, 144), amount),
            DrivingDynamicsLayer.Brake =>
                Blend(Color.FromRgb(76, 66, 72), Color.FromRgb(255, 75, 87), amount),
            DrivingDynamicsLayer.Steering when bucket < 8 =>
                Blend(Color.FromRgb(51, 151, 226), Color.FromRgb(116, 126, 145), bucket / 7d),
            DrivingDynamicsLayer.Steering =>
                Blend(Color.FromRgb(116, 126, 145), Color.FromRgb(206, 83, 255), (bucket - 8) / 7d),
            DrivingDynamicsLayer.TireSlip =>
                Blend(Color.FromRgb(68, 101, 112), Color.FromRgb(255, 89, 51), amount),
            DrivingDynamicsLayer.ExitWheelspin =>
                Blend(Color.FromRgb(79, 79, 68), Color.FromRgb(255, 160, 33), amount),
            DrivingDynamicsLayer.BrakingInstability =>
                Blend(Color.FromRgb(77, 67, 85), Color.FromRgb(255, 58, 118), amount),
            _ => LapSeriesPalette.At(0)
        };
    }

    private static Color Blend(Color start, Color end, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(start.R + (end.R - start.R) * amount),
            (byte)Math.Round(start.G + (end.G - start.G) * amount),
            (byte)Math.Round(start.B + (end.B - start.B) * amount));
    }

    private void DrawPlaybackMarker(
        DrawingContext drawingContext,
        MapMetrics metrics)
    {
        if (playbackElapsedSeconds is not double elapsed) return;
        var lap = laps.FirstOrDefault(candidate => candidate.Id == dynamicsLapId) ??
                  laps.FirstOrDefault();
        if (lap is null || lap.Samples.Count == 0) return;
        var index = FindNearestElapsedSample(lap.Samples, elapsed);
        var point = MapPoint(lap.Samples[index], metrics, viewport);
        drawingContext.PushClip(new RectangleGeometry(metrics.Bounds));
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(95, 32, 184, 207)),
            null,
            point,
            12,
            12);
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(242, 247, 250)),
            new Pen(new SolidColorBrush(Color.FromRgb(32, 184, 207)), 3),
            point,
            6,
            6);
        drawingContext.Pop();
    }

    private static int FindNearestElapsedSample(
        IReadOnlyList<LapSample> samples,
        double elapsed)
    {
        var low = 0;
        var high = samples.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].ElapsedSeconds < elapsed) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return 0;
        return Math.Abs(samples[low].ElapsedSeconds - elapsed) <
               Math.Abs(samples[low - 1].ElapsedSeconds - elapsed)
            ? low
            : low - 1;
    }

    private IReadOnlyList<Point> MapLegendPoints(MapMetrics metrics)
    {
        var points = new List<Point>(laps.Length * 500);
        foreach (var lap in laps)
        {
            var stride = Math.Max(1, lap.Samples.Count / 500);
            for (var index = 0; index < lap.Samples.Count; index += stride)
                points.Add(MapPoint(lap.Samples[index], metrics, viewport));
        }

        return points;
    }

    private IReadOnlyList<CornerMarkerLayout> ResolveCornerMarkerLayouts(
        MapMetrics metrics,
        Rect legendBounds)
    {
        var key = new CornerMarkerLayoutKey(
            CreateViewportKey(metrics),
            legendBounds);
        if (cornerMarkerLayoutKey is { } cachedKey &&
            cachedKey.Equals(key))
            return cornerMarkerLayouts;

        var routePoints = MapLegendPoints(metrics);
        var reserved = new List<Rect>
        {
            MapControlsReservedBounds(metrics.Bounds),
            MapZoomReservedBounds(metrics.Bounds)
        };
        if (!legendBounds.IsEmpty) reserved.Add(legendBounds);

        var layouts = new List<CornerMarkerLayout>(cornerAnnotations.Length);
        foreach (var annotation in cornerAnnotations)
        {
            if (!TryCreateCornerMarker(
                    annotation,
                    metrics,
                    routePoints,
                    reserved,
                    out var marker))
                continue;
            layouts.Add(new CornerMarkerLayout(annotation, marker));
            var occupied = marker.Bounds;
            occupied.Inflate(7, 7);
            reserved.Add(occupied);
        }

        cornerMarkerLayouts = layouts.ToArray();
        cornerMarkerLayoutKey = key;
        return cornerMarkerLayouts;
    }

    private bool TryCreateCornerMarker(
        CornerMapAnnotation annotation,
        MapMetrics metrics,
        IReadOnlyList<Point> routePoints,
        IReadOnlyList<Rect> reservedBounds,
        out CornerMarker marker)
    {
        var seriesIndex = Array.FindIndex(laps, lap => lap.Id == annotation.LapId);
        if (seriesIndex < 0)
        {
            marker = default;
            return false;
        }

        var samples = laps[seriesIndex].Samples;
        var sampleIndex = ChartInteractionAlgorithms.FindNearestProgressSample(
            samples,
            annotation.Window.ApexS);
        if (sampleIndex < 0 || sampleIndex >= samples.Count)
        {
            marker = default;
            return false;
        }

        var anchor = MapPoint(samples[sampleIndex], metrics, viewport);
        if (!metrics.Bounds.Contains(anchor))
        {
            marker = default;
            return false;
        }

        var previous = MapPoint(
            samples[Math.Max(0, sampleIndex - 4)],
            metrics,
            viewport);
        var next = MapPoint(
            samples[Math.Min(samples.Count - 1, sampleIndex + 4)],
            metrics,
            viewport);
        var tangent = next - previous;
        if (tangent.Length < 0.5) tangent = new Vector(1, 0);
        tangent.Normalize();
        var normal = new Vector(-tangent.Y, tangent.X);
        var safeBounds = metrics.Bounds;
        safeBounds.Inflate(-13, -13);
        var bestScore = double.NegativeInfinity;
        var best = default(CornerMarker);
        var found = false;
        foreach (var distance in new[] { 25d, 33d, 41d })
        {
            foreach (var side in new[] { 1d, -1d })
            {
                foreach (var along in new[] { 0d, -12d, 12d })
                {
                    var center = anchor + normal * (distance * side) + tangent * along;
                    if (!safeBounds.Contains(center)) continue;
                    var bounds = new Rect(center.X - 10, center.Y - 10, 20, 20);
                    if (reservedBounds.Any(reserved => reserved.IntersectsWith(bounds)))
                        continue;

                    var clearance = MinimumDistance(center, routePoints);
                    if (clearance < 14) continue;
                    var score =
                        Math.Min(clearance, 32) -
                        Math.Max(0, distance - 25) * 0.6 -
                        Math.Abs(along) * 0.05;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = new CornerMarker(anchor, center, bounds);
                    found = true;
                }
            }
        }

        marker = best;
        return found;
    }

    private static double MinimumDistance(
        Point point,
        IReadOnlyList<Point> routePoints)
    {
        var minimumSquared = double.PositiveInfinity;
        foreach (var routePoint in routePoints)
        {
            var dx = point.X - routePoint.X;
            var dy = point.Y - routePoint.Y;
            minimumSquared = Math.Min(minimumSquared, dx * dx + dy * dy);
        }

        return double.IsFinite(minimumSquared)
            ? Math.Sqrt(minimumSquared)
            : double.PositiveInfinity;
    }

    private static Rect MapControlsReservedBounds(Rect bounds) =>
        new(bounds.Left + 8, bounds.Bottom - 62, 150, 54);

    private static Rect MapZoomReservedBounds(Rect bounds) =>
        new(bounds.Right - 78, bounds.Top + 7, 70, 32);

    private void SetCornerHover(
        CornerMapAnnotation annotation,
        Point pointer)
    {
        var changed = !ReferenceEquals(cornerHover, annotation);
        cornerHover = annotation;
        hover = null;
        linkedCursor?.Set(this, annotation.LapId, annotation.Window.ApexS);
        if (changed)
        {
            hoverToolTip.BorderBrush = new SolidColorBrush(annotation.Accent);
            hoverToolTip.Content = CornerTooltip(annotation);
        }
        hoverToolTip.HorizontalOffset = Math.Min(pointer.X + 16, Math.Max(8, ActualWidth - 500));
        hoverToolTip.VerticalOffset = Math.Min(pointer.Y + 16, Math.Max(8, ActualHeight - 265));
        hoverToolTip.IsOpen = true;
        Cursor = Cursors.Hand;
        if (changed) InvalidateVisual();
    }

    private static TextBlock CornerTooltip(CornerMapAnnotation annotation) => new()
    {
        Text =
            $"{annotation.Context}\n" +
            $"{annotation.Title}\n" +
            $"{annotation.Details}\n" +
            $"{annotation.Hint}\n\n" +
            annotation.Footer,
        FontFamily = new FontFamily("Microsoft YaHei UI"),
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(244, 247, 250)),
        LineHeight = 19,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 470
    };

    private static void DrawCornerAnalysisPoint(
        DrawingContext drawingContext,
        CornerMarker marker,
        CornerMapAnnotation annotation,
        bool highlighted,
        double pixelsPerDip)
    {
        var accent = annotation.Accent;
        var connector = new Pen(
            new SolidColorBrush(Color.FromArgb(210, accent.R, accent.G, accent.B)),
            highlighted ? 2 : 1.25)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var direction = marker.Anchor - marker.Center;
        if (direction.Length < 0.5) direction = new Vector(0, 1);
        direction.Normalize();
        var connectionPoint = marker.Center + direction * 10;
        connector.DashStyle = new DashStyle([2, 2], 0);
        drawingContext.DrawLine(connector, marker.Anchor, connectionPoint);
        drawingContext.DrawEllipse(
            null,
            connector,
            marker.Anchor,
            highlighted ? 4 : 3.2,
            highlighted ? 4 : 3.2);

        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)),
            null,
            marker.Center + new Vector(0, 2),
            10,
            10);
        if (highlighted)
        {
            drawingContext.DrawEllipse(
                null,
                new Pen(new SolidColorBrush(Color.FromArgb(105, accent.R, accent.G, accent.B)), 4),
                marker.Center,
                12,
                12);
        }
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(238, 14, 21, 29)),
            new Pen(
                new SolidColorBrush(highlighted
                    ? Color.FromRgb(255, 255, 255)
                    : accent),
                highlighted ? 1.8 : 1.5),
            marker.Center,
            10,
            10);

        var number = new FormattedText(
            annotation.Window.Number.ToString(),
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.Bold,
                FontStretches.Normal),
            10,
            new SolidColorBrush(highlighted
                ? Color.FromRgb(255, 255, 255)
                : accent),
            pixelsPerDip);
        drawingContext.DrawText(
            number,
            new Point(
                marker.Center.X - number.Width / 2,
                marker.Center.Y - number.Height / 2 - 0.5));
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
            AppLocalization.Literal(label),
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

    private void DrawZoomBadge(DrawingContext drawingContext, Rect bounds)
    {
        var text = new FormattedText(
            $"{viewport.Zoom:0.00}×",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10,
            new SolidColorBrush(Color.FromRgb(230, 237, 242)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var badge = new Rect(
            bounds.Right - text.Width - 26,
            bounds.Top + 10,
            text.Width + 16,
            text.Height + 8);
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
        double PixelsPerDip,
        DrivingDynamicsLayer DynamicsLayer);

    private readonly record struct CornerMarkerLayoutKey(
        MapViewportKey ViewportKey,
        Rect LegendBounds);

    private readonly record struct CornerMarkerLayout(
        CornerMapAnnotation Annotation,
        CornerMarker Marker);

    private readonly record struct CornerMarker(
        Point Anchor,
        Point Center,
        Rect Bounds);
}
