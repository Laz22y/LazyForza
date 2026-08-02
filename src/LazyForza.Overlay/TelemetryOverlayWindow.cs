using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;

namespace LazyForza.Overlay;

internal sealed class TelemetryOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private readonly Canvas surfaceHost;
    private readonly HudSurface dashboardSurface;
    private readonly HudSurface lapSurface;
    private readonly HudSurface driftSurface;
    private OverlayLayout layout;
    private HwndSource? source;

    public TelemetryOverlayWindow(Func<IReadOnlyList<IHudContribution>> getContributions, OverlayLayout initialLayout)
    {
        layout = initialLayout;
        Title = "LazyForza HUD";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        surfaceHost = new Canvas { ClipToBounds = true };
        dashboardSurface = new HudSurface(
            getContributions,
            () => layout,
            HudSurfaceKind.Dashboard);
        lapSurface = new HudSurface(
            getContributions,
            () => layout,
            HudSurfaceKind.Lap);
        driftSurface = new HudSurface(
            getContributions,
            () => layout,
            HudSurfaceKind.Drift);
        surfaceHost.Children.Add(dashboardSurface);
        surfaceHost.Children.Add(lapSurface);
        surfaceHost.Children.Add(driftSurface);
        Content = surfaceHost;
        ApplyLayout(initialLayout);
        SourceInitialized += OnSourceInitialized;
    }

    public void ApplyLayout(OverlayLayout newLayout)
    {
        layout = OverlayLayoutGeometry.Normalize(newLayout) with
        {
            ClickThrough = true,
            IsLocked = true,
            DashboardMotionIntensity = Math.Clamp(newLayout.DashboardMotionIntensity, 0, 1),
            DashboardIdleWaitSeconds = Math.Clamp(newLayout.DashboardIdleWaitSeconds, 0, 60),
            DashboardVisibilityFadeSeconds = Math.Clamp(newLayout.DashboardVisibilityFadeSeconds, 0.05, 10),
            LapCompletedHoldSeconds = Math.Clamp(newLayout.LapCompletedHoldSeconds, 0, 15),
            LapNoMatchConfirmationSeconds = Math.Clamp(newLayout.LapNoMatchConfirmationSeconds, 0.1, 60),
            LapNoMatchFadeSeconds = Math.Clamp(newLayout.LapNoMatchFadeSeconds, 0.05, 10),
            LiveHudStaleSeconds = Math.Clamp(newLayout.LiveHudStaleSeconds, 0.05, 10)
        };
        var union = OverlayLayoutGeometry.UnionBounds(layout);
        var dashboard = OverlayLayoutGeometry.Bounds(layout, OverlayHudKind.Dashboard);
        var lap = OverlayLayoutGeometry.Bounds(layout, OverlayHudKind.Lap);
        var drift = OverlayLayoutGeometry.Bounds(layout, OverlayHudKind.Drift);
        Left = union.Left;
        Top = union.Top;
        Width = union.Width;
        Height = union.Height;
        PositionSurface(dashboardSurface, dashboard, union);
        PositionSurface(lapSurface, lap, union);
        PositionSurface(driftSurface, drift, union);
        Opacity = Math.Clamp(layout.Opacity, 0.25, 1);
        UpdateNativeStyles();
        InvalidateHud();
    }

    public OverlayLayout CaptureLayout() => layout;

    public void InvalidateHud()
    {
        dashboardSurface.InvalidateVisual();
        lapSurface.InvalidateVisual();
        driftSurface.InvalidateVisual();
    }

    public void CapturePng(
        string path,
        double targetWidth,
        double targetHeight,
        bool previewDrift = false)
    {
        var previousDriftPreview = driftSurface.LayoutPreview;
        driftSurface.LayoutPreview = previewDrift;
        driftSurface.InvalidateVisual();
        var pixelWidth = Math.Max(1, (int)Math.Round(targetWidth));
        var pixelHeight = Math.Max(1, (int)Math.Round(targetHeight));
        try
        {
            surfaceHost.Measure(new Size(targetWidth, targetHeight));
            surfaceHost.Arrange(new Rect(0, 0, targetWidth, targetHeight));
            surfaceHost.UpdateLayout();
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surfaceHost);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
        }
        finally
        {
            driftSurface.LayoutPreview = previousDriftPreview;
            driftSurface.InvalidateVisual();
        }
    }

    private static void PositionSurface(
        FrameworkElement surface,
        OverlayHudBounds bounds,
        OverlayHudBounds union)
    {
        surface.Width = bounds.Width;
        surface.Height = bounds.Height;
        Canvas.SetLeft(surface, bounds.Left - union.Left);
        Canvas.SetTop(surface, bounds.Top - union.Top);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        source = HwndSource.FromHwnd(helper.Handle);
        source.AddHook(WindowProcedure);
        UpdateNativeStyles();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (message == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void UpdateNativeStyles()
    {
        if (source is null) return;
        var current = GetWindowLongPtr(source.Handle, GwlExStyle).ToInt64();
        var updated = OverlayNativeStyles.Apply(current);
        _ = SetWindowLongPtr(source.Handle, GwlExStyle, new IntPtr(updated));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newLong);
}

internal enum HudSurfaceKind
{
    Dashboard,
    Lap,
    Drift
}

internal sealed class HudSurface : FrameworkElement
{
    private static readonly Typeface NormalTypeface = new(new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
    private static readonly Typeface LightTypeface = new(new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.Normal, FontStretches.Condensed);
    private static readonly Typeface ChineseNormalTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ChineseLightTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush White = BrushOf(0xF3, 0xF4, 0xF5);
    private static readonly Brush Muted = BrushOf(0x8B, 0x90, 0x99);
    private static readonly Brush Graphite = BrushOf(0x20, 0x25, 0x2D);
    private static readonly Brush Cyan = BrushOf(0x20, 0xB8, 0xCF);
    private readonly Func<IReadOnlyList<IHudContribution>> getContributions;
    private readonly Func<OverlayLayout> getLayout;
    private readonly HudSurfaceKind kind;
    private bool layoutPreview;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly FrameRateLimiter limiter;
    private readonly DashboardHudDynamics dashboardDynamics = new();
    private readonly LapHudDynamics lapDynamics = new();
    private double renderedBrake;
    private double previousPedalRenderSeconds;
    private bool pedalAnimationInitialized;

    public HudSurface(
        Func<IReadOnlyList<IHudContribution>> getContributions,
        Func<OverlayLayout> getLayout,
        HudSurfaceKind kind,
        bool layoutPreview = false)
    {
        this.getContributions = getContributions;
        this.getLayout = getLayout;
        this.kind = kind;
        limiter = new FrameRateLimiter(kind == HudSurfaceKind.Lap ? 30 : 60);
        this.layoutPreview = layoutPreview;
        IsHitTestVisible = false;
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    internal bool LayoutPreview
    {
        get => layoutPreview;
        set => layoutPreview = value;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (limiter.ShouldRender(clock.Elapsed.TotalSeconds)) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var contributions = getContributions();
        var orderedContributions = contributions.OrderBy(item => item.ZIndex).ToArray();
        var dashboard = orderedContributions.Select(item => item.Snapshot).OfType<Modules.Dashboard.DashboardHudState>().LastOrDefault();
        var lap = orderedContributions.Select(item => item.Snapshot).OfType<Modules.LapAnalysis.LapHudState>().LastOrDefault();
        var drift = orderedContributions.Select(item => item.Snapshot).OfType<DriftHudState>().LastOrDefault();
        var now = DateTimeOffset.UtcNow;
        var layout = getLayout();
        if (kind == HudSurfaceKind.Dashboard)
        {
            RenderDashboard(
                drawingContext,
                dashboard,
                now,
                layout);
            return;
        }

        if (kind == HudSurfaceKind.Lap)
        {
            RenderLap(drawingContext, dashboard, lap, now, layout);
            return;
        }

        RenderDrift(drawingContext, drift, now, layout);
    }

    private void RenderDashboard(
        DrawingContext drawingContext,
        Modules.Dashboard.DashboardHudState? dashboard,
        DateTimeOffset now,
        OverlayLayout layout)
    {
        if (layoutPreview)
        {
            dashboard = OverlayLayoutPreviewState.Dashboard(dashboard, now);
            DrawDashboard(drawingContext, dashboard, layout);
            return;
        }

        var dashboardVisible = OverlayVisibilityPolicy.ShouldShowDashboard(dashboard, now, layout.LiveHudStaleSeconds);
        var nowSeconds = clock.Elapsed.TotalSeconds;
        var visual = dashboardDynamics.Update(dashboard, dashboardVisible, layout, nowSeconds);
        var motion = new Vector(
            visual.HorizontalOffset * ActualWidth * 0.018,
            visual.VerticalOffset * ActualHeight * 0.024);
        if (dashboardVisible && visual.Opacity > 0.001)
        {
            drawingContext.PushTransform(new TranslateTransform(motion.X, motion.Y));
            drawingContext.PushOpacity(visual.Opacity);
            DrawDashboard(drawingContext, dashboard!, layout);
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }

    private void RenderDrift(
        DrawingContext drawingContext,
        DriftHudState? drift,
        DateTimeOffset now,
        OverlayLayout layout)
    {
        if (layoutPreview)
        {
            drift = OverlayLayoutPreviewState.Drift(drift, now);
            DrawFullDriftDashboard(drawingContext, drift);
            return;
        }

        if (OverlayVisibilityPolicy.ShouldShowDrift(
                drift,
                now,
                layout.LiveHudStaleSeconds))
        {
            DrawFullDriftDashboard(drawingContext, drift!);
        }
    }

    private void RenderLap(
        DrawingContext drawingContext,
        Modules.Dashboard.DashboardHudState? dashboard,
        Modules.LapAnalysis.LapHudState? lap,
        DateTimeOffset now,
        OverlayLayout layout)
    {
        if (layoutPreview)
        {
            lap = OverlayLayoutPreviewState.Lap(lap, now);
            DrawCumulativeLapDelta(drawingContext, lap);
            DrawLapArc(drawingContext, lap);
            return;
        }

        var lapVisible = OverlayVisibilityPolicy.ShouldShowLap(lap, now, layout.LiveHudStaleSeconds);
        var nowSeconds = clock.Elapsed.TotalSeconds;
        var lapVisual = lapDynamics.Update(lap, lapVisible, layout, nowSeconds);
        if (lapVisible && lapVisual.Opacity > 0.001)
        {
            var followsDashboard = layout.LapHudAttachedToDashboard &&
                                   OverlayVisibilityPolicy.ShouldShowDashboard(
                                       dashboard,
                                       now,
                                       layout.LiveHudStaleSeconds);
            if (followsDashboard)
            {
                var dashboardVisual = dashboardDynamics.Update(
                    dashboard,
                    true,
                    layout,
                    nowSeconds);
                drawingContext.PushTransform(new TranslateTransform(
                    dashboardVisual.HorizontalOffset * ActualWidth * 0.018,
                    dashboardVisual.VerticalOffset * ActualHeight * 0.024));
            }
            drawingContext.PushOpacity(lapVisual.Opacity);
            DrawCumulativeLapDelta(drawingContext, lap!);
            DrawLapArc(drawingContext, lap!);
            drawingContext.Pop();
            if (followsDashboard) drawingContext.Pop();
        }
    }

    private void DrawDashboard(
        DrawingContext dc,
        Modules.Dashboard.DashboardHudState state,
        OverlayLayout layout)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var widgets = layout.DashboardWidgets ??
                      DashboardWidgetLayoutSettings.Default;
        var center = new Point(width * 0.5, height * 0.43);
        const double rpmRadiusXFactor = 0.425;
        const double rpmRadiusYFactor = 0.34;
        if (BeginDashboardWidget(
                dc,
                width,
                height,
                widgets,
                DashboardWidgetKind.RpmArc,
                out var rpmTranslated))
        {
            DrawEllipticalArc(
                dc,
                center,
                width * rpmRadiusXFactor,
                height * rpmRadiusYFactor,
                202,
                338,
                BrushOf(0x20, 0x25, 0x2D, 0.45),
                height * 0.022,
                80);
            DrawRpmSegments(
                dc,
                state,
                center,
                width * rpmRadiusXFactor,
                height * rpmRadiusYFactor);
            EndDashboardWidget(dc, rpmTranslated);
        }

        var circleRadius = Math.Min(width * 0.125, height * 0.21);
        var leftCenter = new Point(width * 0.37, height * 0.41);
        var rightCenter = new Point(width * 0.63, height * 0.41);
        if (BeginDashboardWidget(dc, width, height, widgets, DashboardWidgetKind.SpeedGear, out var speedTranslated))
        {
            DrawSpeedGear(dc, state, leftCenter, circleRadius);
            EndDashboardWidget(dc, speedTranslated);
        }
        if (BeginDashboardWidget(dc, width, height, widgets, DashboardWidgetKind.EngineOutput, out var engineTranslated))
        {
            DrawEngineOutput(dc, state, rightCenter, circleRadius);
            EndDashboardWidget(dc, engineTranslated);
        }
        if (BeginDashboardWidget(dc, width, height, widgets, DashboardWidgetKind.Tires, out var tiresTranslated))
        {
            DrawTires(dc, state, width, height);
            EndDashboardWidget(dc, tiresTranslated);
        }
        if (BeginDashboardWidget(dc, width, height, widgets, DashboardWidgetKind.Pedals, out var pedalsTranslated))
        {
            DrawPedals(dc, state, width, height);
            EndDashboardWidget(dc, pedalsTranslated);
        }
        if (BeginDashboardWidget(dc, width, height, widgets, DashboardWidgetKind.Steering, out var steeringTranslated))
        {
            DrawSteering(dc, state, width, height);
            EndDashboardWidget(dc, steeringTranslated);
        }
        if (BeginDashboardWidget(dc, width, height, widgets, DashboardWidgetKind.ClassBadge, out var badgeTranslated))
        {
            DrawClassBadge(dc, state, width, height);
            EndDashboardWidget(dc, badgeTranslated);
        }
    }

    private static bool BeginDashboardWidget(
        DrawingContext dc,
        double width,
        double height,
        DashboardWidgetLayout widgets,
        DashboardWidgetKind kind,
        out bool translated)
    {
        var placement = widgets.Get(kind);
        translated = false;
        if (!placement.IsVisible) return false;
        if (Math.Abs(placement.OffsetX) < 1e-9 &&
            Math.Abs(placement.OffsetY) < 1e-9)
            return true;
        dc.PushTransform(new TranslateTransform(
            placement.OffsetX * width,
            placement.OffsetY * height));
        translated = true;
        return true;
    }

    private static void EndDashboardWidget(
        DrawingContext dc,
        bool translated)
    {
        if (translated) dc.Pop();
    }

    private static void DrawSpeedGear(
        DrawingContext dc,
        Modules.Dashboard.DashboardHudState state,
        Point center,
        double radius)
    {
        DrawGaugeCircle(dc, center, radius, BrushOf(0x8A, 0x8E, 0x94));
        Text(dc, state.GearDisplay, center.X, center.Y - radius * 0.47,
            radius * 0.64, White, TextAlignment.Center, true);
        var gearCueY = center.Y - radius * 0.47;
        if (state.UpshiftCueActive)
            DrawShiftArrow(
                dc,
                new Point(center.X + radius * 0.48, gearCueY),
                radius * 0.16,
                true,
                BrushOf(0x82, 0xE6, 0xAE));
        if (state.DownshiftCueActive)
            DrawShiftArrow(
                dc,
                new Point(center.X - radius * 0.48, gearCueY),
                radius * 0.16,
                false,
                BrushOf(0xFF, 0x91, 0x9D));
        var dividerY = center.Y - radius * 0.02;
        dc.DrawLine(
            new Pen(BrushOf(0x62, 0x68, 0x72, 0.72), 1),
            new Point(center.X - radius * 0.58, dividerY),
            new Point(center.X + radius * 0.58, dividerY));
        Text(dc, state.SpeedKph.ToString("000"), center.X,
            center.Y + radius * 0.23, radius * 0.41, White,
            TextAlignment.Center, true);
        Text(dc, "km/h", center.X, center.Y + radius * 0.59,
            radius * 0.16, Muted, TextAlignment.Center);
    }

    private static void DrawEngineOutput(
        DrawingContext dc,
        Modules.Dashboard.DashboardHudState state,
        Point center,
        double radius)
    {
        var rpmBrush = InterpolateBrush(
            state.MaxRpm <= 0 ? 0 : state.Rpm / state.MaxRpm,
            BrushOf(0x80, 0x84, 0x8A),
            BrushOf(0xF2, 0x18, 0x27));
        DrawGaugeCircle(dc, center, radius, rpmBrush);
        Text(dc, $"{state.Rpm:0}", center.X - radius * 0.08,
            center.Y - radius * 0.48, radius * 0.28, White,
            TextAlignment.Center, true);
        Text(dc, "RPM", center.X + radius * 0.5, center.Y - radius * 0.41,
            radius * 0.12, Muted, TextAlignment.Center);
        dc.DrawLine(
            new Pen(BrushOf(0x42, 0x46, 0x4D), 1),
            new Point(center.X - radius * 0.58, center.Y - radius * 0.12),
            new Point(center.X + radius * 0.58, center.Y - radius * 0.12));
        Text(dc, $"{NonNegativeWholeNumber(state.PowerKw)} kW", center.X,
            center.Y + radius * 0.08, radius * 0.22, White,
            TextAlignment.Center, true);
        Text(dc, $"{NonNegativeWholeNumber(state.TorqueNm)} N·m", center.X,
            center.Y + radius * 0.42, radius * 0.2, White,
            TextAlignment.Center, true);
    }

    private void DrawRpmSegments(DrawingContext dc, Modules.Dashboard.DashboardHudState state, Point center, double radiusX, double radiusY)
    {
        const int count = 16;
        var ratio = state.MaxRpm <= 0 ? 0 : Math.Clamp(state.Rpm / state.MaxRpm, 0, 1);
        for (var index = 0; index < count; index++)
        {
            var start = 202 + index * (136d / count) + 1;
            var end = 202 + (index + 1) * (136d / count) - 1;
            Brush brush;
            var position = (index + 1d) / count;
            if (position > ratio) brush = BrushOf(0x4B, 0x50, 0x58);
            else if (position >= 0.9) brush = BrushOf(0xF2, 0x18, 0x27);
            else if (position >= 0.75) brush = BrushOf(0xF2, 0x9C, 0x1F);
            else brush = White;
            DrawEllipticalArc(dc, center, radiusX, radiusY, start, end, brush, Math.Max(3, ActualHeight * 0.009), 3);
        }
    }

    private static void DrawGaugeCircle(DrawingContext dc, Point center, double radius, Brush rim)
    {
        dc.DrawEllipse(BrushOf(0x0B, 0x0D, 0x10, 0.46), new Pen(rim, Math.Max(2, radius * 0.035)), center, radius, radius);
        dc.DrawEllipse(null, new Pen(BrushOf(0x2B, 0x2F, 0x35), 1), center, radius * 0.92, radius * 0.92);
    }

    private static void DrawTires(DrawingContext dc, Modules.Dashboard.DashboardHudState state, double width, double height)
    {
        Text(dc, "TYRE TEMP / GRIP", width * 0.19, height * 0.635, height * 0.026, Muted, TextAlignment.Center, true);
        var xs = new[] { width * 0.15, width * 0.23 };
        var ys = new[] { height * 0.71, height * 0.81 };
        var temperatures = new[] { state.TireTemperatureCelsius.FrontLeft, state.TireTemperatureCelsius.FrontRight, state.TireTemperatureCelsius.RearLeft, state.TireTemperatureCelsius.RearRight };
        var grips = new[] { state.GripUi.FrontLeft, state.GripUi.FrontRight, state.GripUi.RearLeft, state.GripUi.RearRight };
        for (var index = 0; index < 4; index++)
        {
            var x = xs[index % 2];
            var y = ys[index / 2];
            var capsule = new Rect(x - width * 0.01, y - height * 0.035, width * 0.02, height * 0.07);
            var temperatureFill = TireTemperatureFill(temperatures[index]);
            var gripOutline = TireGripOutline(grips[index]);
            dc.DrawRoundedRectangle(temperatureFill, new Pen(gripOutline, 1.9), capsule, width * 0.01, width * 0.01);
            var textX = index % 2 == 0 ? x - width * 0.018 : x + width * 0.018;
            Text(dc, $"{temperatures[index]:0.#}°C  {grips[index]:0.00}", textX, y, height * 0.024, White,
                index % 2 == 0 ? TextAlignment.Right : TextAlignment.Left, true);
        }
    }

    private static void DrawShiftArrow(DrawingContext dc, Point center, double size, bool up, Brush brush)
    {
        var direction = up ? -1d : 1d;
        var tip = new Point(center.X, center.Y + direction * size * 0.58);
        var shaftEnd = new Point(center.X, center.Y - direction * size * 0.48);
        var wingY = tip.Y - direction * size * 0.42;
        var pen = new Pen(brush, Math.Max(2, size * 0.2))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        dc.DrawLine(pen, shaftEnd, tip);
        dc.DrawLine(pen, tip, new Point(center.X - size * 0.42, wingY));
        dc.DrawLine(pen, tip, new Point(center.X + size * 0.42, wingY));
    }

    private static Brush TireTemperatureFill(double temperature)
    {
        var heat = DashboardDisplayValues.TireHeatIntensityCelsius(temperature);
        return BlendBrush(
            BrushColor(0xEF, 0x5A, 0x64),
            BrushColor(0x7A, 0x06, 0x18),
            heat,
            heat * 0.94);
    }

    private static Brush TireGripOutline(double grip) =>
        BlendBrush(BrushColor(0x32, 0x12, 0x52), BrushColor(0xF3, 0xF4, 0xF5), Math.Clamp(grip, 0, 1), 1);

    private static string NonNegativeWholeNumber(double value) =>
        DashboardDisplayValues.NonNegativeOutput(value).ToString("0");

    private void DrawPedals(DrawingContext dc, Modules.Dashboard.DashboardHudState state, double width, double height)
    {
        var nowSeconds = clock.Elapsed.TotalSeconds;
        var reduceMotion = getLayout().ReduceMotion;
        if (!pedalAnimationInitialized)
        {
            renderedBrake = state.Brake;
            previousPedalRenderSeconds = nowSeconds;
            pedalAnimationInitialized = true;
        }

        var deltaSeconds = Math.Clamp(nowSeconds - previousPedalRenderSeconds, 0, 0.1);
        previousPedalRenderSeconds = nowSeconds;
        renderedBrake = reduceMotion ? state.Brake : SmoothPedal(renderedBrake, state.Brake, deltaSeconds);

        var baseY = height * 0.825;
        DrawPedal(dc, width * 0.45, baseY, state.Brake, renderedBrake, "BRAKE", false);
        DrawPedal(dc, width * 0.55, baseY, state.Throttle, state.Throttle, "THROTTLE", true);

        void DrawPedal(DrawingContext context, double x, double bottom, double rawValue, double displayValue, string label, bool throttle)
        {
            var pedalWidth = width * 0.045;
            var pedalHeight = height * 0.16;
            var bounds = new Rect(x - pedalWidth / 2, bottom - pedalHeight, pedalWidth, pedalHeight);
            var accent = throttle ? BrushOf(0x28, 0xC6, 0x78) : BrushOf(0xC9, 0x36, 0x49);
            var border = BrushWithOpacity(accent, 0.28 + Math.Clamp(displayValue, 0, 1) * 0.5);
            context.DrawRoundedRectangle(BrushOf(0x0A, 0x0D, 0x11, 0.62), new Pen(border, 1.4), bounds,
                pedalWidth * 0.18, pedalWidth * 0.18);

            for (var tick = 1; tick <= 3; tick++)
            {
                var tickY = bounds.Bottom - bounds.Height * tick / 4;
                context.DrawLine(new Pen(BrushOf(0x8A, 0x90, 0x99, 0.18), 1),
                    new Point(bounds.Left + pedalWidth * 0.22, tickY),
                    new Point(bounds.Right - pedalWidth * 0.22, tickY));
            }

            var fillHeight = Math.Clamp(displayValue, 0, 1) * (pedalHeight - 6);
            var fillBounds = new Rect(bounds.Left + 2, bounds.Bottom - 2 - fillHeight, bounds.Width - 4, fillHeight);
            if (fillHeight > 0.5)
            {
                var fill = throttle
                    ? BrushOf(0x28, 0xC6, 0x78)
                    : new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(BrushColor(0x58, 0x12, 0x20), 0),
                        new(BrushColor(0x8B, 0x1E, 0x2D), 0.72),
                        new(BrushColor(0xC9, 0x36, 0x49), 1)
                    }, new Point(0, 1), new Point(0, 0));

                var corner = pedalWidth * 0.12;
                context.PushClip(new RectangleGeometry(fillBounds, corner, corner));
                context.DrawRectangle(fill, null, fillBounds);

                context.DrawLine(new Pen(BrushWithOpacity(accent, 0.85), 1.6),
                    new Point(fillBounds.Left + corner, fillBounds.Top + 0.8),
                    new Point(fillBounds.Right - corner, fillBounds.Top + 0.8));
                context.Pop();
            }

            Text(context, rawValue.ToString("P0"), x, bounds.Top - height * 0.025, height * 0.028, White, TextAlignment.Center, true);
            Text(context, label, x, bounds.Bottom + height * 0.028, height * 0.023, Muted, TextAlignment.Center, true);
        }
    }

    private static void DrawSteering(
        DrawingContext dc,
        Modules.Dashboard.DashboardHudState state,
        double width,
        double height)
    {
        var steering = Math.Clamp(state.Steering, -1, 1);
        var centerX = width * 0.5;
        var track = new Rect(
            width * 0.405,
            height * 0.925,
            width * 0.19,
            Math.Max(9, height * 0.026));
        var radius = track.Height * 0.5;
        var innerLeft = track.Left + radius * 0.78;
        var innerRight = track.Right - radius * 0.78;
        var markerX = centerX + steering * (innerRight - centerX);
        var activity = Math.Abs(steering);

        Text(
            dc,
            "STEERING",
            centerX,
            height * 0.895,
            height * 0.022,
            Muted,
            TextAlignment.Center,
            true);
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x0F, 0.78),
            new Pen(BrushOf(0x62, 0x68, 0x72, 0.62), 1.2),
            track,
            radius,
            radius);

        var responseLeft = Math.Min(centerX, markerX);
        var responseWidth = Math.Max(1, Math.Abs(markerX - centerX));
        dc.DrawRoundedRectangle(
            BrushWithOpacity(Cyan, 0.22 + activity * 0.62),
            null,
            new Rect(
                responseLeft,
                track.Top + track.Height * 0.22,
                responseWidth,
                track.Height * 0.56),
            track.Height * 0.28,
            track.Height * 0.28);
        dc.DrawLine(
            new Pen(BrushOf(0xE1, 0xE5, 0xE9, 0.46), 1),
            new Point(centerX, track.Top + 2),
            new Point(centerX, track.Bottom - 2));

        var markerRadius = track.Height * (0.24 + activity * 0.08);
        dc.DrawEllipse(
            BrushWithOpacity(Cyan, 0.9),
            new Pen(White, 1.2),
            new Point(markerX, track.Top + track.Height * 0.5),
            markerRadius,
            markerRadius);

        var chevronPen = new Pen(
            BrushOf(0xA4, 0xAA, 0xB2, 0.72),
            Math.Max(1.2, height * 0.0022))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var wing = track.Height * 0.25;
        var leftTip = new Point(track.Left - width * 0.012, track.Top + radius);
        dc.DrawLine(
            chevronPen,
            new Point(leftTip.X + wing, leftTip.Y - wing),
            leftTip);
        dc.DrawLine(
            chevronPen,
            leftTip,
            new Point(leftTip.X + wing, leftTip.Y + wing));
        var rightTip = new Point(track.Right + width * 0.012, track.Top + radius);
        dc.DrawLine(
            chevronPen,
            new Point(rightTip.X - wing, rightTip.Y - wing),
            rightTip);
        dc.DrawLine(
            chevronPen,
            rightTip,
            new Point(rightTip.X - wing, rightTip.Y + wing));
    }

    private static void DrawClassBadge(DrawingContext dc, Modules.Dashboard.DashboardHudState state, double width, double height)
    {
        var performanceClass = PerformanceClassCatalog.Resolve(state.CarClass, state.PerformanceIndex);
        var level = PerformanceClassCatalog.Name(performanceClass);
        var color = ClassBrush(level);
        Text(dc, "CLASS / PI", width * 0.79, height * 0.65, height * 0.026, Muted, TextAlignment.Center, true);
        var bounds = new Rect(width * 0.702, height * 0.69, width * 0.176, height * 0.115);
        var radius = height * 0.018;
        var classWidth = bounds.Width * 0.31;

        dc.DrawRoundedRectangle(BrushOf(0x08, 0x0B, 0x0F, 0.78),
            new Pen(BrushOf(0x7C, 0x82, 0x8B, 0.68), 1.2), bounds, radius, radius);
        dc.PushClip(new RectangleGeometry(bounds, radius, radius));
        dc.DrawRectangle(BrushWithOpacity(color, 0.055), null,
            new Rect(bounds.Left, bounds.Top, classWidth, bounds.Height));
        dc.DrawRectangle(BrushWithOpacity(color, 0.9), null,
            new Rect(bounds.Left, bounds.Top, Math.Max(3, width * 0.0025), bounds.Height));
        dc.Pop();

        var dividerX = bounds.Left + classWidth;
        dc.DrawLine(new Pen(BrushOf(0x8A, 0x90, 0x99, 0.42), 1),
            new Point(dividerX, bounds.Top + bounds.Height * 0.2),
            new Point(dividerX, bounds.Bottom - bounds.Height * 0.2));
        Text(dc, level, bounds.Left + classWidth * 0.53, bounds.Top + bounds.Height * 0.52,
            height * 0.064, color, TextAlignment.Center, true);
        Text(dc, state.PerformanceIndex.ToString("000"), dividerX + (bounds.Right - dividerX) * 0.5,
            bounds.Top + bounds.Height * 0.52, height * 0.053, White, TextAlignment.Center, true);
    }

    private void DrawFullDriftDashboard(
        DrawingContext dc,
        DriftHudState state)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var center = new Point(width * 0.5, height * 0.38);
        var safetyColor = DriftSafetyBrush(state.SpinRiskLevel);
        var angleColor = state.SpinRiskLevel == DriftSpinRiskLevel.Safe
            ? state.CanBuildAngle
                ? BrushOf(0x36, 0xD9, 0x8A)
                : Cyan
            : safetyColor;
        DrawEllipticalArc(
            dc,
            center,
            width * 0.425,
            height * 0.34,
            202,
            338,
            BrushOf(0x20, 0x25, 0x2D, 0.54),
            height * 0.022,
            80);
        var angleEnd = 270 + Math.Clamp(
            state.DriftAngleDegrees / 60,
            -1,
            1) * 68;
        DrawEllipticalArc(
            dc,
            center,
            width * 0.425,
            height * 0.34,
            270,
            angleEnd,
            angleColor,
            height * 0.014,
            40);
        DrawDriftScorePace(
            dc,
            new Rect(
                width * 0.31,
                height * 0.115,
                width * 0.38,
                height * 0.034),
            state);
        Text(
            dc,
            "DRIFT ASSIST  ·  EXPERIMENTAL",
            width * 0.5,
            height * 0.055,
            height * 0.026,
            White,
            TextAlignment.Center,
            true);

        var radius = Math.Min(width * 0.125, height * 0.21);
        var leftCenter = new Point(width * 0.37, height * 0.37);
        var rightCenter = new Point(width * 0.63, height * 0.37);
        DrawGaugeCircle(dc, leftCenter, radius, angleColor);
        DrawGaugeCircle(
            dc,
            rightCenter,
            radius,
            safetyColor);
        Text(
            dc,
            SignedDegrees(state.DriftAngleDegrees),
            leftCenter.X,
            leftCenter.Y - radius * 0.08,
            radius * 0.48,
            White,
            TextAlignment.Center,
            true);
        Text(
            dc,
            "侧滑角",
            leftCenter.X,
            leftCenter.Y + radius * 0.48,
            radius * 0.15,
            Muted,
            TextAlignment.Center);
        Text(
            dc,
            state.StabilityScore.ToString("0"),
            rightCenter.X,
            rightCenter.Y - radius * 0.08,
            radius * 0.52,
            White,
            TextAlignment.Center,
            true);
        Text(
            dc,
            "控车余量",
            rightCenter.X,
            rightCenter.Y + radius * 0.48,
            radius * 0.16,
            Muted,
            TextAlignment.Center);

        DrawDriftSteeringRecommendation(
            dc,
            new Rect(
                width * 0.20,
                height * 0.625,
                width * 0.31,
                height * 0.095),
            state.SteeringCue,
            state.SteeringCueStrength,
            state.Steering,
            safetyColor);
        Text(
            dc,
            state.SpeedKph.ToString("000"),
            width * 0.565,
            height * 0.655,
            height * 0.052,
            White,
            TextAlignment.Center,
            true);
        Text(
            dc,
            "km/h",
            width * 0.565,
            height * 0.702,
            height * 0.016,
            Muted,
            TextAlignment.Center);
        DrawDriftGearRecommendation(
            dc,
            new Rect(
                width * 0.63,
                height * 0.625,
                width * 0.17,
                height * 0.095),
            state.GearDisplay,
            state.GearCue,
            safetyColor);
        DrawDriftLevelBar(
            dc,
            new Rect(
                width * 0.23,
                height * 0.785,
                width * 0.25,
                height * 0.032),
            state.Throttle,
            "油门",
            BrushOf(0x28, 0xC6, 0x78));
        DrawDriftLevelBar(
            dc,
            new Rect(
                width * 0.52,
                height * 0.785,
                width * 0.25,
                height * 0.032),
            Math.Clamp(state.RearSlip / 1.35, 0, 1),
            "后轮滑移",
            BrushOf(0xF2, 0xB8, 0x27));
        DrawSpinRiskBand(
            dc,
            new Rect(
                width * 0.20,
                height * 0.885,
                width * 0.60,
                height * 0.070),
            state);
    }

    private static void DrawDriftSteeringRecommendation(
        DrawingContext dc,
        Rect bounds,
        DriftSteeringCue cue,
        double cueStrength,
        double steering,
        Brush safetyColor)
    {
        dc.DrawRoundedRectangle(
            BrushOf(0x0A, 0x0D, 0x11, 0.7),
            new Pen(BrushWithOpacity(safetyColor, 0.62), 1.2),
            bounds,
            bounds.Height * 0.22,
            bounds.Height * 0.22);
        var center = new Point(
            bounds.Left + bounds.Width * 0.54,
            bounds.Top + bounds.Height * 0.57);
        var trackLeft = bounds.Left + bounds.Width * 0.22;
        var trackRight = bounds.Right - bounds.Width * 0.08;
        dc.DrawLine(
            new Pen(BrushOf(0x58, 0x60, 0x69, 0.72), 1.2),
            new Point(trackLeft, center.Y),
            new Point(trackRight, center.Y));
        dc.DrawLine(
            new Pen(BrushOf(0xF3, 0xF4, 0xF5, 0.34), 1),
            new Point(center.X, center.Y - bounds.Height * 0.21),
            new Point(center.X, center.Y + bounds.Height * 0.21));

        var steeringX = center.X +
                        Math.Clamp(steering, -1, 1) * bounds.Width * 0.30;
        dc.DrawEllipse(
            Cyan,
            new Pen(BrushOf(0xF3, 0xF4, 0xF5, 0.82), 1),
            new Point(steeringX, center.Y),
            bounds.Height * 0.065,
            bounds.Height * 0.065);

        var cueBrush = BrushWithOpacity(
            safetyColor,
            0.62 + Math.Clamp(cueStrength, 0, 1) * 0.38);
        if (cue == DriftSteeringCue.Hold)
        {
            var holdPen = new Pen(cueBrush, Math.Max(2, bounds.Height * 0.075))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawLine(
                holdPen,
                new Point(center.X - bounds.Width * 0.075, center.Y),
                new Point(center.X + bounds.Width * 0.075, center.Y));
        }
        else
        {
            DrawHorizontalCueArrow(
                dc,
                center,
                bounds.Width * (0.13 + cueStrength * 0.035),
                cue == DriftSteeringCue.Right,
                cueBrush);
        }
        Text(
            dc,
            "方向",
            bounds.Left + bounds.Width * 0.07,
            bounds.Top + bounds.Height * 0.25,
            bounds.Height * 0.22,
            Muted,
            TextAlignment.Left,
            true);
    }

    private static void DrawDriftGearRecommendation(
        DrawingContext dc,
        Rect bounds,
        string gearDisplay,
        DriftGearCue cue,
        Brush safetyColor)
    {
        dc.DrawRoundedRectangle(
            BrushOf(0x0A, 0x0D, 0x11, 0.7),
            new Pen(BrushWithOpacity(safetyColor, 0.62), 1.2),
            bounds,
            bounds.Height * 0.22,
            bounds.Height * 0.22);
        var gearCenter = new Point(
            bounds.Left + bounds.Width * 0.40,
            bounds.Top + bounds.Height * 0.52);
        dc.DrawEllipse(
            BrushOf(0x08, 0x0B, 0x0F, 0.82),
            new Pen(BrushWithOpacity(safetyColor, 0.80), 1.4),
            gearCenter,
            bounds.Height * 0.31,
            bounds.Height * 0.31);
        Text(
            dc,
            gearDisplay,
            gearCenter.X,
            gearCenter.Y - bounds.Height * 0.015,
            bounds.Height * 0.35,
            White,
            TextAlignment.Center,
            true);
        var cueCenter = new Point(
            bounds.Left + bounds.Width * 0.76,
            bounds.Top + bounds.Height * 0.52);
        if (cue == DriftGearCue.ShiftUp)
            DrawShiftArrow(dc, cueCenter, bounds.Height * 0.30, true, safetyColor);
        else if (cue == DriftGearCue.ShiftDown)
            DrawShiftArrow(dc, cueCenter, bounds.Height * 0.30, false, safetyColor);
        else
        {
            var holdPen = new Pen(
                BrushWithOpacity(safetyColor, 0.72),
                Math.Max(2, bounds.Height * 0.06))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawLine(
                holdPen,
                new Point(cueCenter.X - bounds.Width * 0.07, cueCenter.Y),
                new Point(cueCenter.X + bounds.Width * 0.07, cueCenter.Y));
        }
    }

    private static void DrawHorizontalCueArrow(
        DrawingContext dc,
        Point center,
        double size,
        bool right,
        Brush brush)
    {
        var direction = right ? 1d : -1d;
        var tip = new Point(center.X + direction * size * 0.58, center.Y);
        var shaftEnd = new Point(center.X - direction * size * 0.48, center.Y);
        var wingX = tip.X - direction * size * 0.42;
        var pen = new Pen(brush, Math.Max(2, size * 0.16))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        dc.DrawLine(pen, shaftEnd, tip);
        dc.DrawLine(pen, tip, new Point(wingX, center.Y - size * 0.34));
        dc.DrawLine(pen, tip, new Point(wingX, center.Y + size * 0.34));
    }

    private static void DrawDriftLevelBar(
        DrawingContext dc,
        Rect bounds,
        double value,
        string label,
        Brush accent)
    {
        value = Math.Clamp(value, 0, 1);
        dc.DrawRoundedRectangle(
            BrushOf(0x0A, 0x0D, 0x11, 0.7),
            new Pen(BrushOf(0x58, 0x60, 0x69, 0.65), 1),
            bounds,
            bounds.Height / 2,
            bounds.Height / 2);
        if (value > 0.002)
        {
            dc.DrawRoundedRectangle(
                BrushWithOpacity(accent, 0.88),
                null,
                new Rect(
                    bounds.Left + 2,
                    bounds.Top + 2,
                    Math.Max(1, (bounds.Width - 4) * value),
                    Math.Max(1, bounds.Height - 4)),
                bounds.Height / 2,
                bounds.Height / 2);
        }
        Text(
            dc,
            $"{label}  {value:P0}",
            bounds.Left,
            bounds.Top - bounds.Height * 0.65,
            bounds.Height * 0.72,
            Muted,
            TextAlignment.Left,
            true);
    }

    private static void DrawDriftScorePace(
        DrawingContext dc,
        Rect bounds,
        DriftHudState state)
    {
        const int segmentCount = 7;
        Text(
            dc,
            "积分速度",
            bounds.Left,
            bounds.Top + bounds.Height * 0.50,
            bounds.Height * 0.55,
            Muted,
            TextAlignment.Left,
            true);
        var meterLeft = bounds.Left + bounds.Width * 0.28;
        var meterWidth = bounds.Width * 0.72;
        var gap = bounds.Height * 0.20;
        var segmentWidth = (meterWidth - gap * (segmentCount - 1)) / segmentCount;
        var filledSegments = state.IsDrifting
            ? Math.Max(1, (int)Math.Ceiling(
                Math.Clamp(state.AngleScorePotential, 0, 1) * segmentCount))
            : 0;
        var activeBrush = state.SpinRiskLevel switch
        {
            DriftSpinRiskLevel.Critical => BrushOf(0xEF, 0x5A, 0x64),
            DriftSpinRiskLevel.Caution => BrushOf(0xF2, 0xB8, 0x27),
            _ => state.CanBuildAngle ? BrushOf(0x36, 0xD9, 0x8A) : Cyan
        };
        for (var index = 0; index < segmentCount; index++)
        {
            var segment = new Rect(
                meterLeft + index * (segmentWidth + gap),
                bounds.Top + bounds.Height * 0.20,
                segmentWidth,
                bounds.Height * 0.60);
            var brush = index < filledSegments
                ? BrushWithOpacity(activeBrush, 0.90)
                : BrushOf(0x4B, 0x50, 0x58, 0.46);
            dc.DrawRoundedRectangle(
                brush,
                null,
                segment,
                segment.Height * 0.24,
                segment.Height * 0.24);
        }
    }

    private static void DrawSpinRiskBand(
        DrawingContext dc,
        Rect bounds,
        DriftHudState state)
    {
        var safetyColor = DriftSafetyBrush(state.SpinRiskLevel);
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x0F, 0.78),
            new Pen(BrushWithOpacity(safetyColor, 0.78), 1.25),
            bounds,
            bounds.Height * 0.24,
            bounds.Height * 0.24);
        Text(
            dc,
            "SPIN",
            bounds.Left + bounds.Width * 0.095,
            bounds.Top + bounds.Height * 0.49,
            bounds.Height * 0.31,
            Muted,
            TextAlignment.Center,
            true);

        const int segmentCount = 10;
        var meterLeft = bounds.Left + bounds.Width * 0.18;
        var meterWidth = bounds.Width * 0.65;
        var gap = bounds.Height * 0.10;
        var segmentWidth = (meterWidth - gap * (segmentCount - 1)) / segmentCount;
        var currentSegment = Math.Clamp(
            (int)Math.Floor(state.SpinRisk * segmentCount),
            0,
            segmentCount - 1);
        for (var index = 0; index < segmentCount; index++)
        {
            var zoneBrush = index < 4
                ? BrushOf(0x36, 0xD9, 0x8A)
                : index < 7
                    ? BrushOf(0xF2, 0xB8, 0x27)
                    : BrushOf(0xEF, 0x5A, 0x64);
            var segment = new Rect(
                meterLeft + index * (segmentWidth + gap),
                bounds.Top + bounds.Height * 0.31,
                segmentWidth,
                bounds.Height * 0.38);
            dc.DrawRoundedRectangle(
                BrushWithOpacity(
                    zoneBrush,
                    index <= currentSegment ? 0.88 : 0.18),
                index == currentSegment
                    ? new Pen(White, 1)
                    : null,
                segment,
                segment.Height * 0.24,
                segment.Height * 0.24);
        }

        var symbolCenter = new Point(
            bounds.Left + bounds.Width * 0.91,
            bounds.Top + bounds.Height * 0.50);
        var symbolPen = new Pen(
            safetyColor,
            Math.Max(2, bounds.Height * 0.065))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (state.SpinRiskLevel == DriftSpinRiskLevel.Safe)
        {
            dc.DrawLine(
                symbolPen,
                new Point(symbolCenter.X - bounds.Height * 0.12, symbolCenter.Y),
                new Point(symbolCenter.X - bounds.Height * 0.025, symbolCenter.Y + bounds.Height * 0.10));
            dc.DrawLine(
                symbolPen,
                new Point(symbolCenter.X - bounds.Height * 0.025, symbolCenter.Y + bounds.Height * 0.10),
                new Point(symbolCenter.X + bounds.Height * 0.16, symbolCenter.Y - bounds.Height * 0.13));
        }
        else
        {
            dc.DrawLine(
                symbolPen,
                new Point(symbolCenter.X, symbolCenter.Y - bounds.Height * 0.16),
                new Point(symbolCenter.X, symbolCenter.Y + bounds.Height * 0.06));
            dc.DrawEllipse(
                safetyColor,
                null,
                new Point(symbolCenter.X, symbolCenter.Y + bounds.Height * 0.19),
                bounds.Height * 0.035,
                bounds.Height * 0.035);
        }
    }

    private static Brush DriftSafetyBrush(DriftSpinRiskLevel level) =>
        level switch
        {
            DriftSpinRiskLevel.Safe => BrushOf(0x36, 0xD9, 0x8A),
            DriftSpinRiskLevel.Caution => BrushOf(0xF2, 0xB8, 0x27),
            _ => BrushOf(0xEF, 0x5A, 0x64)
        };

    private static string SignedDegrees(double value) =>
        $"{value:+0;-0;0}°";

    private void DrawLapArc(DrawingContext dc, Modules.LapAnalysis.LapHudState state)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var center = new Point(width * 0.5, height * 0.43);
        var startAngle = 202d;
        var totalAngle = 136d;
        var totalLength = Math.Max(1, state.Sectors.Count);
        var cursor = startAngle;
        for (var index = 0; index < state.Sectors.Count; index++)
        {
            var segment = state.Sectors[index];
            var portion = 1d / totalLength;
            var span = totalAngle * portion;
            var color = segment.State switch
            {
                SectorColorState.Yellow => BrushOf(0xF2, 0xB8, 0x27),
                SectorColorState.Green => BrushOf(0x20, 0xB8, 0x68),
                SectorColorState.Purple => BrushOf(0xB4, 0x3B, 0xDD),
                _ => BrushOf(0x5A, 0x5F, 0x68)
            };
            var radiusX = width * 0.447;
            var radiusY = height * 0.358;
            DrawEllipticalArc(dc, center, radiusX, radiusY, cursor + 1, cursor + span - 1,
                color, segment.IsCurrent ? height * 0.014 : height * 0.009, 8);
            cursor += span;
        }

        var statusY = height * 0.055;
        var approximatePrefix = state.IsPointToPoint ? "≈ " : string.Empty;
        var title = $"{state.TrackName}  ·  {approximatePrefix}{FormatLapTime(state.CurrentLapSeconds)}";
        Text(dc, title, width * 0.5 + 1.2, statusY + 1.2,
            height * 0.025, BrushOf(0x00, 0x00, 0x00, 0.82), TextAlignment.Center, true);
        Text(dc, title, width * 0.5, statusY,
            height * 0.025, BrushOf(0xF8, 0xFA, 0xFC, 0.98), TextAlignment.Center, true);
    }

    private void DrawCumulativeLapDelta(DrawingContext dc, Modules.LapAnalysis.LapHudState state)
    {
        if (state.CumulativeHistoricalDeltaSeconds is not double delta || !double.IsFinite(delta)) return;
        var faster = delta < -0.0005;
        var slower = delta > 0.0005;
        var color = faster
            ? BrushOf(0x54, 0xE0, 0x91)
            : slower
                ? BrushOf(0xF5, 0xC8, 0x4B)
                : BrushOf(0xF8, 0xFA, 0xFC);
        var sign = slower ? "+" : faster ? "−" : "±";
        var value = $"{sign}{Math.Abs(delta):0.000} s";
        Text(dc, value, ActualWidth * 0.5 + 1, ActualHeight * 0.18 + 1,
            ActualHeight * 0.034, BrushOf(0x00, 0x00, 0x00, 0.8), TextAlignment.Center, true);
        Text(dc, value, ActualWidth * 0.5, ActualHeight * 0.18,
            ActualHeight * 0.034, color, TextAlignment.Center, true);
    }

    private static void DrawEllipticalArc(DrawingContext dc, Point center, double radiusX, double radiusY, double startDegrees, double endDegrees, Brush brush, double thickness, int steps)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        Point? previous = null;
        for (var step = 0; step <= steps; step++)
        {
            var degrees = startDegrees + (endDegrees - startDegrees) * step / steps;
            var radians = degrees * Math.PI / 180;
            var point = new Point(center.X + radiusX * Math.Cos(radians), center.Y + radiusY * Math.Sin(radians));
            if (previous is Point old) dc.DrawLine(pen, old, point);
            previous = point;
        }
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, TextAlignment alignment, bool strong = false)
    {
        if (size <= 0) return;
        var typeface = ContainsChinese(text)
            ? strong ? ChineseNormalTypeface : ChineseLightTypeface
            : strong ? NormalTypeface : LightTypeface;
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush, 1) { TextAlignment = alignment };
        dc.DrawText(formatted, new Point(x, y - formatted.Height / 2));
    }

    private static void BoundedText(
        DrawingContext dc,
        string text,
        Rect bounds,
        double size,
        Brush brush,
        bool strong = false)
    {
        if (size <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var typeface = ContainsChinese(text)
            ? strong ? ChineseNormalTypeface : ChineseLightTypeface
            : strong ? NormalTypeface : LightTypeface;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            1)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = bounds.Width,
            MaxTextHeight = bounds.Height,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(
            formatted,
            new Point(
                bounds.Left,
                bounds.Top + Math.Max(0, (bounds.Height - formatted.Height) / 2)));
    }

    private static bool ContainsChinese(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9FFF');

    private static string FormatLapTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "0:00.000";
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }
    private static Brush ClassBrush(string value) => value switch
    {
        "D" => BrushOf(0x62, 0xB8, 0xE8), "C" => BrushOf(0xF2, 0xB8, 0x27), "B" => BrushOf(0xED, 0x7A, 0x1A),
        "A" => BrushOf(0xE3, 0x31, 0x4F), "S1" => BrushOf(0xB4, 0x3B, 0xDD), "S2" => BrushOf(0x24, 0x72, 0xD4),
        "R" => BrushOf(0xE6, 0x2A, 0x83), "X" => BrushOf(0x00, 0xB8, 0x5A), _ => Muted
    };

    private static Brush BrushWithOpacity(Brush source, double opacity)
    {
        if (source is not SolidColorBrush solid) return source;
        return new SolidColorBrush(solid.Color) { Opacity = opacity };
    }
    private static double SmoothPedal(double current, double target, double deltaSeconds)
    {
        target = Math.Clamp(target, 0, 1);
        var responseSeconds = target > current ? 0.055 : 0.105;
        var amount = 1 - Math.Exp(-deltaSeconds / responseSeconds);
        return current + (target - current) * amount;
    }
    private static Brush InterpolateBrush(double value, Brush low, Brush high)
    {
        value = Math.Clamp((value - 0.65) / 0.35, 0, 1);
        var a = ((SolidColorBrush)low).Color;
        var b = ((SolidColorBrush)high).Color;
        return new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * value), (byte)(a.G + (b.G - a.G) * value), (byte)(a.B + (b.B - a.B) * value)));
    }
    private static Brush BlendBrush(Color low, Color high, double amount, double opacity)
    {
        amount = Math.Clamp(amount, 0, 1);
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Round(low.R + (high.R - low.R) * amount),
            (byte)Math.Round(low.G + (high.G - low.G) * amount),
            (byte)Math.Round(low.B + (high.B - low.B) * amount)))
        {
            Opacity = Math.Clamp(opacity, 0, 1)
        };
        brush.Freeze();
        return brush;
    }
    private static Color BrushColor(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Brush BrushOf(byte r, byte g, byte b, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}

public static class OverlayLayoutPreviewState
{
    private static readonly IReadOnlyList<SectorComparison> PreviewSectors =
    [
        new(0, 18.420, 18.680, 18.510, -0.090, SectorColorState.Green, false),
        new(1, 21.735, 21.910, 21.650, 0.085, SectorColorState.Yellow, true),
        new(2, 19.220, 19.540, 19.220, 0, SectorColorState.Purple, false),
        new(3, null, 20.130, 19.980, null, SectorColorState.Gray, false)
    ];

    public static DashboardHudState Dashboard(
        DashboardHudState? state,
        DateTimeOffset now) =>
        state is null
            ? new DashboardHudState(
                now,
                TelemetrySourceKind.Live,
                "LIVE",
                false,
                true,
                4,
                4,
                "4",
                false,
                142,
                6_280,
                8_200,
                268,
                421,
                new WheelValues(83, 84, 87, 88),
                new WheelValues(0.96f, 0.97f, 0.93f, 0.94f),
                0.24,
                0.72,
                2,
                900,
                new ShiftLearningSnapshot(
                    LearningState.Collecting,
                    0.72,
                    0.68,
                    null,
                    [],
                    [],
                    [],
                    new Dictionary<string, int>(),
                    "布局预览"))
            {
                SpeedMps = 142 / 3.6,
                Steering = 0.16
            }
            : state with
            {
                UpdatedAt = now,
                IsStale = false,
                IsDriving = true
            };

    public static DriftHudState Drift(
        DriftHudState? state,
        DateTimeOffset now)
    {
        var preview = new DriftHudState(
            now,
            state?.Source ?? TelemetrySourceKind.Live,
            state?.SourceLabel ?? "LIVE",
            false,
            true,
            DriftPracticePhase.Stable,
            "稳定漂移",
            DriftSpinRiskLevel.Safe,
            0.12,
            DriftSteeringCue.Left,
            0.72,
            DriftGearCue.ShiftUp,
            0.58,
            true,
            86,
            3,
            "3",
            31,
            42,
            -0.34,
            0.58,
            0,
            0,
            0,
            0.28,
            0.52,
            0.46,
            true,
            6.8,
            4.2,
            8.6,
            82,
            88,
            76);
        return preview;
    }

    public static Modules.LapAnalysis.LapHudState Lap(
        Modules.LapAnalysis.LapHudState? state,
        DateTimeOffset now)
    {
        if (state is null)
        {
            return new Modules.LapAnalysis.LapHudState(
                now,
                TelemetrySourceKind.Live,
                true,
                Modules.LapAnalysis.TrackLearningPhase.ComparingLaps,
                "布局预览",
                string.Empty,
                Modules.LapAnalysis.TrackMatchState.Confirmed,
                0.96,
                "圈速 HUD 预览",
                1,
                PreviewSectors,
                0.58,
                2,
                true)
            {
                CurrentLapSeconds = 79.375,
                CumulativeHistoricalDeltaSeconds = -0.184
            };
        }

        return state with
        {
            UpdatedAt = now,
            IsCompetitionActive = true,
            TrackName = string.IsNullOrWhiteSpace(state.TrackName)
                ? "圈速 HUD 预览"
                : state.TrackName,
            Sectors = state.Sectors.Count == 0 ? PreviewSectors : state.Sectors,
            CurrentLapSeconds = double.IsFinite(state.CurrentLapSeconds) && state.CurrentLapSeconds > 0
                ? state.CurrentLapSeconds
                : 79.375,
            CumulativeHistoricalDeltaSeconds =
                state.CumulativeHistoricalDeltaSeconds is double delta && double.IsFinite(delta)
                    ? delta
                    : -0.184
        };
    }
}
