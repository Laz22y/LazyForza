using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;

namespace LazyForza.Overlay;

internal sealed class TelemetryOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private readonly HudSurface surface;
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
        surface = new HudSurface(getContributions, () => layout);
        Content = surface;
        ApplyLayout(initialLayout);
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    public void ApplyLayout(OverlayLayout newLayout)
    {
        layout = newLayout with
        {
            Scale = OverlayScaleSettings.Normalize(newLayout.Scale),
            DashboardMotionIntensity = Math.Clamp(newLayout.DashboardMotionIntensity, 0, 1),
            DashboardIdleWaitSeconds = Math.Clamp(newLayout.DashboardIdleWaitSeconds, 0, 60),
            DashboardVisibilityFadeSeconds = Math.Clamp(newLayout.DashboardVisibilityFadeSeconds, 0.05, 10),
            LapCompletedHoldSeconds = Math.Clamp(newLayout.LapCompletedHoldSeconds, 0, 15),
            LapNoMatchConfirmationSeconds = Math.Clamp(newLayout.LapNoMatchConfirmationSeconds, 0.1, 60),
            LapNoMatchFadeSeconds = Math.Clamp(newLayout.LapNoMatchFadeSeconds, 0.05, 10),
            LiveHudStaleSeconds = Math.Clamp(newLayout.LiveHudStaleSeconds, 0.05, 10)
        };
        Left = layout.Left;
        Top = layout.Top;
        Width = OverlayScaleSettings.ScaledDimension(layout.Width, layout.Scale);
        Height = OverlayScaleSettings.ScaledDimension(layout.Height, layout.Scale);
        Opacity = Math.Clamp(layout.Opacity, 0.25, 1);
        UpdateNativeStyles();
        surface.InvalidateVisual();
    }

    public OverlayLayout CaptureLayout() => layout with
    {
        Left = Left,
        Top = Top,
        Width = Width / OverlayScaleSettings.Normalize(layout.Scale),
        Height = Height / OverlayScaleSettings.Normalize(layout.Scale)
    };

    public void InvalidateHud() => surface.InvalidateVisual();

    public void CapturePng(string path, double targetWidth, double targetHeight)
    {
        var pixelWidth = Math.Max(1, (int)Math.Round(targetWidth));
        var pixelHeight = Math.Max(1, (int)Math.Round(targetHeight));
        surface.Measure(new Size(targetWidth, targetHeight));
        surface.Arrange(new Rect(0, 0, targetWidth, targetHeight));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        source = HwndSource.FromHwnd(helper.Handle);
        source.AddHook(WindowProcedure);
        UpdateNativeStyles();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!layout.IsLocked && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (message == WmNcHitTest && layout.ClickThrough)
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
        var updated = OverlayNativeStyles.Apply(current, layout.ClickThrough || layout.IsLocked);
        _ = SetWindowLongPtr(source.Handle, GwlExStyle, new IntPtr(updated));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newLong);
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
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly FrameRateLimiter limiter = new();
    private readonly DashboardHudDynamics dashboardDynamics = new();
    private readonly LapHudDynamics lapDynamics = new();
    private double renderedBrake;
    private double previousPedalRenderSeconds;
    private bool pedalAnimationInitialized;

    public HudSurface(Func<IReadOnlyList<IHudContribution>> getContributions, Func<OverlayLayout> getLayout)
    {
        this.getContributions = getContributions;
        this.getLayout = getLayout;
        IsHitTestVisible = true;
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (limiter.ShouldRender(clock.Elapsed.TotalSeconds)) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var contributions = getContributions();
        var dashboard = contributions.Select(item => item.Snapshot).OfType<Modules.Dashboard.DashboardHudState>().LastOrDefault();
        var lap = contributions.Select(item => item.Snapshot).OfType<Modules.LapAnalysis.LapHudState>().LastOrDefault();
        var now = DateTimeOffset.UtcNow;
        var layout = getLayout();
        var dashboardVisible = OverlayVisibilityPolicy.ShouldShowDashboard(dashboard, now, layout.LiveHudStaleSeconds);
        var lapVisible = OverlayVisibilityPolicy.ShouldShowLap(lap, now, layout.LiveHudStaleSeconds);
        var nowSeconds = clock.Elapsed.TotalSeconds;
        var visual = dashboardDynamics.Update(dashboard, dashboardVisible, layout, nowSeconds);
        var lapVisual = lapDynamics.Update(lap, lapVisible, layout, nowSeconds);
        var motion = new Vector(
            visual.HorizontalOffset * ActualWidth * 0.018,
            visual.VerticalOffset * ActualHeight * 0.024);
        if (dashboardVisible && visual.Opacity > 0.001)
        {
            drawingContext.PushTransform(new TranslateTransform(motion.X, motion.Y));
            drawingContext.PushOpacity(visual.Opacity);
            DrawDashboard(drawingContext, dashboard!);
            if (lapVisual.Opacity > 0.001 && lap?.CumulativeHistoricalDeltaSeconds is not null)
            {
                drawingContext.PushOpacity(lapVisual.Opacity);
                DrawCumulativeLapDelta(drawingContext, lap);
                drawingContext.Pop();
            }
            drawingContext.Pop();
            drawingContext.Pop();
        }

        if (lapVisible && lapVisual.Opacity > 0.001)
        {
            if (dashboardVisible) drawingContext.PushTransform(new TranslateTransform(motion.X, motion.Y));
            drawingContext.PushOpacity(lapVisual.Opacity);
            DrawLapArc(drawingContext, lap!, dashboardVisible);
            drawingContext.Pop();
            if (dashboardVisible) drawingContext.Pop();
        }
    }

    private void DrawDashboard(DrawingContext dc, Modules.Dashboard.DashboardHudState state)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var center = new Point(width * 0.5, height * 0.43);
        const double rpmRadiusXFactor = 0.425;
        const double rpmRadiusYFactor = 0.34;
        DrawEllipticalArc(dc, center, width * rpmRadiusXFactor, height * rpmRadiusYFactor, 202, 338,
            BrushOf(0x20, 0x25, 0x2D, 0.45), height * 0.022, 80);
        DrawRpmSegments(dc, state, center, width * rpmRadiusXFactor, height * rpmRadiusYFactor);

        var circleRadius = Math.Min(width * 0.125, height * 0.21);
        var leftCenter = new Point(width * 0.37, height * 0.41);
        var rightCenter = new Point(width * 0.63, height * 0.41);
        DrawGaugeCircle(dc, leftCenter, circleRadius, BrushOf(0x8A, 0x8E, 0x94));
        var rpmBrush = InterpolateBrush(state.MaxRpm <= 0 ? 0 : state.Rpm / state.MaxRpm, BrushOf(0x80, 0x84, 0x8A), BrushOf(0xF2, 0x18, 0x27));
        DrawGaugeCircle(dc, rightCenter, circleRadius, rpmBrush);

        Text(dc, state.GearDisplay, leftCenter.X, leftCenter.Y - circleRadius * 0.47, circleRadius * 0.64, White, TextAlignment.Center, true);
        var gearCueY = leftCenter.Y - circleRadius * 0.47;
        if (state.UpshiftCueActive)
            DrawShiftArrow(dc, new Point(leftCenter.X + circleRadius * 0.48, gearCueY), circleRadius * 0.16, true,
                BrushOf(0x82, 0xE6, 0xAE));
        if (state.DownshiftCueActive)
            DrawShiftArrow(dc, new Point(leftCenter.X - circleRadius * 0.48, gearCueY), circleRadius * 0.16, false,
                BrushOf(0xFF, 0x91, 0x9D));
        var dividerY = leftCenter.Y - circleRadius * 0.02;
        dc.DrawLine(new Pen(BrushOf(0x62, 0x68, 0x72, 0.72), 1), new Point(leftCenter.X - circleRadius * 0.58, dividerY), new Point(leftCenter.X + circleRadius * 0.58, dividerY));
        Text(dc, state.SpeedKph.ToString("000"), leftCenter.X, leftCenter.Y + circleRadius * 0.23, circleRadius * 0.41, White, TextAlignment.Center, true);
        Text(dc, "km/h", leftCenter.X, leftCenter.Y + circleRadius * 0.59, circleRadius * 0.16, Muted, TextAlignment.Center);

        Text(dc, $"{state.Rpm:0}", rightCenter.X - circleRadius * 0.08, rightCenter.Y - circleRadius * 0.48, circleRadius * 0.28, White, TextAlignment.Center, true);
        Text(dc, "RPM", rightCenter.X + circleRadius * 0.5, rightCenter.Y - circleRadius * 0.41, circleRadius * 0.12, Muted, TextAlignment.Center);
        dc.DrawLine(new Pen(BrushOf(0x42, 0x46, 0x4D), 1), new Point(rightCenter.X - circleRadius * 0.58, rightCenter.Y - circleRadius * 0.12), new Point(rightCenter.X + circleRadius * 0.58, rightCenter.Y - circleRadius * 0.12));
        Text(dc, $"{NonNegativeWholeNumber(state.PowerKw)} kW", rightCenter.X, rightCenter.Y + circleRadius * 0.08, circleRadius * 0.22, White, TextAlignment.Center, true);
        Text(dc, $"{NonNegativeWholeNumber(state.TorqueNm)} N·m", rightCenter.X, rightCenter.Y + circleRadius * 0.42, circleRadius * 0.2, White, TextAlignment.Center, true);

        DrawTires(dc, state, width, height);
        DrawPedals(dc, state, width, height);
        DrawClassBadge(dc, state, width, height);

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

    private void DrawLapArc(DrawingContext dc, Modules.LapAnalysis.LapHudState state, bool dashboardVisible)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var center = new Point(width * 0.5, dashboardVisible ? height * 0.43 : height * 0.58);
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
            var radiusX = dashboardVisible ? width * 0.447 : width * 0.42;
            var radiusY = dashboardVisible ? height * 0.358 : height * 0.24;
            DrawEllipticalArc(dc, center, radiusX, radiusY, cursor + 1, cursor + span - 1,
                color, segment.IsCurrent ? height * 0.014 : height * 0.009, 8);
            cursor += span;
        }

        var statusY = dashboardVisible ? height * 0.055 : height * 0.38;
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
