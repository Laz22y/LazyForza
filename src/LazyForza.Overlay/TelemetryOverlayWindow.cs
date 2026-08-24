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
using LazyForza.Modules.EstateRace;

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
    private readonly HudSurface estateRaceSurface;
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
        estateRaceSurface = new HudSurface(
            getContributions,
            () => layout,
            HudSurfaceKind.EstateRace);
        surfaceHost.Children.Add(dashboardSurface);
        surfaceHost.Children.Add(lapSurface);
        surfaceHost.Children.Add(driftSurface);
        surfaceHost.Children.Add(estateRaceSurface);
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
        var estateRace = OverlayLayoutGeometry.Bounds(layout, OverlayHudKind.EstateRace);
        Left = union.Left;
        Top = union.Top;
        Width = union.Width;
        Height = union.Height;
        PositionSurface(dashboardSurface, dashboard, union);
        PositionSurface(lapSurface, lap, union);
        PositionSurface(driftSurface, drift, union);
        PositionSurface(estateRaceSurface, estateRace, union);
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
        estateRaceSurface.InvalidateVisual();
    }

    public void CapturePng(
        string path,
        double targetWidth,
        double targetHeight,
        bool previewDrift = false,
        bool previewEstateRace = false,
        bool previewEstateRaceFinished = false,
        bool previewEstateRaceChequered = false)
    {
        var previousDriftPreview = driftSurface.LayoutPreview;
        var previousEstateRacePreview = estateRaceSurface.LayoutPreview;
        var previousEstateRaceFinishedPreview = estateRaceSurface.EstateRaceFinishedPreview;
        var previousEstateRaceChequeredPreview = estateRaceSurface.EstateRaceChequeredPreview;
        driftSurface.LayoutPreview = previewDrift;
        estateRaceSurface.LayoutPreview = previewEstateRace;
        estateRaceSurface.EstateRaceFinishedPreview = previewEstateRaceFinished;
        estateRaceSurface.EstateRaceChequeredPreview = previewEstateRaceChequered;
        driftSurface.InvalidateVisual();
        estateRaceSurface.InvalidateVisual();
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
            estateRaceSurface.LayoutPreview = previousEstateRacePreview;
            estateRaceSurface.EstateRaceFinishedPreview = previousEstateRaceFinishedPreview;
            estateRaceSurface.EstateRaceChequeredPreview = previousEstateRaceChequeredPreview;
            driftSurface.InvalidateVisual();
            estateRaceSurface.InvalidateVisual();
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
    Drift,
    EstateRace
}

internal enum RaceHeaderSignal
{
    None,
    Yellow,
    DoubleYellow,
    Red,
    Blue,
    Chequered,
    HighLatency,
    NetworkUnstable,
    Reconnecting
}

internal enum QualifyingEliminationVisualState
{
    None,
    AtRisk,
    Eliminated
}

internal sealed class HudSurface : FrameworkElement
{
    private static readonly Typeface NormalTypeface = new(new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
    private static readonly Typeface LightTypeface = new(new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.Normal, FontStretches.Condensed);
    private static readonly Typeface ChineseNormalTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ChineseLightTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface RaceNormalTypeface = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface RaceLightTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface RaceTitleTypeface = new(new FontFamily("Bahnschrift, Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface RaceStrategyTitleTypeface = new(new FontFamily("Bahnschrift, Segoe UI Variable Display, Segoe UI"), FontStyles.Italic, FontWeights.Bold, FontStretches.Expanded);
    private static readonly Brush RaceChequeredHeaderBackground = FrozenLinearGradient(
        [
            new GradientStop(BrushColor(0x1B, 0x20, 0x28), 0),
            new GradientStop(BrushColor(0x08, 0x0B, 0x10), 0.54),
            new GradientStop(BrushColor(0x18, 0x1D, 0x24), 1)
        ],
        new Point(0, 0),
        new Point(1, 0.82));
    private static readonly Brush RaceChequeredStageBackground = FrozenLinearGradient(
        [
            new GradientStop(BrushColor(0x18, 0x1D, 0x24), 0),
            new GradientStop(BrushColor(0x08, 0x0B, 0x10), 0.54),
            new GradientStop(BrushColor(0x16, 0x1B, 0x22), 1)
        ],
        new Point(0, 0),
        new Point(1, 0.42));
    private static readonly Brush RaceChequeredGoldAccent = FrozenLinearGradient(
        [
            new GradientStop(BrushColor(0xFF, 0xDB, 0x7D), 0),
            new GradientStop(BrushColor(0xE3, 0xAD, 0x43), 0.55),
            new GradientStop(BrushColor(0xFF, 0xD8, 0x70), 1)
        ],
        new Point(0, 0),
        new Point(0, 1));
    private static readonly Brush RaceChequeredLightCell = FrozenLinearGradient(
        [
            new GradientStop(BrushColor(0xFA, 0xFA, 0xF6), 0),
            new GradientStop(BrushColor(0xB5, 0xBA, 0xBC), 0.48),
            new GradientStop(BrushColor(0xEE, 0xEF, 0xEA), 1)
        ],
        new Point(0, 0),
        new Point(1, 1));
    private static readonly Brush RaceChequeredDarkCell = FrozenLinearGradient(
        [
            new GradientStop(BrushColor(0x27, 0x2D, 0x34), 0),
            new GradientStop(BrushColor(0x06, 0x08, 0x0C), 0.52),
            new GradientStop(BrushColor(0x1A, 0x1F, 0x26), 1)
        ],
        new Point(0, 0),
        new Point(1, 1));
    private static readonly Brush RaceChequeredSatin = FrozenLinearGradient(
        [
            new GradientStop(Color.FromArgb(0x32, 0xFF, 0xFF, 0xF6), 0),
            new GradientStop(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 0.28),
            new GradientStop(Color.FromArgb(0x38, 0x00, 0x00, 0x00), 0.72),
            new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 1)
        ],
        new Point(0, 0),
        new Point(1, 1));
    private static readonly Brush RaceChequeredFoldOverlay = FrozenLinearGradient(
        [
            new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0),
            new GradientStop(Color.FromArgb(0x42, 0x00, 0x00, 0x00), 0.13),
            new GradientStop(Color.FromArgb(0x06, 0x00, 0x00, 0x00), 0.24),
            new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xF8), 0.34),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45),
            new GradientStop(Color.FromArgb(0x48, 0x00, 0x00, 0x00), 0.58),
            new GradientStop(Color.FromArgb(0x05, 0x00, 0x00, 0x00), 0.69),
            new GradientStop(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xF8), 0.79),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.88),
            new GradientStop(Color.FromArgb(0x32, 0x00, 0x00, 0x00), 1)
        ],
        new Point(0.05, 1),
        new Point(0.96, 0));
    private static readonly Pen RaceChequeredFibrePen = FrozenPen(
        BrushOf(0xFF, 0xFF, 0xFF, 0.075),
        0.65);
    private static readonly Pen RaceChequeredHighlightPen = FrozenPen(
        BrushOf(0xFF, 0xFF, 0xFA, 0.13),
        0.6);
    private static readonly Pen RaceChequeredShadowPen = FrozenPen(
        BrushOf(0x00, 0x00, 0x00, 0.23),
        0.7);
    private static readonly Brush White = BrushOf(0xF3, 0xF4, 0xF5);
    private static readonly Brush Muted = BrushOf(0x8B, 0x90, 0x99);
    private static readonly Brush RaceSecondary = BrushOf(0xAE, 0xB8, 0xC4);
    private static readonly Brush Graphite = BrushOf(0x20, 0x25, 0x2D);
    private static readonly Brush Cyan = BrushOf(0x20, 0xB8, 0xCF);
    private static readonly Brush RaceStrategyBackground = FrozenLinearGradient(
        [
            new GradientStop(BrushColor(0x08, 0x0C, 0x12), 0),
            new GradientStop(BrushColor(0x0D, 0x15, 0x1D), 0.58),
            new GradientStop(BrushColor(0x08, 0x0C, 0x12), 1)
        ],
        new Point(0, 0),
        new Point(1, 0.72));
    private static readonly Brush RaceStrategyCyan = BrushOf(0x27, 0xDB, 0xED);
    private static readonly Brush RaceStrategyAmber = BrushOf(0xFF, 0xB8, 0x2E);
    private readonly Func<IReadOnlyList<IHudContribution>> getContributions;
    private readonly Func<OverlayLayout> getLayout;
    private readonly HudSurfaceKind kind;
    private bool layoutPreview;
    private bool estateRaceFinishedPreview;
    private bool estateRaceChequeredPreview;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly FrameRateLimiter limiter;
    private readonly FrameRateLimiter animatedLimiter = new(30);
    private readonly FrameRateLimiter transitionLimiter = new(60);
    private readonly DashboardHudDynamics dashboardDynamics = new();
    private readonly LapHudDynamics lapDynamics = new();
    private readonly Dictionary<Guid, PitHudRuntime> pitHudRuntime = [];
    private readonly EstatePitWindowHudRuntime pitWindowHudRuntime = new();
    private readonly EstateFullRaceStrategyHudRuntime fullRaceStrategyHudRuntime = new();
    private readonly EstateRaceHudAnimationController estateRaceWidgetAnimations = new();
    private readonly Dictionary<EstateRaceHudWidgetKind, RaceWidgetDrawingRuntime> raceWidgetDrawings = [];
    private readonly Dictionary<EstateRaceHudWidgetKind, EstateRaceWidgetVisual> raceWidgetVisuals = [];
    private readonly Dictionary<Guid, RaceMapPointRuntime> raceMapPointRuntime = [];
    private readonly Dictionary<Guid, AnimatedRowRuntime> leaderboardRowRuntime = [];
    private readonly Dictionary<Guid, LeaderboardValueRuntime> leaderboardValueRuntime = [];
    private readonly Dictionary<Guid, AnimatedRowRuntime> pitStopRowRuntime = [];
    private readonly double[] startLightLevels = new double[5];
    private double renderedBrake;
    private double previousPedalRenderSeconds;
    private bool pedalAnimationInitialized;
    private RaceHeaderSignal raceHeaderSignal;
    private RaceHeaderSignal previousRaceHeaderSignal;
    private double raceHeaderTransitionStartedAt = double.NegativeInfinity;
    private double smoothAnimationUntilSeconds = double.NegativeInfinity;
    private bool estateRaceContinuousAnimation;
    private bool lapFadeAnimation;
    private bool raceWidgetContentAnimation;
    private bool raceMapPointAnimation;
    private bool raceMapFlagAnimation;
    private bool leaderboardRowAnimation;
    private bool pitStopRowAnimation;
    private bool startLightAnimation;
    private bool practiceProgramAnimation;
    private double estateRaceAnimationNowSeconds;
    private double previousStartLightRenderSeconds = double.NaN;
    private double previousPracticeProgramRenderSeconds = double.NaN;
    private EstatePracticeTestKind? animatedPracticeProgramKind;
    private double animatedPracticeProgress;
    private string? practiceGuidanceAnimationKey;
    private double practiceGuidanceAnimationStartedSeconds;
    private bool estateRaceReduceMotion;
    private string? raceMapFlagVisualKey;
    private string? previousRaceMapFlagVisualKey;
    private double raceMapFlagTransitionStartedSeconds = double.NegativeInfinity;
    private string? raceLogoHash;
    private ImageSource? raceLogoImage;
    private readonly EstateRaceLeaderboardRefreshCache raceComparisonCache = new();
    private bool renderingSubscribed;

    public HudSurface(
        Func<IReadOnlyList<IHudContribution>> getContributions,
        Func<OverlayLayout> getLayout,
        HudSurfaceKind kind,
        bool layoutPreview = false)
    {
        this.getContributions = getContributions;
        this.getLayout = getLayout;
        this.kind = kind;
        limiter = new FrameRateLimiter(kind switch
        {
            HudSurfaceKind.EstateRace => 10,
            HudSurfaceKind.Lap or HudSurfaceKind.Drift => 30,
            _ => 60
        });
        this.layoutPreview = layoutPreview;
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            if (this.layoutPreview) return;
            CompositionTarget.Rendering += OnRendering;
            renderingSubscribed = true;
        };
        Unloaded += (_, _) =>
        {
            if (!renderingSubscribed) return;
            CompositionTarget.Rendering -= OnRendering;
            renderingSubscribed = false;
        };
    }

    internal bool LayoutPreview
    {
        get => layoutPreview;
        set => layoutPreview = value;
    }

    internal bool EstateRaceFinishedPreview
    {
        get => estateRaceFinishedPreview;
        set => estateRaceFinishedPreview = value;
    }

    internal bool EstateRaceChequeredPreview
    {
        get => estateRaceChequeredPreview;
        set => estateRaceChequeredPreview = value;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Layout previews contain deterministic example data. Repainting four
        // full-screen preview surfaces on every composition tick only burns UI
        // time and can restart transient animations while the user is dragging.
        // WPF still renders the first frame, and editor changes explicitly
        // invalidate the affected preview surface.
        if (layoutPreview) return;

        var nowSeconds = clock.Elapsed.TotalSeconds;
        var reduceMotion = getLayout().ReduceMotion;
        var activeLimiter = !reduceMotion &&
                            (nowSeconds < smoothAnimationUntilSeconds || lapFadeAnimation)
            ? transitionLimiter
            : !reduceMotion && (estateRaceContinuousAnimation || lapFadeAnimation)
                ? animatedLimiter
                : limiter;
        if (activeLimiter.ShouldRender(nowSeconds)) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var contributions = getContributions();
        var now = DateTimeOffset.UtcNow;
        var layout = getLayout();
        if (kind == HudSurfaceKind.Dashboard)
        {
            RenderDashboard(
                drawingContext,
                LastSnapshot<Modules.Dashboard.DashboardHudState>(contributions),
                now,
                layout);
            return;
        }

        if (kind == HudSurfaceKind.Lap)
        {
            RenderLap(
                drawingContext,
                LastSnapshot<Modules.Dashboard.DashboardHudState>(contributions),
                LastSnapshot<Modules.LapAnalysis.LapHudState>(contributions),
                LastSnapshot<EstateRaceHudState>(contributions),
                now,
                layout);
            return;
        }

        if (kind == HudSurfaceKind.EstateRace)
        {
            RenderEstateRace(
                drawingContext,
                LastSnapshot<EstateRaceHudState>(contributions),
                now,
                layout);
            return;
        }

        RenderDrift(drawingContext, LastSnapshot<DriftHudState>(contributions), now, layout);
    }

    private static T? LastSnapshot<T>(IReadOnlyList<IHudContribution> contributions)
        where T : class
    {
        for (var index = contributions.Count - 1; index >= 0; index--)
            if (contributions[index].Snapshot is T snapshot)
                return snapshot;
        return null;
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
        EstateRaceHudState? estateRace,
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

        var lapVisible = OverlayVisibilityPolicy.ShouldShowLap(lap, now, layout.LiveHudStaleSeconds) &&
                         EstateRaceAllowsLapTiming(estateRace);
        if (lap is not null && EstateRaceAllowsLapTiming(estateRace))
            lap = ApplyEstateRaceLapColors(lap, estateRace);
        var nowSeconds = clock.Elapsed.TotalSeconds;
        var lapVisual = lapDynamics.Update(lap, lapVisible, layout, nowSeconds);
        lapFadeAnimation = lapVisual.Opacity > 0.001 && lapVisual.Opacity < 0.999;
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

    private void RenderEstateRace(
        DrawingContext dc,
        EstateRaceHudState? state,
        DateTimeOffset now,
        OverlayLayout layout)
    {
        estateRaceContinuousAnimation = false;
        raceWidgetContentAnimation = false;
        raceMapPointAnimation = false;
        raceMapFlagAnimation = false;
        leaderboardRowAnimation = false;
        pitStopRowAnimation = false;
        startLightAnimation = false;
        practiceProgramAnimation = false;
        estateRaceAnimationNowSeconds = clock.Elapsed.TotalSeconds;
        estateRaceReduceMotion = layout.ReduceMotion || layoutPreview;
        if (layoutPreview)
        {
            pitHudRuntime.Clear();
            state = OverlayLayoutPreviewState.EstateRace(
                now,
                estateRaceFinishedPreview,
                estateRaceChequeredPreview);
        }
        if (state?.Session is not { } session ||
            state.ConnectionState is not (EstateRaceConnectionState.Connected or
                EstateRaceConnectionState.Reconnecting))
            return;
        var networkQuality = SelectRaceNetworkQuality(state, now);
        if (now - state.UpdatedAt > (networkQuality != EstateRaceNetworkQuality.Normal
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromSeconds(Math.Max(2, layout.LiveHudStaleSeconds * 4))))
            return;

        var estimatedServerNow = state.ServerClockOffset is TimeSpan serverClockOffset
            ? now + serverClockOffset
            : session.ServerTime + (now - state.UpdatedAt) + state.EstimatedOneWayLatency;
        var widgets = layout.EstateRaceWidgets ?? EstateRaceHudLayoutSettings.Default;
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.Leaderboard,
            widgets.Get(EstateRaceHudWidgetKind.Leaderboard),
            widgetDc => DrawRaceLeaderboard(widgetDc, state, session, estimatedServerNow, networkQuality));
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.TrackMap,
            widgets.Get(EstateRaceHudWidgetKind.TrackMap),
            widgetDc => DrawRaceTrackMap(widgetDc, state, session));
        var localParticipant = session.Participants.FirstOrDefault(item => item.Id == state.LocalParticipantId);
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.GripStatus,
            widgets.Get(EstateRaceHudWidgetKind.GripStatus),
            widgetDc => DrawRaceGripStatus(widgetDc, state),
            state.LocalGripCondition != RaceGripCondition.Unknown &&
            localParticipant is not { IsInPitLane: true } and not { IsInServiceZone: true },
            state.LocalGripCondition.ToString());
        var banner = ShouldSuppressRaceStartBanner(session, session.Banner)
            ? null
            : session.Banner;
        if (session.BlueFlags?.Any(item => item.RecipientParticipantId == state.LocalParticipantId) == true)
            banner = new EstateRaceBanner(
                Guid.Empty,
                RaceBannerKind.BlueFlag,
                "蓝旗 · 后方快车正在套圈",
                "请保持可预判路线，并在安全位置让行",
                state.LocalParticipantId,
                now,
                null);
        banner = ApplyStartSequenceCountdown(session, banner, estimatedServerNow);
        if (EstateRaceHudVisibilityPolicy.ShouldShowBanner(session, banner, estimatedServerNow))
        {
            DrawRaceWidget(dc, EstateRaceHudWidgetKind.Banner,
                widgets.Get(EstateRaceHudWidgetKind.Banner),
                widgetDc => DrawRaceBanner(widgetDc, banner!),
                contentKey: RaceBannerAnimationKey(session, banner!, estimatedServerNow));
        }
        else
            DrawRaceWidget(dc, EstateRaceHudWidgetKind.Banner,
                widgets.Get(EstateRaceHudWidgetKind.Banner), _ => { }, false);
        var startLightSession = layoutPreview
            ? session with { IlluminatedStartLights = 5, StartLightsOut = false }
            : session;
        var showStartLights = layoutPreview || session.Phase == RaceSessionPhase.Countdown ||
                              session.Phase == RaceSessionPhase.Race && session.StartLightsOut &&
                              session.StartsAt is DateTimeOffset lightsOutAt &&
                              estimatedServerNow - lightsOutAt < TimeSpan.FromSeconds(1);
        if (!showStartLights)
        {
            Array.Clear(startLightLevels);
            previousStartLightRenderSeconds = double.NaN;
        }
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.StartLights,
            widgets.Get(EstateRaceHudWidgetKind.StartLights),
            widgetDc => DrawRaceStartLights(widgetDc, startLightSession), showStartLights);
        var pitHud = UpdatePitHud(session, state.LocalParticipantId, state.PitService, now, estimatedServerNow);
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.PitStopInfo,
            widgets.Get(EstateRaceHudWidgetKind.PitStopInfo),
            widgetDc => DrawRacePitStopInfo(widgetDc, pitHud),
            session.Phase == RaceSessionPhase.Race && pitHud.Entries.Count > 0,
            PitHudAnimationKey(pitHud));
        var limiterVisible = EstateRaceHudVisibilityPolicy.ShouldShowPitLimiter(state.PitService);
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.PitLimiter,
            widgets.Get(EstateRaceHudWidgetKind.PitLimiter),
            widgetDc => DrawRacePitLimiter(widgetDc, state.PitService), limiterVisible,
            state.PitService.IsSpeeding ? "speeding" : "within-limit");
        var penaltyVisible = EstateRaceHudVisibilityPolicy.ShouldShowPenaltyStatus(
            session,
            localParticipant,
            estimatedServerNow);
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.PenaltyStatus,
            widgets.Get(EstateRaceHudWidgetKind.PenaltyStatus),
            widgetDc => DrawRacePenaltyStatus(widgetDc, localParticipant!), penaltyVisible,
            penaltyVisible ? PenaltyAnimationKey(localParticipant!) : null);
        var activePractice = state.PracticeTests?.Items.FirstOrDefault(item => item.IsVisibleOnHud(now));
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.PracticeProgram,
            widgets.Get(EstateRaceHudWidgetKind.PracticeProgram),
            widgetDc => DrawRacePracticeProgram(widgetDc, activePractice!),
            activePractice is not null &&
            (layoutPreview || EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(state, now)),
            activePractice is null
                ? null
                : $"{activePractice.Kind}:{activePractice.Status}");
        var pitWindow = pitWindowHudRuntime.Update(
            session,
            state.LocalParticipantId,
            state.PitStrategy,
            now,
            layoutPreview && !estateRaceFinishedPreview);
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.PitWindowSuggestion,
            widgets.Get(EstateRaceHudWidgetKind.PitWindowSuggestion),
            widgetDc => DrawRacePitWindowSuggestion(widgetDc, pitWindow),
            pitWindow.IsVisible,
            pitWindow.IsVisible
                ? $"{pitWindow.StartLap}:{pitWindow.EndLap}:{pitWindow.LapsUntilWindow}:{pitWindow.WindowOpen}"
                : null);
        var fullRaceStrategy = fullRaceStrategyHudRuntime.Update(
            session,
            state.LocalParticipantId,
            state.PitStrategy,
            estimatedServerNow,
            estateRaceAnimationNowSeconds,
            layoutPreview && !estateRaceFinishedPreview);
        DrawRaceWidget(dc, EstateRaceHudWidgetKind.FullRaceStrategy,
            widgets.Get(EstateRaceHudWidgetKind.FullRaceStrategy),
            widgetDc => DrawRaceFullStrategy(widgetDc, fullRaceStrategy),
            fullRaceStrategy.IsVisible,
            FullRaceStrategyAnimationKey(fullRaceStrategy));
        var transientAnimation = estateRaceWidgetAnimations.AnyAnimating ||
                                 raceWidgetContentAnimation ||
                                 startLightAnimation ||
                                 practiceProgramAnimation ||
                                 raceMapFlagAnimation;
        if (!estateRaceReduceMotion && transientAnimation)
            smoothAnimationUntilSeconds = Math.Max(
                smoothAnimationUntilSeconds,
                estateRaceAnimationNowSeconds + 0.05);
        estateRaceContinuousAnimation = raceHeaderSignal != RaceHeaderSignal.None ||
                                        showStartLights ||
                                        pitHud.Entries.Count > 0 ||
                                        penaltyVisible ||
                                        limiterVisible && state.PitService.IsSpeeding ||
                                        estateRaceWidgetAnimations.AnyAnimating ||
                                        raceWidgetContentAnimation ||
                                        raceMapPointAnimation ||
                                        raceMapFlagAnimation ||
                                        leaderboardRowAnimation ||
                                        pitStopRowAnimation ||
                                        startLightAnimation ||
                                        practiceProgramAnimation;
    }

    private void DrawRaceWidget(
        DrawingContext dc,
        EstateRaceHudWidgetKind kind,
        EstateRaceHudWidgetPlacement placement,
        Action<DrawingContext> draw,
        bool contentVisible = true,
        string? contentKey = null)
    {
        var requestedVisible = placement.IsVisible && contentVisible && placement.Opacity > 0.001;
        var visual = estateRaceWidgetAnimations.Update(
            kind,
            requestedVisible,
            estateRaceAnimationNowSeconds,
            estateRaceReduceMotion,
            layoutPreview);
        raceWidgetVisuals[kind] = visual;
        if (!visual.ShouldDraw) return;

        if (!raceWidgetDrawings.TryGetValue(kind, out var runtime))
        {
            runtime = new RaceWidgetDrawingRuntime();
            raceWidgetDrawings[kind] = runtime;
        }

        if (requestedVisible)
        {
            var group = new DrawingGroup();
            using (var groupDc = group.Open()) draw(groupDc);
            if (runtime.Current is not null && contentKey is not null &&
                !string.Equals(runtime.ContentKey, contentKey, StringComparison.Ordinal))
            {
                runtime.Outgoing = runtime.Current;
                runtime.ContentTransitionStartedSeconds = estateRaceAnimationNowSeconds;
            }
            runtime.Current = group;
            runtime.ContentKey = contentKey;
        }

        var drawing = runtime.Current;
        if (drawing is null) return;
        var widgetSize = EstateRaceWidgetNominalSize(kind, ActualWidth, ActualHeight);
        dc.PushOpacity(placement.Opacity * visual.Opacity);
        dc.PushTransform(new TranslateTransform(
            placement.Left * ActualWidth + visual.OffsetXFactor * ActualWidth,
            placement.Top * ActualHeight + visual.OffsetYFactor * ActualHeight));
        // Saved placement coordinates describe the scaled widget's top-left
        // corner. Apply that scale at the local origin so editor selection and
        // hit-testing stay aligned. Only the transient entry animation scales
        // around the widget centre.
        dc.PushTransform(new ScaleTransform(placement.Scale, placement.Scale));
        dc.PushTransform(new ScaleTransform(
            visual.Scale,
            visual.Scale,
            widgetSize.Width / 2,
            widgetSize.Height / 2));

        var contentProgress = estateRaceReduceMotion
            ? 1
            : SmoothStep(
                (estateRaceAnimationNowSeconds - runtime.ContentTransitionStartedSeconds) /
                RaceWidgetContentTransitionSeconds(kind));
        if (runtime.Outgoing is not null && contentProgress < 1)
        {
            raceWidgetContentAnimation = true;
            dc.PushOpacity(1 - contentProgress);
            dc.DrawDrawing(runtime.Outgoing);
            dc.Pop();
            dc.PushOpacity(contentProgress);
            dc.DrawDrawing(drawing);
            dc.Pop();
        }
        else
        {
            runtime.Outgoing = null;
            dc.DrawDrawing(drawing);
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    internal static EstateRaceBanner? ApplyStartSequenceCountdown(
        EstateRaceSession session,
        EstateRaceBanner? banner,
        DateTimeOffset estimatedServerNow)
    {
        if (banner is null || session.Phase != RaceSessionPhase.Countdown ||
            session.StartSequenceAt is not DateTimeOffset startSequenceAt)
            return banner;

        var wholeSeconds = Math.Max(
            0,
            (int)Math.Ceiling((startSequenceAt - estimatedServerNow).TotalSeconds));
        return banner with { Detail = $"{wholeSeconds} 秒后启动发车程序" };
    }

    internal static bool ShouldSuppressRaceStartBanner(
        EstateRaceSession session,
        EstateRaceBanner? banner) =>
        banner is
        {
            Kind: RaceBannerKind.Information,
            Title: "比赛开始",
            IsInvestigation: false
        } &&
        session.Phase == RaceSessionPhase.Race &&
        session.StartLightsOut;

    private static string RaceBannerAnimationKey(
        EstateRaceSession session,
        EstateRaceBanner banner,
        DateTimeOffset estimatedServerNow)
    {
        if (session.Phase != RaceSessionPhase.Countdown ||
            session.StartSequenceAt is not DateTimeOffset startSequenceAt)
            return banner.Id.ToString("N");
        var seconds = Math.Max(
            0,
            (int)Math.Ceiling((startSequenceAt - estimatedServerNow).TotalSeconds));
        return $"{banner.Id:N}:{seconds}";
    }

    private static string PitHudAnimationKey(PitHudSnapshot snapshot) =>
        $"{snapshot.ActiveParticipantCount}:" + string.Join('|', snapshot.Entries.Select(entry =>
            $"{entry.ParticipantId:N}:{entry.IsPenalty}:{entry.PenaltyCompleted}:{entry.IsService}:{entry.ServiceCompleted}"));

    private static string PenaltyAnimationKey(EstateRaceParticipant participant) =>
        $"{participant.IsServingTimePenalty}:{participant.PenaltyServiceCompleted}:" +
        $"{participant.HasPendingDriveThrough}:{participant.IsServingDriveThrough}:{participant.DriveThroughOverdue}";

    private static string? FullRaceStrategyAnimationKey(FullRaceStrategyHudSnapshot snapshot) =>
        snapshot.IsVisible
            ? $"{snapshot.TotalLaps}:{snapshot.MinimumRequiredStops}:{snapshot.CompletedStops}:" +
              string.Join('|', snapshot.StopWindows.Select(window =>
                  $"{window.StartLap}-{window.EndLap}-{window.TargetLap}"))
            : null;

    internal static Size EstateRaceWidgetNominalSize(
        EstateRaceHudWidgetKind kind,
        double width,
        double height) => kind switch
    {
        EstateRaceHudWidgetKind.Leaderboard => new Size(
            width * 0.235,
            height * (0.053 + 0.026) + Math.Max(36, height * 0.045) * 12),
        EstateRaceHudWidgetKind.TrackMap => new Size(
            Math.Min(width * 0.19, height * 0.28),
            Math.Min(width * 0.19, height * 0.28)),
        EstateRaceHudWidgetKind.GripStatus => new Size(width * 0.20, height * 0.095),
        EstateRaceHudWidgetKind.Banner => new Size(width * 0.50, height * 0.09),
        EstateRaceHudWidgetKind.StartLights => new Size(width * 0.30, height * 0.09),
        EstateRaceHudWidgetKind.PitStopInfo => new Size(width * 0.215, height * (0.041 + 0.0665 * 2)),
        EstateRaceHudWidgetKind.PitLimiter => new Size(height * 0.11, height * 0.11),
        EstateRaceHudWidgetKind.PenaltyStatus => new Size(width * 0.27, height * 0.105),
        EstateRaceHudWidgetKind.PracticeProgram => new Size(width * 0.32, height * 0.12),
        EstateRaceHudWidgetKind.PitWindowSuggestion => new Size(width * 0.19, height * 0.14),
        EstateRaceHudWidgetKind.FullRaceStrategy => new Size(width * 0.68, height * 0.36),
        _ => new Size(1, 1)
    };

    private double RaceWidgetEntryProgress(EstateRaceHudWidgetKind kind) =>
        raceWidgetVisuals.TryGetValue(kind, out var visual) ? visual.Opacity : 1;

    private static double RaceWidgetContentTransitionSeconds(EstateRaceHudWidgetKind kind) => kind switch
    {
        EstateRaceHudWidgetKind.PitStopInfo => 0.14,
        EstateRaceHudWidgetKind.PitLimiter => 0.16,
        EstateRaceHudWidgetKind.PenaltyStatus => 0.20,
        EstateRaceHudWidgetKind.PitWindowSuggestion => 0.16,
        EstateRaceHudWidgetKind.FullRaceStrategy => 0.22,
        _ => 0.18
    };

    private void DrawRaceLeaderboard(
        DrawingContext dc,
        EstateRaceHudState state,
        EstateRaceSession session,
        DateTimeOffset estimatedServerNow,
        EstateRaceNetworkQuality networkQuality)
    {
        var width = ActualWidth * 0.235;
        var participants = session.Participants.Take(12).ToArray();
        var localParticipant = participants.FirstOrDefault(item => item.Id == state.LocalParticipantId);
        var leaderParticipant = participants.FirstOrDefault(item => item.Position == 1) ??
                                participants.FirstOrDefault();
        var qualifying = session.Phase is RaceSessionPhase.Qualifying or RaceSessionPhase.Grid ||
                         session.Phase == RaceSessionPhase.Suspended &&
                         session.SuspendedFromPhase == RaceSessionPhase.Qualifying;
        var practice = session.Phase == RaceSessionPhase.Practice ||
                       session.Phase == RaceSessionPhase.Suspended &&
                       session.SuspendedFromPhase == RaceSessionPhase.Practice;
        var timedLap = qualifying || practice;
        var race = session.Phase == RaceSessionPhase.Race ||
                   session.Phase == RaceSessionPhase.Suspended &&
                   session.SuspendedFromPhase == RaceSessionPhase.Race;
        var finished = session.Phase == RaceSessionPhase.Finished;
        var targetSignal = finished
            ? RaceHeaderSignal.None
            : SelectRaceHeaderSignal(session, state.LocalParticipantId, networkQuality);
        var chequeredHeader = targetSignal == RaceHeaderSignal.Chequered;
        var premiumFinishHeader = chequeredHeader || finished;
        var topHeaderHeight = ActualHeight * (premiumFinishHeader ? 0.047 : 0.053);
        var stageHeaderHeight = ActualHeight * (premiumFinishHeader ? 0.032 : 0.026);
        var headerHeight = topHeaderHeight + stageHeaderHeight;
        var rowHeight = Math.Max(36, ActualHeight * 0.045);
        var height = headerHeight + participants.Length * rowHeight;
        UpdateRaceHeaderSignal(targetSignal);
        var reduceMotion = getLayout().ReduceMotion;
        var transitioningFromChequered = finished && previousRaceHeaderSignal == RaceHeaderSignal.Chequered;
        var transitionDuration = targetSignal == RaceHeaderSignal.Chequered
            ? 0.62
            : transitioningFromChequered
                ? 0.58
                : 0.22;
        var transitionProgress = reduceMotion
            ? 1
            : Math.Clamp((clock.Elapsed.TotalSeconds - raceHeaderTransitionStartedAt) / transitionDuration, 0, 1);
        transitionProgress = 1 - Math.Pow(1 - transitionProgress, 3);
        var accent = premiumFinishHeader
            ? RaceChequeredGoldAccent
            : qualifying
            ? BrushOf(0xB4, 0x63, 0xFF)
            : BrushOf(0x38, 0xD5, 0xE8);
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.94),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.34), 1),
            new Rect(0, 0, width, height),
            8,
            8);
        var leaderboardClip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        leaderboardClip.Freeze();
        dc.PushClip(leaderboardClip);
        dc.DrawRectangle(
            premiumFinishHeader
                ? RaceChequeredHeaderBackground
                : BrushOf(0x10, 0x16, 0x20, 0.99),
            null,
            new Rect(0, 0, width, topHeaderHeight));
        dc.DrawRectangle(
            premiumFinishHeader
                ? RaceChequeredStageBackground
                : BrushOf(0x08, 0x0C, 0x12, 0.99),
            null,
            new Rect(0, topHeaderHeight, width, stageHeaderHeight));
        dc.DrawRectangle(accent, null, new Rect(0, 0, Math.Max(4, width * 0.014), headerHeight));
        var headerBottomRule = premiumFinishHeader
            ? BrushOf(0x8B, 0x96, 0xA3, 0.48)
            : BrushWithOpacity(accent, 0.80);
        dc.DrawRectangle(headerBottomRule, null,
            new Rect(width * 0.055, headerHeight - 2, width * 0.89, 2));
        var organizerLogoBounds = premiumFinishHeader
            ? new Rect(width * 0.030, topHeaderHeight * 0.08, width * 0.135, topHeaderHeight * 0.84)
            : new Rect(width * 0.035, topHeaderHeight * 0.13, width * 0.115, topHeaderHeight * 0.74);
        DrawRaceOrganizerLogo(dc, state.OrganizerLogo, organizerLogoBounds);

        var phase = finished ? "FINAL" : practice
            ? session.PracticeSessionCount > 1 && session.PracticeSessionNumber > 0
                ? $"PRACTICE · FP{session.PracticeSessionNumber}"
                : "PRACTICE"
            : qualifying
            ? session.QualifyingSessionCount > 1 && session.QualifyingSessionNumber > 0
                ? $"QUALIFYING · Q{session.QualifyingSessionNumber}"
                : "QUALIFYING"
            :
            session.Phase == RaceSessionPhase.Race ? "RACE" : RacePhaseText(session.Phase).ToUpperInvariant();
        if (finished)
        {
            if (transitioningFromChequered && transitionProgress < 1)
                DrawRaceChequeredHeader(
                    dc,
                    width,
                    topHeaderHeight,
                    1 - transitionProgress,
                    reduceMotion);
            DrawRaceFinishedHeader(
                dc,
                width,
                topHeaderHeight,
                transitionProgress,
                leaderParticipant);
        }
        else if (targetSignal == RaceHeaderSignal.None)
            RaceTitleText(dc, phase, width * 0.935, topHeaderHeight * 0.52,
                Math.Max(14, topHeaderHeight * 0.42), White, TextAlignment.Right);
        else if (targetSignal == RaceHeaderSignal.Chequered)
            DrawRaceChequeredHeader(dc, width, topHeaderHeight, transitionProgress, reduceMotion);
        else
        {
            var signalWidth = width * (0.72 + 0.28 * transitionProgress);
            var signalLeft = width - signalWidth;
            var signalColor = RaceHeaderSignalColor(targetSignal);
            dc.DrawRectangle(BrushWithOpacity(signalColor, 0.13 + transitionProgress * 0.08), null,
                new Rect(signalLeft, 0, signalWidth, topHeaderHeight));
            dc.DrawRectangle(BrushWithOpacity(signalColor, 0.92), null,
                new Rect(signalLeft, topHeaderHeight - 2, signalWidth, 2));
            var signalText = RaceHeaderSignalText(targetSignal);
            var signalPanelBounds = new Rect(
                width * 0.805,
                topHeaderHeight * 0.13,
                width * 0.16,
                topHeaderHeight * 0.74);
            var signalTextLeft = width * 0.18;
            var signalTextRight = signalPanelBounds.Left - width * 0.04;
            var signalTextCenter = (signalTextLeft + signalTextRight) / 2 -
                                   width * 0.03 * (1 - transitionProgress);
            RaceTitleText(dc, signalText, signalTextCenter,
                topHeaderHeight * 0.52, Math.Max(13, topHeaderHeight * 0.34), White, TextAlignment.Center);
            DrawRaceMarshalPanels(dc, targetSignal, signalPanelBounds, transitionProgress);
        }

        // The lower strip is reserved for session progress. Flag animations are
        // confined to the top bar so remaining time and race laps never jump,
        // disappear or get replaced by marshal instructions.
        var stageDetail = session.Phase switch
        {
            RaceSessionPhase.Finished =>
                $"RACE TIME {FormatRaceTime(participants.FirstOrDefault()?.AdjustedRaceTotalSeconds)} · {participants.Count(item => item.Status == RaceParticipantStatus.Finished)} CLASSIFIED",
            RaceSessionPhase.Practice when session.PracticeEndsAt is DateTimeOffset practiceEnding =>
                $"{(session.PracticeSessionCount > 1 ? $"FP{session.PracticeSessionNumber} · " : string.Empty)}REMAINING {FormatRemaining(practiceEnding - estimatedServerNow)}",
            RaceSessionPhase.Practice when session.PracticeTimeExpired &&
                                                   session.PracticeSessionCount > 1 &&
                                                   session.PracticeSessionNumber < session.PracticeSessionCount =>
                $"FP{session.PracticeSessionNumber} COMPLETE · WAITING FOR FP{session.PracticeSessionNumber + 1}",
            RaceSessionPhase.Practice when session.PracticeTimeExpired =>
                $"{(session.PracticeSessionCount > 1 ? $"FP{session.PracticeSessionNumber}" : "PRACTICE")} COMPLETE",
            RaceSessionPhase.Qualifying when session.QualifyingEndsAt is DateTimeOffset ending =>
                $"{(session.QualifyingSessionCount > 1 ? $"Q{session.QualifyingSessionNumber} · " : string.Empty)}REMAINING {FormatRemaining(ending - estimatedServerNow)}",
            RaceSessionPhase.Qualifying when session.QualifyingTimeExpired &&
                                                     session.QualifyingSessionCount > 1 &&
                                                     session.QualifyingSessionNumber < session.QualifyingSessionCount =>
                $"Q{session.QualifyingSessionNumber} COMPLETE · WAITING FOR Q{session.QualifyingSessionNumber + 1}",
            RaceSessionPhase.Qualifying when session.QualifyingTimeExpired =>
                $"{(session.QualifyingSessionCount > 1 ? $"Q{session.QualifyingSessionNumber}" : "QUALIFYING")} COMPLETE",
            RaceSessionPhase.Race when session.TotalRaceLaps > 0 =>
                $"TIME {FormatRaceTime(EstimatedRaceElapsedSeconds(session, estimatedServerNow))} · LAP {DisplayedRaceLap(participants.FirstOrDefault(), session.TotalRaceLaps)}/{session.TotalRaceLaps}",
            RaceSessionPhase.Suspended when session.SuspendedFromPhase == RaceSessionPhase.Qualifying &&
                                           session.QualifyingEndsAt is DateTimeOffset ending =>
                $"SESSION SUSPENDED · REMAINING {FormatRemaining(ending - session.ServerTime)}",
            RaceSessionPhase.Suspended when session.SuspendedFromPhase == RaceSessionPhase.Practice &&
                                           session.PracticeEndsAt is DateTimeOffset practiceEnding =>
                $"SESSION SUSPENDED · REMAINING {FormatRemaining(practiceEnding - session.ServerTime)}",
            RaceSessionPhase.Suspended when session.SuspendedFromPhase == RaceSessionPhase.Race &&
                                           session.TotalRaceLaps > 0 =>
                $"SESSION SUSPENDED · LAP {DisplayedRaceLap(participants.FirstOrDefault(), session.TotalRaceLaps)}/{session.TotalRaceLaps}",
            RaceSessionPhase.OutLap => "PROCEED TO THE GRID",
            RaceSessionPhase.FormationLap => "FORMATION LAP",
            RaceSessionPhase.Countdown => "START PROCEDURE",
            RaceSessionPhase.Grid => "GRID SET · WAITING FOR RACE CONTROL",
            _ => "WAITING FOR RACE CONTROL"
        };
        if (finished)
        {
            var detailProgress = SmoothStep((transitionProgress - 0.14) / 0.86);
            dc.PushOpacity(detailProgress);
            RaceText(dc, "FINAL CLASSIFICATION", width * 0.055,
                topHeaderHeight + stageHeaderHeight * 0.52,
                Math.Max(10, stageHeaderHeight * 0.35), White, TextAlignment.Left, true);
            var classified = participants.Count(item => item.Status == RaceParticipantStatus.Finished);
            var laps = Math.Max(0, session.TotalRaceLaps);
            RaceText(dc, $"{laps} LAPS · {classified} CLASSIFIED", width * 0.945,
                topHeaderHeight + stageHeaderHeight * 0.52,
                Math.Max(9, stageHeaderHeight * 0.31), RaceSecondary, TextAlignment.Right, true);
            dc.Pop();
        }
        else
        {
            RaceBoundedText(dc, stageDetail,
                new Rect(width * 0.055, topHeaderHeight, width * 0.89, stageHeaderHeight),
                targetSignal == RaceHeaderSignal.Chequered
                    ? Math.Max(11.5, stageHeaderHeight * 0.38)
                    : Math.Max(10, stageHeaderHeight * 0.38),
                RaceSecondary,
                true);
        }

        var initialLeaderboardRows = leaderboardRowRuntime.Count == 0;
        var visibleLeaderboardIds = participants.Select(item => item.Id).ToHashSet();
        foreach (var staleId in leaderboardRowRuntime.Keys.Where(id => !visibleLeaderboardIds.Contains(id)).ToArray())
            leaderboardRowRuntime.Remove(staleId);
        foreach (var staleId in leaderboardValueRuntime.Keys.Where(id => !visibleLeaderboardIds.Contains(id)).ToArray())
            leaderboardValueRuntime.Remove(staleId);
        for (var index = 0; index < participants.Length; index++)
        {
            var participant = participants[index];
            var eliminationState = QualifyingEliminationState(session, participant);
            var eliminated = eliminationState == QualifyingEliminationVisualState.Eliminated;
            if (!leaderboardRowRuntime.TryGetValue(participant.Id, out var rowRuntime))
            {
                rowRuntime = new AnimatedRowRuntime(
                    index + (initialLeaderboardRows ? 0 : 0.22),
                    initialLeaderboardRows ? 1 : 0,
                    estateRaceAnimationNowSeconds);
                leaderboardRowRuntime[participant.Id] = rowRuntime;
            }
            var rowVisual = rowRuntime.Update(
                index,
                estateRaceAnimationNowSeconds,
                estateRaceReduceMotion,
                0.24,
                0.18);
            leaderboardRowAnimation |= rowVisual.IsAnimating;
            var top = headerHeight + rowVisual.Position * rowHeight;
            dc.PushOpacity(rowVisual.Opacity);
            var local = participant.Id == state.LocalParticipantId;
            if (finished && index < 3)
            {
                var podiumFill = index switch
                {
                    0 => BrushOf(0xFF, 0xD1, 0x66, 0.18),
                    1 => BrushOf(0xC5, 0xD0, 0xDC, 0.12),
                    _ => BrushOf(0xD0, 0x8A, 0x55, 0.12)
                };
                dc.DrawRectangle(podiumFill, null, new Rect(0, top, width, rowHeight));
            }
            if (eliminated)
            {
                dc.DrawRectangle(BrushOf(0x78, 0x80, 0x89, 0.11), null,
                    new Rect(0, top, width, rowHeight));
            }
            else if (participant.Id == state.LocalParticipantId)
            {
                dc.DrawRectangle(BrushOf(0x2E, 0xC8, 0xE0, 0.13), null,
                    new Rect(0, top, width, rowHeight));
                dc.DrawRectangle(BrushOf(0x2E, 0xC8, 0xE0, 0.75), null,
                    new Rect(width - 2, top + 3, 2, rowHeight - 6));
            }
            else if (index % 2 == 1)
            {
                dc.DrawRectangle(BrushOf(0xFF, 0xFF, 0xFF, 0.025), null,
                    new Rect(0, top, width, rowHeight));
            }
            if (index > 0)
                dc.DrawLine(new Pen(BrushOf(0x91, 0xA0, 0xB0, 0.16), 1),
                    new Point(width * 0.04, top), new Point(width * 0.96, top));
            dc.DrawRectangle(eliminated ? BrushOf(0x6D, 0x74, 0x7D, 0.62) : RaceThemeBrush(participant.ThemeColor), null,
                new Rect(width * 0.018, top + rowHeight * 0.18, width * 0.010, rowHeight * 0.64));
            var positionFill = eliminationState switch
            {
                QualifyingEliminationVisualState.AtRisk => BrushOf(0xEA, 0x3F, 0x47, 0.96),
                QualifyingEliminationVisualState.Eliminated => BrushOf(0x54, 0x5B, 0x65, 0.78),
                _ when local => BrushOf(0xDE, 0xF8, 0xFC, 0.96),
                _ => BrushOf(0x25, 0x2E, 0x39, 0.90)
            };
            var positionText = eliminationState switch
            {
                QualifyingEliminationVisualState.AtRisk => BrushOf(0x08, 0x0B, 0x11),
                QualifyingEliminationVisualState.Eliminated => BrushOf(0xB5, 0xBB, 0xC3),
                _ when local => BrushOf(0x08, 0x0B, 0x11),
                _ => White
            };
            dc.DrawRoundedRectangle(
                positionFill,
                null,
                new Rect(width * 0.043, top + rowHeight * 0.18, width * 0.095, rowHeight * 0.64),
                3,
                3);
            RaceText(dc, participant.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                width * 0.090, top + rowHeight * 0.5, Math.Max(12, rowHeight * 0.33),
                positionText, TextAlignment.Center, true);
            if (participant.Id == session.FastestParticipantId)
            {
                var center = new Point(width * 0.158, top + rowHeight * 0.5);
                if (race || finished)
                    DrawRaceFastestLapClock(dc, center, rowHeight);
                else
                    dc.DrawEllipse(BrushOf(0xB4, 0x63, 0xFF), null, center,
                        Math.Max(2.5, rowHeight * 0.065),
                        Math.Max(2.5, rowHeight * 0.065));
            }
            var nameWidth = width * 0.31;
            var hasTeam = session.AllowTeams && !string.IsNullOrWhiteSpace(participant.TeamName);
            RaceBoundedText(dc, participant.DisplayName,
                new Rect(width * 0.18, hasTeam ? top : top + rowHeight * 0.17, nameWidth, hasTeam ? rowHeight * 0.66 : rowHeight * 0.66),
                Math.Max(13, rowHeight * 0.31), eliminated ? BrushOf(0x9A, 0xA1, 0xAA) : White, true);
            if (hasTeam)
                RaceBoundedText(dc, participant.TeamName!,
                    new Rect(width * 0.18, top + rowHeight * 0.57, nameWidth, rowHeight * 0.34),
                    Math.Max(10, rowHeight * 0.19),
                    eliminated
                        ? BrushOf(0x77, 0x7F, 0x88)
                        : string.IsNullOrWhiteSpace(participant.TeamColor)
                        ? Muted
                        : RaceThemeBrush(participant.TeamColor!));
            var showPitBadge = ShouldShowLeaderboardPitBadge(session, participant);
            var showFinishBadge = ShouldShowLeaderboardFinishBadge(session, participant);
            var status = finished
                ? EstateRaceLeaderboardFormatter.FormatFinished(
                    participant,
                    leaderParticipant,
                    leaderParticipant?.CompletedLaps ?? 0)
                : participant.QualifyingEliminatedInSession is int eliminatedIn && qualifying
                ? $"OUT Q{eliminatedIn}"
                : raceComparisonCache.Format(
                    participant,
                    localParticipant,
                    timedLap,
                    race,
                    participants,
                    DateTimeOffset.UtcNow,
                    showPitStatus: !showPitBadge);
            var underInvestigation = HasPendingInvestigation(session, participant.Id);
            var penaltyBadge = PendingPenaltyBadge(participant);
            var showPitInValue = !showPitBadge && (participant.IsInServiceZone || participant.IsInPitLane);
            var valueBrush = eliminated
                ? BrushOf(0x8D, 0x94, 0x9D)
                : showPitInValue
                ? BrushOf(0xFF, 0xC4, 0x4D)
                : finished && participant.Position == 1
                    ? RaceChequeredGoldAccent
                : participant.Status == RaceParticipantStatus.Disqualified
                    ? BrushOf(0xFF, 0x45, 0x5F)
                    : participant.Id == session.FastestParticipantId && timedLap
                        ? BrushOf(0xC0, 0x63, 0xFF)
                        : White;
            dc.DrawRoundedRectangle(
                showPitInValue
                    ? BrushOf(0xFF, 0xC4, 0x4D, 0.10)
                    : BrushOf(0x00, 0x00, 0x00, 0.18),
                null,
                new Rect(
                    width * 0.61,
                    top + rowHeight * 0.18,
                    width * (penaltyBadge is null ? 0.35 : 0.22),
                    rowHeight * 0.64),
                3,
                3);
            DrawLeaderboardAnimatedValue(
                dc,
                participant.Id,
                status,
                width * (penaltyBadge is null ? 0.935 : 0.81),
                top + rowHeight * 0.5,
                Math.Max(12, rowHeight * 0.27),
                valueBrush);
            if (penaltyBadge is not null)
                DrawLeaderboardPenaltyBadge(dc, penaltyBadge, width, top, rowHeight);
            DrawLeaderboardStatusBadges(
                dc,
                width,
                top,
                rowHeight,
                underInvestigation,
                showPitBadge,
                showFinishBadge,
                eliminated);
            dc.Pop();
        }
        dc.Pop();
    }

    private void UpdateRaceHeaderSignal(RaceHeaderSignal target)
    {
        if (target == raceHeaderSignal) return;
        previousRaceHeaderSignal = raceHeaderSignal;
        raceHeaderSignal = target;
        raceHeaderTransitionStartedAt = clock.Elapsed.TotalSeconds;
        smoothAnimationUntilSeconds = Math.Max(
            smoothAnimationUntilSeconds,
            raceHeaderTransitionStartedAt + 0.72);
    }

    private void DrawLeaderboardAnimatedValue(
        DrawingContext dc,
        Guid participantId,
        string value,
        double x,
        double y,
        double size,
        Brush brush)
    {
        if (!leaderboardValueRuntime.TryGetValue(participantId, out var runtime))
        {
            runtime = new LeaderboardValueRuntime(value, estateRaceAnimationNowSeconds);
            leaderboardValueRuntime[participantId] = runtime;
        }
        var visual = runtime.Update(
            value,
            estateRaceAnimationNowSeconds,
            estateRaceReduceMotion);
        if (visual.Previous is not null && visual.Progress < 1)
        {
            raceWidgetContentAnimation = true;
            dc.PushOpacity(1 - visual.Progress);
            RaceText(dc, visual.Previous, x, y, size, brush, TextAlignment.Right, true);
            dc.Pop();
            dc.PushOpacity(visual.Progress);
            RaceText(dc, visual.Current, x, y, size, brush, TextAlignment.Right, true);
            dc.Pop();
            return;
        }
        RaceText(dc, visual.Current, x, y, size, brush, TextAlignment.Right, true);
    }

    private static int DisplayedRaceLap(EstateRaceParticipant? leader, int totalRaceLaps) =>
        Math.Clamp((leader?.CompletedLaps ?? 0) + 1, 1, Math.Max(1, totalRaceLaps));

    private static double? EstimatedRaceElapsedSeconds(
        EstateRaceSession session,
        DateTimeOffset estimatedServerNow)
    {
        if (session.RaceElapsedSeconds is not double elapsed) return null;
        if (session.Phase != RaceSessionPhase.Race || estimatedServerNow <= session.ServerTime)
            return elapsed;
        return elapsed + (estimatedServerNow - session.ServerTime).TotalSeconds;
    }

    internal static RaceHeaderSignal SelectRaceHeaderSignal(
        EstateRaceSession session,
        Guid? localParticipantId)
    {
        if (session.Flag == RaceControlFlag.Red) return RaceHeaderSignal.Red;
        if (session.Flag == RaceControlFlag.Chequered ||
            session.ChequeredImminent && session.Flag == RaceControlFlag.Green)
            return RaceHeaderSignal.Chequered;
        if (session.Flag == RaceControlFlag.Green &&
            session.BlueFlags?.Any(item => item.ApproachingParticipantId == localParticipantId) == true)
            return RaceHeaderSignal.Blue;
        if (session.Flag != RaceControlFlag.Yellow) return RaceHeaderSignal.None;

        var zones = session.YellowZones ?? [];
        if (zones.Count == 0 || zones.Any(zone => zone.SectorIndex is null))
            return RaceHeaderSignal.DoubleYellow;
        var localSector = session.Participants
            .FirstOrDefault(item => item.Id == localParticipantId)?.CurrentSector;
        return localSector is int sector && zones.Any(zone => zone.SectorIndex == sector)
            ? RaceHeaderSignal.Yellow
            : RaceHeaderSignal.None;
    }

    internal static EstateRaceNetworkQuality SelectRaceNetworkQuality(
        EstateRaceHudState state,
        DateTimeOffset now)
    {
        if (state.ConnectionState == EstateRaceConnectionState.Reconnecting)
            return EstateRaceNetworkQuality.Reconnecting;
        if (state.ConnectionState != EstateRaceConnectionState.Connected)
            return EstateRaceNetworkQuality.Normal;

        var responseAge = state.LastServerResponseAt is DateTimeOffset lastResponse
            ? now - lastResponse
            : TimeSpan.Zero;
        if (responseAge >= TimeSpan.FromSeconds(9) ||
            state.EstimatedRoundTripLatency >= TimeSpan.FromMilliseconds(450) ||
            state.NetworkJitter >= TimeSpan.FromMilliseconds(140))
            return EstateRaceNetworkQuality.Unstable;
        if (state.EstimatedRoundTripLatency >= TimeSpan.FromMilliseconds(180) ||
            state.NetworkJitter >= TimeSpan.FromMilliseconds(70))
            return EstateRaceNetworkQuality.HighLatency;
        return EstateRaceNetworkQuality.Normal;
    }

    internal static RaceHeaderSignal SelectRaceHeaderSignal(
        EstateRaceSession session,
        Guid? localParticipantId,
        EstateRaceNetworkQuality networkQuality)
    {
        var controlSignal = SelectRaceHeaderSignal(session, localParticipantId);
        return controlSignal == RaceHeaderSignal.None
            ? NetworkHeaderSignal(networkQuality)
            : controlSignal;
    }

    private static RaceHeaderSignal NetworkHeaderSignal(EstateRaceNetworkQuality quality) => quality switch
    {
        EstateRaceNetworkQuality.HighLatency => RaceHeaderSignal.HighLatency,
        EstateRaceNetworkQuality.Unstable => RaceHeaderSignal.NetworkUnstable,
        EstateRaceNetworkQuality.Reconnecting => RaceHeaderSignal.Reconnecting,
        _ => RaceHeaderSignal.None
    };

    internal static string RaceHeaderSignalText(RaceHeaderSignal signal) => signal switch
    {
        RaceHeaderSignal.Yellow or RaceHeaderSignal.DoubleYellow => "YELLOW FLAG",
        RaceHeaderSignal.Red => "RED FLAG",
        RaceHeaderSignal.Blue => "BLUE FLAG",
        RaceHeaderSignal.Chequered => "CHEQUERED FLAG",
        RaceHeaderSignal.HighLatency => "HIGH LATENCY",
        RaceHeaderSignal.NetworkUnstable => "NETWORK UNSTABLE",
        RaceHeaderSignal.Reconnecting => "RECONNECTING",
        _ => string.Empty
    };

    internal static bool HasPendingInvestigation(EstateRaceSession session, Guid participantId) =>
        session.Investigations?.Any(item =>
            item.Status == RaceInvestigationStatus.Pending &&
            (item.ParticipantId == participantId ||
             item.RelatedParticipantIds?.Contains(participantId) == true)) == true;

    internal static bool ShouldShowLeaderboardPitBadge(
        EstateRaceSession session,
        EstateRaceParticipant participant)
    {
        if (!participant.IsInPitLane && !participant.IsInServiceZone) return false;
        return session.Phase is RaceSessionPhase.Lobby or
                   RaceSessionPhase.Practice or
                   RaceSessionPhase.Qualifying or
                   RaceSessionPhase.Grid or
                   RaceSessionPhase.Finished ||
               session.Phase == RaceSessionPhase.Suspended &&
               session.SuspendedFromPhase is RaceSessionPhase.Practice or RaceSessionPhase.Qualifying;
    }

    internal static bool ShouldShowLeaderboardFinishBadge(
        EstateRaceSession session,
        EstateRaceParticipant participant)
    {
        if (participant.Status is RaceParticipantStatus.DidNotFinish or
            RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
            return false;
        if (participant.Status == RaceParticipantStatus.Finished) return true;
        if (participant.QualifyingEliminatedInSession is not null) return true;
        return session.Phase switch
        {
            RaceSessionPhase.Practice => session.PracticeTimeExpired &&
                                         !participant.PracticeFinalLapPending,
            RaceSessionPhase.Qualifying => session.QualifyingTimeExpired &&
                                           !participant.QualifyingFinalLapPending,
            RaceSessionPhase.Grid => true,
            _ => false
        };
    }

    internal static QualifyingEliminationVisualState QualifyingEliminationState(
        EstateRaceSession session,
        EstateRaceParticipant participant)
    {
        if (participant.QualifyingEliminatedInSession is not null)
            return QualifyingEliminationVisualState.Eliminated;
        var currentQualifying = session.Phase == RaceSessionPhase.Qualifying ||
                                session.Phase == RaceSessionPhase.Suspended &&
                                session.SuspendedFromPhase == RaceSessionPhase.Qualifying;
        if (!currentQualifying || !participant.QualifyingEligible ||
            session.QualifyingSessionNumber <= 0 ||
            session.QualifyingEliminationCounts is not { Count: > 0 } eliminationCounts)
            return QualifyingEliminationVisualState.None;
        var index = session.QualifyingSessionNumber - 1;
        if (index >= eliminationCounts.Count || eliminationCounts[index] <= 0)
            return QualifyingEliminationVisualState.None;
        var eligible = session.Participants
            .Where(item => item.QualifyingEligible && item.QualifyingEliminatedInSession is null)
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Id)
            .ToArray();
        var eliminationCount = Math.Min(eliminationCounts[index], Math.Max(0, eligible.Length - 1));
        return eliminationCount > 0 && eligible.TakeLast(eliminationCount).Any(item => item.Id == participant.Id)
            ? QualifyingEliminationVisualState.AtRisk
            : QualifyingEliminationVisualState.None;
    }

    private static Brush RaceHeaderSignalColor(RaceHeaderSignal signal) => signal switch
    {
        RaceHeaderSignal.Yellow or RaceHeaderSignal.DoubleYellow => BrushOf(0xFF, 0xCF, 0x18),
        RaceHeaderSignal.Red => BrushOf(0xFF, 0x2E, 0x43),
        RaceHeaderSignal.Blue => BrushOf(0x24, 0x7B, 0xFF),
        RaceHeaderSignal.HighLatency => BrushOf(0xFF, 0xB2, 0x24),
        RaceHeaderSignal.NetworkUnstable or RaceHeaderSignal.Reconnecting => BrushOf(0xFF, 0x79, 0x2E),
        _ => White
    };

    private void DrawRaceChequeredHeader(
        DrawingContext dc,
        double width,
        double height,
        double transitionProgress,
        bool reduceMotion)
    {
        var champagne = BrushOf(0xF1, 0xC9, 0x72);
        var animationSeconds = reduceMotion ? 0 : clock.Elapsed.TotalSeconds;
        var goldPulse = reduceMotion ? 0.88 : 0.84 + 0.06 * Math.Sin(animationSeconds * Math.PI * 0.72);
        var entryOffset = (1 - transitionProgress) * width * 0.24;
        var sashStart = width * 0.670 + entryOffset;
        var sashShoulder = width * 0.880 + entryOffset * 0.20;
        var finishRuleThickness = Math.Max(1, height * 0.018);
        var sashBottom = height - finishRuleThickness;
        var sash = new StreamGeometry();
        using (var context = sash.Open())
        {
            context.BeginFigure(new Point(sashStart, sashBottom), true, true);
            context.BezierTo(
                new Point(width * 0.730 + entryOffset * 0.72, height * 1.01),
                new Point(width * 0.825 + entryOffset * 0.36, height * 0.23),
                new Point(sashShoulder, -height * 0.04),
                true,
                true);
            context.LineTo(new Point(width + 2, -height * 0.04), true, false);
            context.LineTo(new Point(width + 2, sashBottom), true, false);
        }
        sash.Freeze();

        var sashLeadingEdge = new StreamGeometry();
        using (var context = sashLeadingEdge.Open())
        {
            context.BeginFigure(new Point(sashStart, sashBottom), false, false);
            context.BezierTo(
                new Point(width * 0.730 + entryOffset * 0.72, height * 1.01),
                new Point(width * 0.825 + entryOffset * 0.36, height * 0.23),
                new Point(sashShoulder, -height * 0.04),
                true,
                false);
        }
        sashLeadingEdge.Freeze();

        var smokeRibbon = new StreamGeometry();
        using (var context = smokeRibbon.Open())
        {
            context.BeginFigure(new Point(width * 0.31, height), true, true);
            context.BezierTo(
                new Point(width * 0.48, height * 0.88),
                new Point(width * 0.67, height * 0.20),
                new Point(width * 0.92, 0),
                true,
                true);
            context.LineTo(new Point(width, 0), true, false);
            context.LineTo(new Point(width, height * 0.25), true, false);
            context.BezierTo(
                new Point(width * 0.73, height * 0.34),
                new Point(width * 0.56, height * 0.96),
                new Point(width * 0.38, height),
                true,
                true);
        }
        smokeRibbon.Freeze();
        dc.DrawGeometry(
            BrushOf(0xA7, 0xB0, 0xBA, 0.055 * transitionProgress),
            null,
            smokeRibbon);

        // A shallow layer shadow keeps the woven sash attached to the graphite
        // header instead of looking like a flat checkerboard button.
        dc.PushTransform(new TranslateTransform(0, Math.Max(1, height * 0.035)));
        dc.DrawGeometry(BrushOf(0x00, 0x00, 0x00, 0.42 * transitionProgress), null, sash);
        dc.Pop();

        dc.PushOpacity(transitionProgress);
        dc.PushClip(sash);
        dc.DrawRectangle(
            new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(BrushColor(0x08, 0x0B, 0x10), 0),
                    new(BrushColor(0x13, 0x18, 0x20), 0.56),
                    new(BrushColor(0x08, 0x0B, 0x10), 1)
                },
                new Point(0, 0),
                new Point(1, 1)),
            null,
            new Rect(sashStart - height, 0, width - sashStart + height, height));

        var cellHeight = Math.Max(6, height * 0.200);
        var cellWidth = cellHeight * 1.80;
        var patternDrift = reduceMotion ? 0 : animationSeconds * height * 0.018 % (cellWidth * 2);
        var patternLeft = width * 0.57 - cellWidth * 2 + patternDrift;
        const int patternRows = 7;
        var patternColumns = (int)Math.Ceiling((width - patternLeft) / cellWidth) + 3;
        dc.PushTransform(new SkewTransform(-15, 0, patternLeft, 0));
        for (var row = -1; row < patternRows; row++)
        for (var column = -1; column < patternColumns; column++)
        {
            var left = patternLeft + column * cellWidth;
            var top = row * cellHeight - height * 0.12;
            var lightCell = (row + column) % 2 == 0;
            dc.DrawRectangle(
                lightCell
                    ? RaceChequeredLightCell
                    : RaceChequeredDarkCell,
                null,
                new Rect(left, top, cellWidth + 0.6, cellHeight + 0.6));
            dc.DrawLine(
                RaceChequeredHighlightPen,
                new Point(left, top + cellHeight * 0.26),
                new Point(left + cellWidth, top + cellHeight * 0.26));
            dc.DrawLine(
                RaceChequeredShadowPen,
                new Point(left, top + cellHeight * 0.78),
                new Point(left + cellWidth, top + cellHeight * 0.78));
        }
        dc.Pop();

        dc.DrawRectangle(
            RaceChequeredSatin,
            null,
            new Rect(sashStart - height, 0, width - sashStart + height, height));
        dc.DrawRectangle(
            RaceChequeredFoldOverlay,
            null,
            new Rect(sashStart - height, 0, width - sashStart + height, height));

        // Fine horizontal fibres keep the flag material understated at compact
        // HUD sizes and remain visible without becoming visual noise.
        var fibreStep = Math.Max(2, height * 0.055);
        for (var y = 0d; y < height; y += fibreStep)
            dc.DrawLine(
                RaceChequeredFibrePen,
                new Point(sashStart - height, y),
                new Point(width, y));

        var diagonalFibreStep = Math.Max(6, height * 0.16);
        for (var x = sashStart - height * 1.5; x < width + height; x += diagonalFibreStep)
            dc.DrawLine(
                RaceChequeredFibrePen,
                new Point(x, height),
                new Point(x + height * 0.42, 0));

        if (!reduceMotion)
        {
            var shimmerProgress = animationSeconds % 3.6 / 3.6;
            var shimmerX = width * (0.49 + shimmerProgress * 0.58);
            var shimmerWidth = Math.Max(8, height * 0.20);
            var shimmer = new StreamGeometry();
            using (var context = shimmer.Open())
            {
                context.BeginFigure(new Point(shimmerX, -height * 0.10), true, true);
                context.PolyLineTo([
                    new Point(shimmerX + shimmerWidth, -height * 0.10),
                    new Point(shimmerX - shimmerWidth * 0.35, height * 1.10),
                    new Point(shimmerX - shimmerWidth * 1.35, height * 1.10)
                ], true, false);
            }
            shimmer.Freeze();
            dc.DrawGeometry(BrushOf(0xFF, 0xFA, 0xE8, 0.075 * transitionProgress), null, shimmer);
        }
        dc.Pop();
        dc.Pop();

        dc.DrawGeometry(
            null,
            new Pen(BrushOf(0x00, 0x00, 0x00, 0.42 * transitionProgress), Math.Max(1.4, height * 0.050)),
            sashLeadingEdge);
        dc.DrawGeometry(
            null,
            new Pen(BrushOf(0xD8, 0xDD, 0xDF, 0.13 * transitionProgress), Math.Max(0.6, height * 0.010)),
            sashLeadingEdge);

        // One continuous finish rule closes both the smoked-glass title area
        // and the flag cloth. The sash ends at its upper edge, so the gold line
        // reads as a deliberate hem instead of cutting through the pattern.
        dc.DrawRectangle(
            BrushWithOpacity(champagne, goldPulse * 0.70 * transitionProgress),
            null,
            new Rect(width * 0.004, sashBottom, width * 0.992, finishRuleThickness));

        var separatorPen = new Pen(
            BrushWithOpacity(champagne, 0.84 * transitionProgress),
            Math.Max(1, height * 0.021));
        dc.DrawLine(
            separatorPen,
            new Point(width * 0.174, height * 0.18),
            new Point(width * 0.158, height * 0.82));

        var titleBounds = new Rect(
            width * 0.182 - (1 - transitionProgress) * width * 0.012,
            0,
            width * 0.555,
            height);
        var titleSize = Math.Max(15, height * 0.41);
        var shadowBounds = new Rect(
            titleBounds.X + Math.Max(0.5, height * 0.012),
            titleBounds.Y + Math.Max(0.7, height * 0.018),
            titleBounds.Width,
            titleBounds.Height);
        DrawRaceFinishTitle(
            dc,
            "CHEQUERED FLAG",
            shadowBounds,
            titleSize,
            BrushOf(0x00, 0x00, 0x00, 0.72 * transitionProgress));
        DrawRaceFinishTitle(
            dc,
            "CHEQUERED FLAG",
            titleBounds,
            titleSize,
            BrushOf(0xF7, 0xF7, 0xF3, transitionProgress));
    }

    private static void DrawRaceFinishedHeader(
        DrawingContext dc,
        double width,
        double height,
        double transitionProgress,
        EstateRaceParticipant? winner)
    {
        var titleProgress = SmoothStep(transitionProgress / 0.76);
        var winnerProgress = SmoothStep((transitionProgress - 0.24) / 0.76);
        var champagne = BrushOf(0xF1, 0xC9, 0x72);
        var finishRuleThickness = Math.Max(1, height * 0.018);

        dc.PushOpacity(titleProgress);
        dc.DrawRectangle(
            BrushWithOpacity(champagne, 0.70),
            null,
            new Rect(width * 0.004, height - finishRuleThickness, width * 0.992, finishRuleThickness));

        var separatorPen = new Pen(
            BrushWithOpacity(champagne, 0.84),
            Math.Max(1, height * 0.021));
        dc.DrawLine(
            separatorPen,
            new Point(width * 0.174, height * 0.18),
            new Point(width * 0.158, height * 0.82));

        var titleBounds = new Rect(width * 0.182, 0, width * 0.45, height);
        var titleSize = Math.Max(15, height * 0.39);
        DrawRaceFinishTitle(
            dc,
            "RACE COMPLETE",
            new Rect(
                titleBounds.X + Math.Max(0.5, height * 0.012),
                titleBounds.Y + Math.Max(0.7, height * 0.018),
                titleBounds.Width,
                titleBounds.Height),
            titleSize,
            BrushOf(0x00, 0x00, 0x00, 0.64));
        DrawRaceFinishTitle(dc, "RACE COMPLETE", titleBounds, titleSize, RaceChequeredGoldAccent);
        dc.Pop();

        if (winnerProgress <= 0) return;

        var slideOffset = (1 - winnerProgress) * width * 0.018;
        var blockLeft = width * 0.695 + slideOffset;
        dc.PushOpacity(winnerProgress);
        dc.DrawLine(
            new Pen(BrushOf(0xB9, 0xC1, 0xCA, 0.44), Math.Max(0.8, height * 0.014)),
            new Point(blockLeft, height * 0.18),
            new Point(blockLeft, height * 0.82));
        dc.DrawRectangle(
            winner is null ? BrushOf(0x20, 0xD9, 0xEF) : RaceThemeBrush(winner.ThemeColor),
            null,
            new Rect(
                blockLeft + width * 0.024,
                height * 0.25,
                Math.Max(2, width * 0.006),
                height * 0.50));

        var winnerTextLeft = blockLeft + width * 0.052;
        var winnerTextWidth = Math.Max(1, width * 0.95 - winnerTextLeft);
        RaceBoundedText(
            dc,
            "WINNER",
            new Rect(winnerTextLeft, height * 0.14, winnerTextWidth, height * 0.32),
            Math.Max(8, height * 0.17),
            RaceSecondary,
            true);
        RaceBoundedText(
            dc,
            string.IsNullOrWhiteSpace(winner?.DisplayName) ? "—" : winner.DisplayName,
            new Rect(winnerTextLeft, height * 0.42, winnerTextWidth, height * 0.44),
            Math.Max(11, height * 0.25),
            White,
            true);
        dc.Pop();
    }

    private void DrawRaceMarshalPanels(
        DrawingContext dc,
        RaceHeaderSignal signal,
        Rect bounds,
        double transitionProgress)
    {
        var panelCount = signal == RaceHeaderSignal.DoubleYellow ? 2 : 1;
        var gap = bounds.Width * 0.06;
        var panelWidth = (bounds.Width - gap * (panelCount - 1)) / panelCount;
        var lit = RaceHeaderSignalColor(signal);
        var pulse = 0.76 + 0.24 * Math.Sin(clock.Elapsed.TotalSeconds * Math.PI * 5);
        for (var panel = 0; panel < panelCount; panel++)
        {
            var left = bounds.Left + panel * (panelWidth + gap) + (1 - transitionProgress) * bounds.Width * 0.22;
            var panelRect = new Rect(left, bounds.Top, panelWidth, bounds.Height);
            dc.DrawRoundedRectangle(
                BrushOf(0x04, 0x07, 0x0B, 0.98),
                new Pen(BrushWithOpacity(lit, 0.60), 1),
                panelRect,
                3,
                3);
            var dimension = signal == RaceHeaderSignal.Chequered ? 4 : 3;
            var cell = Math.Min(panelRect.Width, panelRect.Height) / (dimension + 0.85);
            var gridWidth = cell * dimension;
            var startX = panelRect.Left + (panelRect.Width - gridWidth) / 2 + cell / 2;
            var startY = panelRect.Top + (panelRect.Height - gridWidth) / 2 + cell / 2;
            for (var row = 0; row < dimension; row++)
            for (var column = 0; column < dimension; column++)
            {
                var center = new Point(startX + column * cell, startY + row * cell);
                if (signal == RaceHeaderSignal.Chequered)
                {
                    dc.DrawRectangle(
                        (row + column) % 2 == 0 ? White : BrushOf(0x2A, 0x30, 0x39),
                        null,
                        new Rect(center.X - cell * 0.34, center.Y - cell * 0.34, cell * 0.68, cell * 0.68));
                }
                else
                {
                    var phase = (row + column + panel) % 2 == 0 ? pulse : 1.72 - pulse;
                    dc.DrawEllipse(
                        BrushWithOpacity(lit, (0.60 + 0.34 * phase) * transitionProgress),
                        null,
                        center,
                        cell * 0.26,
                        cell * 0.26);
                }
            }
        }
    }

    private void DrawRaceOrganizerLogo(
        DrawingContext dc,
        EstateRaceOrganizerLogo? logo,
        Rect bounds)
    {
        var requestedHash = logo?.Sha256 ?? "__lazyforza_default__";
        if (!string.Equals(requestedHash, raceLogoHash, StringComparison.Ordinal))
        {
            raceLogoHash = requestedHash;
            raceLogoImage = null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (logo is null)
                    bitmap.UriSource = new Uri("pack://application:,,,/Assets/LazyForza.png", UriKind.Absolute);
                else
                    bitmap.StreamSource = new MemoryStream(logo.Bytes, writable: false);
                bitmap.EndInit();
                bitmap.Freeze();
                raceLogoImage = logo is null
                    ? MakeDarkLogoBackgroundTransparent(bitmap)
                    : bitmap;
            }
            catch
            {
                raceLogoImage = null;
            }
        }

        if (raceLogoImage is not null && raceLogoImage.Width > 0 && raceLogoImage.Height > 0)
        {
            var scale = Math.Min(bounds.Width / raceLogoImage.Width, bounds.Height / raceLogoImage.Height);
            var target = new Rect(
                bounds.Left + (bounds.Width - raceLogoImage.Width * scale) / 2,
                bounds.Top + (bounds.Height - raceLogoImage.Height * scale) / 2,
                raceLogoImage.Width * scale,
                raceLogoImage.Height * scale);
            dc.DrawImage(raceLogoImage, target);
            return;
        }

        RaceTitleText(dc, "LF", bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2,
            Math.Max(12, bounds.Height * 0.58), White, TextAlignment.Center);
    }

    private static BitmapSource MakeDarkLogoBackgroundTransparent(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var maximum = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
            var opacity = Math.Clamp((maximum - 58) / 66d, 0, 1);
            pixels[offset + 3] = (byte)Math.Round(pixels[offset + 3] * opacity);
        }
        var transparent = BitmapSource.Create(
            width,
            height,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        transparent.Freeze();
        return transparent;
    }

    private void DrawRaceTrackMap(
        DrawingContext dc,
        EstateRaceHudState state,
        EstateRaceSession session)
    {
        var size = Math.Min(ActualWidth * 0.19, ActualHeight * 0.28);
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.91),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.32), 1),
            new Rect(0, 0, size, size),
            9,
            9);
        var map = new Rect(size * 0.10, size * 0.12, size * 0.80, size * 0.76);
        if (state.TrackOutline.Count >= 2)
        {
            var geometry = RaceMapGeometry(state.TrackOutline, map);
            dc.DrawGeometry(null,
                new Pen(BrushOf(0x00, 0x00, 0x00, 0.70), size * 0.038), geometry);
            dc.DrawGeometry(null,
                new Pen(BrushOf(0xC8, 0xD2, 0xDC, 0.82), size * 0.017), geometry);

            var targetFlagVisual = RaceMapFlagVisualKey(session);
            if (!string.Equals(targetFlagVisual, raceMapFlagVisualKey, StringComparison.Ordinal))
            {
                previousRaceMapFlagVisualKey = raceMapFlagVisualKey;
                raceMapFlagVisualKey = targetFlagVisual;
                raceMapFlagTransitionStartedSeconds = estateRaceAnimationNowSeconds;
            }
            var flagProgress = estateRaceReduceMotion || layoutPreview
                ? 1
                : SmoothStep((estateRaceAnimationNowSeconds - raceMapFlagTransitionStartedSeconds) / 0.18);
            if (previousRaceMapFlagVisualKey is not null && flagProgress < 1)
            {
                raceMapFlagAnimation = true;
                DrawRaceMapFlagOverlay(
                    dc,
                    previousRaceMapFlagVisualKey,
                    geometry,
                    state.TrackSectors ?? [],
                    map,
                    size,
                    1 - flagProgress);
            }
            DrawRaceMapFlagOverlay(
                dc,
                raceMapFlagVisualKey ?? "normal",
                geometry,
                state.TrackSectors ?? [],
                map,
                size,
                flagProgress);
            if (flagProgress >= 1) previousRaceMapFlagVisualKey = null;
        }
        if (state.PitLaneOutline is { Count: >= 2 } pitLane)
        {
            var geometry = RaceMapGeometry(pitLane, map);
            dc.DrawGeometry(null,
                new Pen(BrushOf(0x00, 0x00, 0x00, 0.82), size * 0.020), geometry);
            dc.DrawGeometry(null,
                new Pen(BrushOf(0xF4, 0xC5, 0x24, 0.96), size * 0.006), geometry);
        }
        if (state.StartFinishGate is { } startFinish)
        {
            var left = new Point(
                map.Left + startFinish.Left.X * map.Width,
                map.Top + startFinish.Left.Y * map.Height);
            var right = new Point(
                map.Left + startFinish.Right.X * map.Width,
                map.Top + startFinish.Right.Y * map.Height);
            var dx = right.X - left.X;
            var dy = right.Y - left.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length >= 2)
                DrawRaceStartFinishMarker(dc, left, right, size);
        }
        var connectedParticipants = session.Participants.Where(item => item.IsConnected).ToArray();
        var mapParticipantIds = connectedParticipants.Select(item => item.Id).ToHashSet();
        foreach (var staleId in raceMapPointRuntime.Keys.Where(id => !mapParticipantIds.Contains(id)).ToArray())
            raceMapPointRuntime.Remove(staleId);
        foreach (var participant in connectedParticipants)
        {
            var targetMapPoint = new Point(
                Math.Clamp(participant.MapX, 0, 1),
                Math.Clamp(participant.MapY, 0, 1));
            if (!raceMapPointRuntime.TryGetValue(participant.Id, out var pointRuntime))
            {
                pointRuntime = new RaceMapPointRuntime(targetMapPoint, estateRaceAnimationNowSeconds);
                raceMapPointRuntime[participant.Id] = pointRuntime;
            }
            var visualPoint = pointRuntime.Update(
                targetMapPoint,
                estateRaceAnimationNowSeconds,
                estateRaceReduceMotion || layoutPreview);
            raceMapPointAnimation |= visualPoint.IsAnimating;
            var point = new Point(
                map.Left + visualPoint.Point.X * map.Width,
                map.Top + visualPoint.Point.Y * map.Height);
            var local = participant.Id == state.LocalParticipantId;
            if (local)
                dc.DrawEllipse(BrushOf(0x38, 0xD5, 0xE8, 0.18), null,
                    point, size * 0.050, size * 0.050);
            dc.DrawEllipse(
                RaceThemeBrush(participant.ThemeColor),
                new Pen(local ? White : BrushOf(0x08, 0x0B, 0x11), size * 0.007),
                point,
                local ? Math.Max(7, size * 0.032) : Math.Max(5.5, size * 0.024),
                local ? Math.Max(7, size * 0.032) : Math.Max(5.5, size * 0.024));
            RaceText(dc, participant.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                point.X, point.Y, Math.Max(8.5, size * 0.035),
                BrushOf(0x05, 0x08, 0x0C), TextAlignment.Center, true);
        }
    }

    private static string RaceMapFlagVisualKey(EstateRaceSession session)
    {
        if (session.Flag == RaceControlFlag.Red) return "red";
        var zones = session.YellowZones ?? [];
        if (session.Flag == RaceControlFlag.Yellow &&
            (zones.Count == 0 || zones.Any(zone => zone.SectorIndex is null)))
            return "yellow:all";
        var sectors = zones
            .Where(zone => zone.SectorIndex is not null)
            .Select(zone => zone.SectorIndex!.Value)
            .Distinct()
            .Order()
            .ToArray();
        return sectors.Length == 0 ? "normal" : $"yellow:{string.Join(',', sectors)}";
    }

    private static void DrawRaceMapFlagOverlay(
        DrawingContext dc,
        string visualKey,
        Geometry fullTrack,
        IReadOnlyList<EstateRaceMapSector> trackSectors,
        Rect map,
        double mapSize,
        double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0.001 || visualKey == "normal") return;
        if (visualKey == "red")
        {
            dc.DrawGeometry(null,
                new Pen(BrushOf(0xFF, 0x28, 0x3F, 0.98 * opacity), mapSize * 0.019), fullTrack);
            return;
        }
        if (visualKey == "yellow:all")
        {
            dc.DrawGeometry(null,
                new Pen(BrushOf(0xFF, 0xCB, 0x21, 0.98 * opacity), mapSize * 0.019), fullTrack);
            return;
        }

        var separator = visualKey.IndexOf(':');
        if (separator < 0) return;
        var yellowSectors = visualKey[(separator + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var sector) ? sector : int.MinValue)
            .ToHashSet();
        foreach (var sector in trackSectors)
        {
            if (!yellowSectors.Contains(sector.SectorIndex) || sector.Points.Count < 2) continue;
            dc.DrawGeometry(null,
                new Pen(BrushOf(0xFF, 0xCB, 0x21, 0.98 * opacity), mapSize * 0.019),
                RaceMapGeometry(sector.Points, map));
        }
    }

    private static void DrawRaceStartFinishMarker(
        DrawingContext dc,
        Point left,
        Point right,
        double mapSize)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        var sourceLength = Math.Sqrt(dx * dx + dy * dy);
        if (sourceLength < 0.1) return;
        var tangentX = dx / sourceLength;
        var tangentY = dy / sourceLength;
        var normalX = -tangentY;
        var normalY = tangentX;
        var center = new Point((left.X + right.X) / 2, (left.Y + right.Y) / 2);
        var length = Math.Clamp(sourceLength, mapSize * 0.026, mapSize * 0.055);
        var halfThickness = Math.Clamp(length * 0.18, mapSize * 0.006, mapSize * 0.011);
        const int columns = 4;
        const int rows = 2;

        Point CellPoint(double along, double across) => new(
            center.X + tangentX * along + normalX * across,
            center.Y + tangentY * along + normalY * across);

        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var along0 = -length / 2 + length * column / columns;
            var along1 = -length / 2 + length * (column + 1) / columns;
            var across0 = -halfThickness + 2 * halfThickness * row / rows;
            var across1 = -halfThickness + 2 * halfThickness * (row + 1) / rows;
            var cell = new StreamGeometry();
            using (var context = cell.Open())
            {
                context.BeginFigure(CellPoint(along0, across0), true, true);
                context.PolyLineTo([
                    CellPoint(along1, across0),
                    CellPoint(along1, across1),
                    CellPoint(along0, across1)
                ], true, false);
            }
            cell.Freeze();
            dc.DrawGeometry(
                (row + column) % 2 == 0 ? White : BrushOf(0x12, 0x17, 0x1E),
                null,
                cell);
        }

        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(CellPoint(-length / 2, -halfThickness), true, true);
            context.PolyLineTo([
                CellPoint(length / 2, -halfThickness),
                CellPoint(length / 2, halfThickness),
                CellPoint(-length / 2, halfThickness)
            ], true, false);
        }
        outline.Freeze();
        dc.DrawGeometry(null, new Pen(BrushOf(0x00, 0x00, 0x00, 0.90), Math.Max(0.8, mapSize * 0.003)), outline);
    }

    private void DrawRaceFastestLapClock(DrawingContext dc, Point center, double rowHeight)
    {
        var radius = Math.Max(4.5, rowHeight * 0.12);
        var purple = BrushOf(0xB4, 0x63, 0xFF);
        var darkPurple = BrushOf(0x25, 0x0D, 0x3D, 0.96);
        dc.DrawEllipse(darkPurple, new Pen(purple, Math.Max(1.2, rowHeight * 0.035)),
            center, radius, radius);
        var handPen = new Pen(White, Math.Max(1, rowHeight * 0.025))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dc.DrawLine(handPen, center, new Point(center.X, center.Y - radius * 0.50));
        dc.DrawLine(handPen, center, new Point(center.X + radius * 0.42, center.Y + radius * 0.18));
        dc.DrawRoundedRectangle(purple, null,
            new Rect(center.X - radius * 0.28, center.Y - radius * 1.34,
                radius * 0.56, radius * 0.30), radius * 0.10, radius * 0.10);
    }

    private static StreamGeometry RaceMapGeometry(
        IReadOnlyList<EstateRaceMapPoint> points,
        Rect map)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = points[0];
            context.BeginFigure(
                new Point(map.Left + first.X * map.Width, map.Top + first.Y * map.Height),
                false,
                false);
            context.PolyLineTo(points.Skip(1)
                .Select(point => new Point(
                    map.Left + point.X * map.Width,
                    map.Top + point.Y * map.Height))
                .ToArray(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private PitHudSnapshot UpdatePitHud(
        EstateRaceSession session,
        Guid? localParticipantId,
        EstatePitServiceState localPitService,
        DateTimeOffset now,
        DateTimeOffset estimatedServerNow)
    {
        if (session.Phase != RaceSessionPhase.Race)
        {
            pitHudRuntime.Clear();
            return PitHudSnapshot.Empty;
        }

        var activeParticipantCount = EstateRacePitHudTiming.ActiveParticipantCount(session.Participants);
        var connectedParticipantIds = session.Participants
            .Where(participant => participant.IsConnected)
            .Select(participant => participant.Id)
            .ToHashSet();
        foreach (var disconnectedId in pitHudRuntime.Keys
                     .Where(id => !connectedParticipantIds.Contains(id))
                     .ToArray())
            pitHudRuntime.Remove(disconnectedId);

        foreach (var participant in session.Participants)
        {
            if (!participant.IsConnected) continue;
            var isLocal = participant.Id == localParticipantId;
            var inServiceZone = isLocal ? localPitService.IsInServiceZone : participant.IsInServiceZone;
            var inPit = (isLocal ? localPitService.IsInPitLane : participant.IsInPitLane) || inServiceZone;
            if (!pitHudRuntime.TryGetValue(participant.Id, out var runtime))
            {
                if (!inPit) continue;
                runtime = new PitHudRuntime { EnteredAt = now, WasInPit = true };
                pitHudRuntime[participant.Id] = runtime;
            }

            if (inPit && !runtime.WasInPit)
            {
                runtime.EnteredAt = now;
                runtime.ExitedAt = null;
                runtime.ServiceCompletedAt = null;
                runtime.FrozenServiceSeconds = 0;
            }
            var projectedPitLaneSeconds = EstateRacePitHudTiming.ProjectElapsedSeconds(
                participant.PitLaneElapsedSeconds,
                participant.LastSeenAt,
                estimatedServerNow,
                inPit);
            if (inPit && projectedPitLaneSeconds <= 0)
                projectedPitLaneSeconds = Math.Max(0, (now - runtime.EnteredAt).TotalSeconds);
            var serviceCounting = isLocal
                ? localPitService.IsCounting
                : inServiceZone && participant.PitServiceElapsedSeconds > 0 &&
                  participant.SpeedKph <= 1.5 && !participant.IsServingTimePenalty;
            var serviceCompleted = isLocal
                ? localPitService.RequirementMet
                : participant.PitServiceRequirementMet;
            var projectedServiceSeconds = isLocal
                ? EffectivePitServiceElapsed(localPitService, now)
                : EstateRacePitHudTiming.ProjectElapsedSeconds(
                    participant.PitServiceElapsedSeconds,
                    participant.LastSeenAt,
                    estimatedServerNow,
                    serviceCounting);
            if (serviceCounting)
            {
                runtime.FrozenServiceSeconds = Math.Max(
                    runtime.FrozenServiceSeconds,
                    projectedServiceSeconds);
            }
            if (serviceCompleted && !runtime.ServiceCompleted)
            {
                runtime.ServiceCompletedAt = now;
                runtime.FrozenServiceSeconds = Math.Max(
                    runtime.FrozenServiceSeconds,
                    projectedServiceSeconds);
            }
            if (!inPit && runtime.WasInPit)
            {
                runtime.ExitedAt = now;
                runtime.FrozenPitLaneSeconds = projectedPitLaneSeconds > 0
                    ? projectedPitLaneSeconds
                    : Math.Max(0, (now - runtime.EnteredAt).TotalSeconds);
            }
            runtime.WasInPit = inPit;
            runtime.Position = participant.Position;
            runtime.DisplayName = participant.DisplayName;
            runtime.ThemeColor = participant.ThemeColor;
            runtime.TeamName = participant.TeamName;
            runtime.IsInServiceZone = inServiceZone;
            runtime.ServiceElapsedSeconds = projectedServiceSeconds;
            runtime.WasServiceCounting = serviceCounting;
            runtime.ServiceCompleted = serviceCompleted;
            runtime.ServiceRequiredSeconds = isLocal ? localPitService.RequiredSeconds : 0;
            runtime.ServicePaused = isLocal &&
                                    localPitService.ProgressState == EstatePitServiceProgressState.MovementGrace;
            runtime.PitLaneElapsedSeconds = projectedPitLaneSeconds;
            runtime.IsServingPenalty = participant.IsServingTimePenalty;
            runtime.PenaltyServiceCompleted = participant.PenaltyServiceCompleted;
            runtime.PenaltyElapsedSeconds = participant.PenaltyServiceElapsedSeconds;
            runtime.PenaltyRequiredSeconds = participant.PenaltyServiceRequiredSeconds;
        }

        foreach (var id in pitHudRuntime
                     .Where(pair => !pair.Value.WasInPit &&
                                    (pair.Value.ExitedAt is null || now - pair.Value.ExitedAt > TimeSpan.FromSeconds(3)))
                     .Select(pair => pair.Key)
                     .ToArray())
            pitHudRuntime.Remove(id);

        var localPosition = session.Participants.FirstOrDefault(item => item.Id == localParticipantId)?.Position ?? 1;
        var entries = pitHudRuntime
            .Select(pair =>
            {
                var runtime = pair.Value;
                var showPenalty = runtime.IsServingPenalty || runtime.PenaltyServiceCompleted;
                var serviceHold = runtime.ServiceCompletedAt is DateTimeOffset completedAt &&
                                  now - completedAt <= TimeSpan.FromSeconds(3);
                var showService = !showPenalty && (runtime.IsInServiceZone || serviceHold);
                var serviceState = runtime.ServiceCompleted || serviceHold
                    ? PitHudServiceState.Completed
                    : runtime.WasServiceCounting
                        ? PitHudServiceState.Counting
                        : runtime.ServicePaused
                            ? PitHudServiceState.Paused
                            : runtime.IsInServiceZone
                                ? PitHudServiceState.WaitingForStop
                                : PitHudServiceState.None;
                var seconds = showPenalty
                    ? runtime.PenaltyElapsedSeconds
                    : showService
                    ? runtime.WasServiceCounting
                        ? runtime.ServiceElapsedSeconds
                        : runtime.FrozenServiceSeconds
                    : runtime.WasInPit
                        ? runtime.PitLaneElapsedSeconds
                        : runtime.FrozenPitLaneSeconds;
                return new PitHudView(
                    pair.Key,
                    runtime.Position,
                    runtime.DisplayName,
                    runtime.ThemeColor,
                    runtime.TeamName,
                    showService,
                    serviceHold && showService,
                    seconds,
                    runtime.WasInPit,
                    showPenalty,
                    runtime.PenaltyServiceCompleted,
                    runtime.PenaltyRequiredSeconds,
                    serviceState,
                    runtime.ServiceRequiredSeconds);
            })
            .OrderByDescending(item => item.ParticipantId == localParticipantId)
            .ThenBy(item => Math.Abs(item.Position - localPosition))
            .ThenBy(item => item.Position)
            .Take(2)
            .ToArray();
        return new PitHudSnapshot(entries, activeParticipantCount);
    }

    private void DrawRacePitStopInfo(DrawingContext dc, PitHudSnapshot snapshot)
    {
        var entries = snapshot.Entries;
        var width = ActualWidth * 0.215;
        var headerHeight = ActualHeight * 0.041;
        var rowHeight = ActualHeight * 0.0665;
        var height = headerHeight + rowHeight * entries.Count;
        var border = BrushOf(0x8B, 0x9A, 0xAA, 0.46);
        var yellow = BrushOf(0xF4, 0xC5, 0x24);
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.97),
            new Pen(border, 1),
            new Rect(0, 0, width, height), 9, 9);
        dc.DrawRoundedRectangle(yellow, null,
            new Rect(0, headerHeight * 0.16, Math.Max(4, width * 0.010), headerHeight * 0.68), 2, 2);
        dc.DrawLine(new Pen(BrushWithOpacity(yellow, 0.86), 1),
            new Point(0, headerHeight - 1),
            new Point(width, headerHeight - 1));
        RaceText(dc, "PIT STOP", width * 0.052, headerHeight * 0.57,
            Math.Max(14, headerHeight * 0.42), White, TextAlignment.Left, true);
        var participantText = snapshot.ActiveParticipantCount == 1
            ? "1 PLAYER"
            : $"{snapshot.ActiveParticipantCount} PLAYERS";
        RaceText(dc, participantText, width * 0.948, headerHeight * 0.57,
            Math.Max(10, headerHeight * 0.27), RaceSecondary, TextAlignment.Right, true);

        var initialPitRows = pitStopRowRuntime.Count == 0;
        var visiblePitIds = entries.Select(item => item.ParticipantId).ToHashSet();
        foreach (var staleId in pitStopRowRuntime.Keys.Where(id => !visiblePitIds.Contains(id)).ToArray())
            pitStopRowRuntime.Remove(staleId);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!pitStopRowRuntime.TryGetValue(entry.ParticipantId, out var rowRuntime))
            {
                rowRuntime = new AnimatedRowRuntime(
                    index + (initialPitRows ? 0 : 0.18),
                    initialPitRows ? 1 : 0,
                    estateRaceAnimationNowSeconds);
                pitStopRowRuntime[entry.ParticipantId] = rowRuntime;
            }
            var rowVisual = rowRuntime.Update(
                index,
                estateRaceAnimationNowSeconds,
                estateRaceReduceMotion,
                0.20,
                0.18);
            pitStopRowAnimation |= rowVisual.IsAnimating;
            var top = headerHeight + rowVisual.Position * rowHeight;
            dc.PushOpacity(rowVisual.Opacity);
            var theme = RaceThemeBrush(entry.ThemeColor);
            var card = new Rect(
                width * 0.018,
                top + rowHeight * 0.07,
                width * 0.964,
                rowHeight * 0.86);
            dc.DrawRoundedRectangle(
                index == 0 ? BrushOf(0x0C, 0x18, 0x24, 0.91) : BrushOf(0x0B, 0x0F, 0x15, 0.94),
                new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.20), 1),
                card,
                6,
                6);
            dc.DrawRoundedRectangle(theme, null,
                new Rect(card.Left, card.Top, Math.Max(4, width * 0.010), card.Height), 5, 2);
            dc.DrawLine(new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.46), 1),
                new Point(width * 0.145, top + rowHeight * 0.20),
                new Point(width * 0.145, top + rowHeight * 0.80));
            RaceText(dc, entry.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                width * 0.090, top + rowHeight * 0.50, Math.Max(18, rowHeight * 0.42), White,
                TextAlignment.Center, true);
            const double informationLeft = 0.175;
            RaceBoundedText(dc, entry.DisplayName,
                new Rect(width * informationLeft, top + rowHeight * 0.11, width * 0.42, rowHeight * 0.38),
                Math.Max(15, rowHeight * 0.265), White, true);
            var metadataTop = top + rowHeight * 0.61;
            var hasTeam = !string.IsNullOrWhiteSpace(entry.TeamName);
            if (hasTeam)
                RaceBoundedText(dc, entry.TeamName!.ToUpperInvariant(),
                    new Rect(width * informationLeft, metadataTop, width * 0.25, rowHeight * 0.29),
                    Math.Max(9, rowHeight * 0.125), BrushWithOpacity(theme, 0.96), true);
            var modeColor = entry.IsPenalty
                ? entry.PenaltyCompleted ? BrushOf(0x4D, 0xD8, 0x91) : BrushOf(0xFF, 0x45, 0x5F)
                : entry.ServiceState == PitHudServiceState.Completed ? BrushOf(0x4D, 0xD8, 0x91)
                : entry.IsService ? BrushOf(0x20, 0xD9, 0xEF) : BrushOf(0xA7, 0xB2, 0xBF);
            var modeText = entry.IsPenalty
                ? entry.PenaltyCompleted ? "PENALTY SERVED" : "PENALTY"
                : entry.ServiceState switch
                {
                    PitHudServiceState.Completed => "TYRE STOP OK",
                    PitHudServiceState.Paused => "HOLD STILL",
                    PitHudServiceState.WaitingForStop => "STOP CAR",
                    PitHudServiceState.Counting => "TYRE STOP",
                    _ => "PIT LANE"
                };
            var modeBounds = new Rect(
                width * (hasTeam ? 0.435 : informationLeft),
                metadataTop - rowHeight * 0.01,
                width * (hasTeam ? 0.17 : 0.22),
                rowHeight * 0.30);
            dc.DrawRoundedRectangle(
                BrushWithOpacity(modeColor, 0.10),
                new Pen(BrushWithOpacity(modeColor, 0.28), 1),
                modeBounds,
                3,
                3);
            RaceBoundedText(dc, modeText, modeBounds,
                Math.Max(9, rowHeight * 0.12), modeColor, true);
            var secondsText = entry.IsPenalty && entry.PenaltyRequiredSeconds > 0
                ? $"{entry.Seconds:0.0}/{entry.PenaltyRequiredSeconds:0.#}"
                : entry.IsService && entry.ServiceRequiredSeconds > 0
                    ? $"{Math.Max(0, entry.Seconds):0.0}/{entry.ServiceRequiredSeconds:0.#}"
                    : $"{Math.Max(0, entry.Seconds):0.000}";
            var timeColor = entry.IsPenalty
                ? entry.PenaltyCompleted ? BrushOf(0x4D, 0xD8, 0x91) : BrushOf(0xFF, 0xF4, 0xF5)
                : entry.ServiceState == PitHudServiceState.Completed ? BrushOf(0x4D, 0xD8, 0x91)
                : entry.IsService ? BrushOf(0x20, 0xD9, 0xEF) : White;
            RaceText(dc, secondsText, width * 0.945, top + rowHeight * 0.405,
                Math.Max(23, rowHeight * 0.385), timeColor, TextAlignment.Right, true);
            var timerLabel = entry.IsPenalty
                ? "PENALTY TIME"
                : entry.ServiceState switch
                {
                    PitHudServiceState.Completed => "SERVICE COMPLETE",
                    PitHudServiceState.Paused => "TIMER PAUSED",
                    PitHudServiceState.WaitingForStop => "STOP TO START",
                    PitHudServiceState.Counting => "TYRE TIME",
                    _ => "TOTAL TIME"
                };
            RaceText(dc, timerLabel, width * 0.945, metadataTop + rowHeight * 0.105,
                Math.Max(9, rowHeight * 0.115), BrushWithOpacity(timeColor, 0.82), TextAlignment.Right, true);
            if (entry.IsService)
            {
                var segmentWidth = width * 0.038;
                var segmentGap = width * 0.010;
                var segmentCount = 5;
                var totalWidth = segmentWidth * segmentCount + segmentGap * (segmentCount - 1);
                var left = width * 0.945 - totalWidth;
                for (var segment = 0; segment < segmentCount; segment++)
                    dc.DrawRoundedRectangle(
                        BrushWithOpacity(timeColor, entry.ServiceCompleted ? 0.90 : 0.64),
                        null,
                        new Rect(left + segment * (segmentWidth + segmentGap),
                            top + rowHeight * 0.875,
                            segmentWidth,
                            Math.Max(2, rowHeight * 0.035)),
                        2,
                        2);
            }
            dc.Pop();
        }
    }

    private void DrawRacePitLimiter(DrawingContext dc, EstatePitServiceState pit)
    {
        var size = ActualHeight * 0.11;
        var center = new Point(size / 2, size / 2);
        var radius = size * 0.36;
        var over = pit.IsSpeeding;
        if (over)
        {
            var pulse = estateRaceReduceMotion
                ? 0.5
                : 0.5 + 0.5 * Math.Sin(estateRaceAnimationNowSeconds * Math.PI * 1.4);
            dc.DrawEllipse(
                BrushOf(0xFF, 0x2F, 0x46, 0.10 + 0.12 * pulse),
                new Pen(BrushOf(0xFF, 0x2F, 0x46, 0.34 + 0.28 * pulse), Math.Max(1, size * 0.018)),
                center,
                size * (0.46 + 0.035 * pulse),
                size * (0.46 + 0.035 * pulse));
        }
        dc.DrawEllipse(BrushOf(0xF4, 0xF6, 0xF8, 0.97),
            new Pen(over ? BrushOf(0xFF, 0x2F, 0x46) : BrushOf(0xE2, 0x18, 0x2F), size * 0.075),
            center, radius, radius);
        RaceText(dc, Math.Round(pit.SpeedLimitKph).ToString(System.Globalization.CultureInfo.InvariantCulture),
            center.X, center.Y, size * 0.29, BrushOf(0x08, 0x0A, 0x0D), TextAlignment.Center, true);
        var indicator = over ? BrushOf(0xFF, 0x2F, 0x46) : pit.IsInPitLane || pit.IsOnPitRoute
            ? BrushOf(0x4D, 0xD8, 0x91) : BrushOf(0xF4, 0xC5, 0x24);
        for (var index = 0; index < 3; index++)
            dc.DrawRoundedRectangle(indicator, null,
                new Rect(size * (0.28 + index * 0.17), size * 0.91, size * 0.11, size * 0.045), 2, 2);
    }

    private static string? PendingPenaltyBadge(EstateRaceParticipant participant)
    {
        if (participant.PendingTimePenaltySeconds > 0)
            return $"+{participant.PendingTimePenaltySeconds:0.#}s";
        if (participant.HasPendingDriveThrough) return "DT";
        var postRaceAdjustment = participant.Penalties
            .Where(item => !item.IsRevoked && !item.IsServed && item.IsPostRaceAdjustment)
            .Sum(item => item.ValueSeconds ?? 0);
        if (postRaceAdjustment > 0) return $"+{postRaceAdjustment:0.#}s";
        return participant.Penalties.Any(item =>
            !item.IsRevoked && !item.IsServed && item.Kind == RacePenaltyKind.StopAndGo)
            ? "S&G"
            : null;
    }

    private void DrawLeaderboardPenaltyBadge(
        DrawingContext dc,
        string text,
        double width,
        double top,
        double rowHeight)
    {
        var bounds = new Rect(
            width * 0.845,
            top + rowHeight * 0.15,
            width * 0.125,
            rowHeight * 0.70);
        dc.DrawRoundedRectangle(
            BrushOf(0x00, 0x00, 0x00, 0.34),
            null,
            new Rect(bounds.Left + 1.5, bounds.Top + 1.5, bounds.Width, bounds.Height),
            4,
            4);
        dc.DrawRoundedRectangle(
            BrushOf(0x16, 0x1B, 0x23, 0.98),
            new Pen(BrushOf(0xFF, 0x45, 0x5F, 0.82), 1),
            bounds,
            4,
            4);
        dc.DrawRoundedRectangle(
            BrushOf(0xFF, 0x45, 0x5F),
            null,
            new Rect(bounds.Left, bounds.Top, Math.Max(3, bounds.Width * 0.08), bounds.Height),
            3,
            3);
        dc.DrawRectangle(
            BrushOf(0xFF, 0x45, 0x5F, 0.24),
            null,
            new Rect(bounds.Left + bounds.Width * 0.08, bounds.Top, bounds.Width * 0.92, bounds.Height * 0.14));
        RaceText(dc, text, bounds.Left + bounds.Width * 0.52, bounds.Top + bounds.Height * 0.53,
            Math.Max(11, rowHeight * 0.27), White, TextAlignment.Center, true);
    }

    private void DrawLeaderboardStatusBadges(
        DrawingContext dc,
        double width,
        double top,
        double rowHeight,
        bool underInvestigation,
        bool inPit,
        bool finished,
        bool muted)
    {
        var cursor = width * 0.605;
        var gap = Math.Max(2, rowHeight * 0.07);
        dc.PushOpacity(muted ? 0.58 : 1);
        if (finished)
        {
            var badgeWidth = Math.Max(14, rowHeight * 0.52);
            DrawLeaderboardFinishBadge(dc, cursor - badgeWidth, top, badgeWidth, rowHeight);
            cursor -= badgeWidth + gap;
        }
        if (inPit)
        {
            var badgeSize = Math.Max(12, rowHeight * 0.40);
            DrawLeaderboardPitBadge(dc, cursor - badgeSize, top, badgeSize, rowHeight);
            cursor -= badgeSize + gap;
        }
        if (underInvestigation)
        {
            var badgeSize = Math.Max(12, rowHeight * 0.38);
            DrawLeaderboardInvestigationBadge(dc, cursor - badgeSize, top, badgeSize, rowHeight);
        }
        dc.Pop();
    }

    private void DrawLeaderboardInvestigationBadge(
        DrawingContext dc,
        double left,
        double top,
        double size,
        double rowHeight)
    {
        var center = new Point(left + size * 0.5, top + rowHeight * 0.5);
        var radius = size * 0.5;
        dc.DrawEllipse(
            BrushOf(0xFF, 0xCF, 0x28, 0.14),
            new Pen(BrushOf(0xFF, 0xCF, 0x28, 0.92), 1),
            center,
            radius,
            radius);
        RaceText(dc, "!", center.X, center.Y - rowHeight * 0.01,
            Math.Max(11, rowHeight * 0.27), BrushOf(0xFF, 0xD8, 0x47), TextAlignment.Center, true);
    }

    private void DrawLeaderboardPitBadge(
        DrawingContext dc,
        double left,
        double top,
        double size,
        double rowHeight)
    {
        var bounds = new Rect(left, top + (rowHeight - size) * 0.5, size, size);
        dc.DrawRoundedRectangle(
            BrushOf(0xFF, 0xC4, 0x4D, 0.14),
            new Pen(BrushOf(0xFF, 0xC4, 0x4D, 0.92), 1),
            bounds,
            2,
            2);
        RaceText(dc, "P", bounds.Left + bounds.Width * 0.5, bounds.Top + bounds.Height * 0.5,
            Math.Max(9, size * 0.64), BrushOf(0xFF, 0xCE, 0x69), TextAlignment.Center, true);
    }

    private static void DrawLeaderboardFinishBadge(
        DrawingContext dc,
        double left,
        double top,
        double width,
        double rowHeight)
    {
        var height = Math.Max(10, rowHeight * 0.34);
        var bounds = new Rect(left, top + (rowHeight - height) * 0.5, width, height);
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.96),
            new Pen(BrushOf(0xD7, 0xDC, 0xE2, 0.74), 1),
            bounds,
            2,
            2);
        const int columns = 4;
        const int rows = 2;
        var cellWidth = (bounds.Width - 4) / columns;
        var cellHeight = (bounds.Height - 4) / rows;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var light = (row + column) % 2 == 0;
            dc.DrawRectangle(
                light ? BrushOf(0xEC, 0xEF, 0xF2) : BrushOf(0x20, 0x25, 0x2C),
                null,
                new Rect(
                    bounds.Left + 2 + column * cellWidth,
                    bounds.Top + 2 + row * cellHeight,
                    cellWidth + 0.2,
                    cellHeight + 0.2));
        }
    }

    private void DrawRacePenaltyStatus(DrawingContext dc, EstateRaceParticipant participant)
    {
        var width = ActualWidth * 0.27;
        var height = ActualHeight * 0.105;
        var completed = participant.PenaltyServiceCompleted;
        var driveThrough = participant.HasPendingDriveThrough;
        var servingDriveThrough = participant.IsServingDriveThrough;
        var active = participant.IsServingTimePenalty;
        var postRaceAdjustment = participant.Penalties
            .Where(item => !item.IsRevoked && !item.IsServed && item.IsPostRaceAdjustment)
            .Sum(item => item.ValueSeconds ?? 0);
        var overdue = participant.DriveThroughOverdue && postRaceAdjustment > 0;
        var accent = completed
            ? BrushOf(0x4D, 0xD8, 0x91)
            : driveThrough || servingDriveThrough ? BrushOf(0xF4, 0xC5, 0x24) : BrushOf(0xFF, 0x45, 0x5F);
        dc.DrawRoundedRectangle(
            BrushOf(0x00, 0x00, 0x00, 0.34),
            null,
            new Rect(2, 3, width, height),
            9,
            9);
        dc.DrawRoundedRectangle(
            BrushOf(0x0B, 0x10, 0x17, 0.97),
            new Pen(BrushWithOpacity(accent, 0.62), 1),
            new Rect(0, 0, width, height),
            9,
            9);
        var entryProgress = RaceWidgetEntryProgress(EstateRaceHudWidgetKind.PenaltyStatus);
        dc.DrawRoundedRectangle(accent, null,
            new Rect(0, 0, width * entryProgress, Math.Max(3, height * 0.055)), 8, 8);
        var controlLabelBounds = new Rect(
            width * 0.045,
            height * 0.15,
            width * 0.265,
            height * 0.27);
        dc.DrawRoundedRectangle(
            BrushWithOpacity(accent, 0.12),
            new Pen(BrushWithOpacity(accent, 0.38), 1),
            controlLabelBounds,
            4,
            4);
        dc.DrawRectangle(
            accent,
            null,
            new Rect(
                controlLabelBounds.Left,
                controlLabelBounds.Top,
                Math.Max(2, width * 0.007),
                controlLabelBounds.Height));
        RaceBoundedText(
            dc,
            "RACE CONTROL",
            new Rect(
                controlLabelBounds.Left + width * 0.018,
                controlLabelBounds.Top,
                controlLabelBounds.Width - width * 0.028,
                controlLabelBounds.Height),
            Math.Max(9, height * 0.105),
            accent,
            true);
        var title = completed
            ? "PENALTY SERVED"
            : overdue ? "DRIVE THROUGH MISSED"
            : servingDriveThrough ? "DRIVE THROUGH"
            : driveThrough ? "DRIVE THROUGH"
            : active ? "PENALTY STOP" : "TIME PENALTY";
        var detail = completed
            ? "处罚执行完成"
            : overdue
                ? "未按期执行 · 已替换为完赛加时"
            : servingDriveThrough
                ? "保持行驶 · 不得停车或暂停"
            : driveThrough
                ? participant.DriveThroughLapsRemaining switch
                {
                    > 0 => $"还可跨越终点线 {participant.DriveThroughLapsRemaining} 次",
                    0 => "本圈必须进入维修区执行",
                    _ => "驶过维修区且不得停车"
                }
                : active
                    ? "保持静止 · 不要打开暂停菜单"
                    : "先执行罚时，完成后才能开始换胎";
        RaceText(dc, title, width * 0.045, height * 0.55,
            Math.Max(14, height * 0.19), White, TextAlignment.Left, true);
        RaceBoundedText(dc, OverlayTextLocalization.Text(detail),
            new Rect(width * 0.045, height * 0.67, width * 0.68, height * 0.22),
            Math.Max(11, height * 0.125), RaceSecondary, true, TextAlignment.Left);
        var valueText = completed
            ? "OK"
            : overdue ? $"+{postRaceAdjustment:0}s"
            : servingDriveThrough || driveThrough ? "DT"
            : active
                ? $"{Math.Max(0, participant.PenaltyServiceRequiredSeconds - participant.PenaltyServiceElapsedSeconds):0.0}"
                : $"+{participant.PendingTimePenaltySeconds:0.#}s";
        var valueBounds = new Rect(width * 0.75, height * 0.18, width * 0.21, height * 0.64);
        dc.DrawRoundedRectangle(BrushWithOpacity(accent, 0.14),
            new Pen(BrushWithOpacity(accent, 0.46), 1), valueBounds, 5, 5);
        RaceText(dc, valueText, valueBounds.Left + valueBounds.Width * 0.5, valueBounds.Top + valueBounds.Height * 0.52,
            Math.Max(20, height * 0.29), completed ? accent : White, TextAlignment.Center, true);
        if (active && participant.PenaltyServiceRequiredSeconds > 0)
        {
            var progress = Math.Clamp(
                participant.PenaltyServiceElapsedSeconds / participant.PenaltyServiceRequiredSeconds,
                0,
                1);
            dc.DrawRoundedRectangle(BrushOf(0x7E, 0x89, 0x96, 0.24), null,
                new Rect(width * 0.045, height * 0.93, width * 0.91, height * 0.045), 2, 2);
            dc.DrawRoundedRectangle(accent, null,
                new Rect(width * 0.045, height * 0.93, width * 0.91 * progress, height * 0.045), 2, 2);
        }
    }

    private sealed class PitHudRuntime
    {
        public DateTimeOffset EnteredAt { get; set; }
        public DateTimeOffset? ExitedAt { get; set; }
        public DateTimeOffset? ServiceCompletedAt { get; set; }
        public bool WasInPit { get; set; }
        public bool IsInServiceZone { get; set; }
        public bool WasServiceCounting { get; set; }
        public bool ServiceCompleted { get; set; }
        public bool ServicePaused { get; set; }
        public int Position { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string ThemeColor { get; set; } = "#42D7E8";
        public string? TeamName { get; set; }
        public double ServiceElapsedSeconds { get; set; }
        public double ServiceRequiredSeconds { get; set; }
        public double FrozenServiceSeconds { get; set; }
        public double FrozenPitLaneSeconds { get; set; }
        public double PitLaneElapsedSeconds { get; set; }
        public bool IsServingPenalty { get; set; }
        public bool PenaltyServiceCompleted { get; set; }
        public double PenaltyElapsedSeconds { get; set; }
        public double PenaltyRequiredSeconds { get; set; }
    }

    private sealed class RaceWidgetDrawingRuntime
    {
        public DrawingGroup? Current { get; set; }
        public DrawingGroup? Outgoing { get; set; }
        public string? ContentKey { get; set; }
        public double ContentTransitionStartedSeconds { get; set; } = double.NegativeInfinity;
    }

    private sealed class AnimatedRowRuntime(
        double position,
        double opacity,
        double lastSeconds)
    {
        private double position = position;
        private double opacity = opacity;
        private double lastSeconds = lastSeconds;

        public AnimatedRowVisual Update(
            double targetPosition,
            double nowSeconds,
            bool reduceMotion,
            double moveSeconds,
            double fadeSeconds)
        {
            var deltaSeconds = Math.Clamp(nowSeconds - lastSeconds, 0, 0.25);
            lastSeconds = nowSeconds;
            if (reduceMotion)
            {
                position = targetPosition;
                opacity = 1;
            }
            else
            {
                var amount = 1 - Math.Exp(-deltaSeconds / Math.Max(0.01, moveSeconds * 0.28));
                position += (targetPosition - position) * amount;
                if (Math.Abs(position - targetPosition) < 0.001) position = targetPosition;
                opacity = MoveTowards(opacity, 1, deltaSeconds / Math.Max(0.01, fadeSeconds));
            }
            return new AnimatedRowVisual(
                position,
                SmoothStep(opacity),
                Math.Abs(position - targetPosition) > 0.001 || opacity < 0.999);
        }
    }

    private readonly record struct AnimatedRowVisual(double Position, double Opacity, bool IsAnimating);

    private sealed class LeaderboardValueRuntime(string value, double nowSeconds)
    {
        private string current = value;
        private string? previous;
        private double transitionStartedSeconds = nowSeconds;

        public LeaderboardValueVisual Update(string value, double nowSeconds, bool reduceMotion)
        {
            if (!string.Equals(current, value, StringComparison.Ordinal))
            {
                previous = reduceMotion ? null : current;
                current = value;
                transitionStartedSeconds = nowSeconds;
            }
            var progress = reduceMotion
                ? 1
                : SmoothStep((nowSeconds - transitionStartedSeconds) / 0.12);
            if (progress >= 1) previous = null;
            return new LeaderboardValueVisual(previous, current, progress);
        }
    }

    private readonly record struct LeaderboardValueVisual(
        string? Previous,
        string Current,
        double Progress);

    private sealed class RaceMapPointRuntime(Point point, double lastSeconds)
    {
        private Point point = point;
        private double lastSeconds = lastSeconds;

        public RaceMapPointVisual Update(Point target, double nowSeconds, bool reduceMotion)
        {
            var deltaSeconds = Math.Clamp(nowSeconds - lastSeconds, 0, 0.25);
            lastSeconds = nowSeconds;
            var delta = target - point;
            if (reduceMotion || delta.Length > 0.35)
            {
                point = target;
                return new RaceMapPointVisual(point, false);
            }

            var amount = 1 - Math.Exp(-deltaSeconds / 0.055);
            point += delta * amount;
            var remaining = target - point;
            if (remaining.Length < 0.0005) point = target;
            return new RaceMapPointVisual(point, (target - point).Length >= 0.0005);
        }
    }

    private readonly record struct RaceMapPointVisual(Point Point, bool IsAnimating);

    private sealed record PitHudView(
        Guid ParticipantId,
        int Position,
        string DisplayName,
        string ThemeColor,
        string? TeamName,
        bool IsService,
        bool ServiceCompleted,
        double Seconds,
        bool IsInPit,
        bool IsPenalty,
        bool PenaltyCompleted,
        double PenaltyRequiredSeconds,
        PitHudServiceState ServiceState,
        double ServiceRequiredSeconds);

    private enum PitHudServiceState
    {
        None,
        WaitingForStop,
        Counting,
        Paused,
        Completed
    }

    private sealed record PitHudSnapshot(
        IReadOnlyList<PitHudView> Entries,
        int ActiveParticipantCount)
    {
        public static PitHudSnapshot Empty { get; } = new([], 0);
    }

    private void DrawRaceStartLights(DrawingContext dc, EstateRaceSession session)
    {
        var width = ActualWidth * 0.30;
        var height = ActualHeight * 0.09;
        var spacing = width * 0.018;
        var cellWidth = (width - spacing * 4) / 5;
        var deltaSeconds = double.IsFinite(previousStartLightRenderSeconds)
            ? Math.Clamp(estateRaceAnimationNowSeconds - previousStartLightRenderSeconds, 0, 0.1)
            : 0;
        previousStartLightRenderSeconds = estateRaceAnimationNowSeconds;
        for (var index = 0; index < 5; index++)
        {
            var left = index * (cellWidth + spacing);
            var housing = new Rect(left, 0, cellWidth, height);
            dc.DrawRoundedRectangle(
                BrushOf(0x05, 0x07, 0x0A, 0.90),
                new Pen(BrushOf(0x92, 0x9D, 0xAA, 0.32), 1),
                housing,
                Math.Max(5, height * 0.12),
                Math.Max(5, height * 0.12));
            var center = new Point(left + cellWidth / 2, height / 2);
            var radius = Math.Min(cellWidth, height) * 0.28;
            var illuminated = !session.StartLightsOut && index < session.IlluminatedStartLights;
            var targetLevel = illuminated ? 1d : 0d;
            startLightLevels[index] = estateRaceReduceMotion || layoutPreview
                ? targetLevel
                : MoveTowards(
                    startLightLevels[index],
                    targetLevel,
                    deltaSeconds / (illuminated ? 0.10 : 0.05));
            var lightLevel = SmoothStep(startLightLevels[index]);
            startLightAnimation |= Math.Abs(startLightLevels[index] - targetLevel) > 0.001;
            if (lightLevel > 0.001)
            {
                dc.DrawEllipse(BrushOf(0xFF, 0x18, 0x2F, 0.18 * lightLevel), null,
                    center, radius * (1.15 + 0.40 * lightLevel), radius * (1.15 + 0.40 * lightLevel));
                dc.DrawEllipse(BrushOf(0xFF, 0x18, 0x2F, 0.42 * lightLevel), null,
                    center, radius * (1 + 0.22 * lightLevel), radius * (1 + 0.22 * lightLevel));
            }
            dc.DrawEllipse(
                BlendBrush(BrushColor(0x35, 0x0B, 0x10), BrushColor(0xFF, 0x21, 0x35), lightLevel, 0.78 + 0.22 * lightLevel),
                new Pen(BlendBrush(BrushColor(0x70, 0x32, 0x38), BrushColor(0xFF, 0x8A, 0x96), lightLevel, 0.65 + 0.30 * lightLevel), 1),
                center,
                radius,
                radius);
            if (lightLevel > 0.001)
                dc.DrawEllipse(BrushOf(0xFF, 0xD8, 0xDC, 0.72 * lightLevel), null,
                    new Point(center.X - radius * 0.24, center.Y - radius * 0.28),
                    radius * 0.16,
                    radius * 0.16);
        }
    }

    private void DrawRaceGripStatus(DrawingContext dc, EstateRaceHudState state)
    {
        var width = ActualWidth * 0.20;
        var height = ActualHeight * 0.095;
        var color = state.LocalGripCondition switch
        {
            RaceGripCondition.SlightlyReduced => BrushOf(0x4D, 0xD8, 0x91),
            RaceGripCondition.ModeratelyReduced => BrushOf(0xF2, 0xC3, 0x43),
            RaceGripCondition.SeverelyReduced => BrushOf(0xF2, 0x82, 0x42),
            RaceGripCondition.AtLimit => BrushOf(0xFF, 0x45, 0x5F),
            _ => Muted
        };
        dc.DrawRoundedRectangle(BrushOf(0x08, 0x0B, 0x11, 0.93),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.32), 1),
            new Rect(0, 0, width, height), 8, 8);
        dc.DrawRectangle(color, null, new Rect(0, 0, width * 0.018, height));
        RaceText(dc, "GRIP TREND", width * 0.070, height * 0.25,
            Math.Max(11, height * 0.17), RaceSecondary, TextAlignment.Left, true);
        RaceText(dc, OverlayTextLocalization.Text(GripConditionText(state.LocalGripCondition)), width * 0.070, height * 0.58,
            Math.Max(13, height * 0.25), color, TextAlignment.Left, true);
        RaceBoundedText(dc, OverlayTextLocalization.Text(state.GripExplanation),
            new Rect(width * 0.42, height * 0.08, width * 0.52, height * 0.46),
            Math.Max(11, height * 0.15),
            RaceSecondary,
            true);
        var activeLevel = state.LocalGripCondition switch
        {
            RaceGripCondition.SlightlyReduced => 1,
            RaceGripCondition.ModeratelyReduced => 2,
            RaceGripCondition.SeverelyReduced => 3,
            RaceGripCondition.AtLimit => 4,
            _ => 0
        };
        var segmentWidth = width * 0.105;
        var entryProgress = RaceWidgetEntryProgress(EstateRaceHudWidgetKind.GripStatus);
        for (var index = 0; index < 4; index++)
        {
            var segmentProgress = SmoothStep(
                (entryProgress - index * 0.07) / Math.Max(0.01, 1 - index * 0.07));
            dc.DrawRoundedRectangle(
                index < activeLevel
                    ? BrushWithOpacity(color, segmentProgress)
                    : BrushOf(0x5D, 0x67, 0x74, 0.25),
                null,
                new Rect(
                    width * 0.49 + index * width * 0.116,
                    height * 0.64,
                    segmentWidth,
                    height * 0.13),
                2,
                2);
        }
    }

    private void DrawRacePitWindowSuggestion(
        DrawingContext dc,
        PitWindowHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.19;
        var height = ActualHeight * 0.14;
        var cyan = BrushOf(0x20, 0xD9, 0xEF);
        var amber = BrushOf(0xFF, 0xB5, 0x21);
        var accent = snapshot.WindowOpen ? amber : cyan;
        dc.DrawRoundedRectangle(
            BrushOf(0x07, 0x0C, 0x13, 0.97),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.46), 1),
            new Rect(0, 0, width, height),
            9,
            9);
        dc.DrawRoundedRectangle(accent, null,
            new Rect(0, height * 0.08, Math.Max(5, width * 0.013), height * 0.84), 3, 3);
        RaceTitleText(dc, snapshot.WindowOpen ? "WINDOW OPEN" : "PIT WINDOW",
            width * 0.055, height * 0.17,
            Math.Max(14, height * 0.15), snapshot.WindowOpen ? amber : White,
            TextAlignment.Left);
        dc.DrawLine(new Pen(BrushOf(0x9A, 0xA8, 0xB7, 0.38), 1),
            new Point(width * 0.055, height * 0.29),
            new Point(width * 0.95, height * 0.29));

        if (snapshot.WindowOpen)
        {
            RaceTitleText(dc, "PIT THIS LAP", width * 0.055, height * 0.48,
                Math.Max(18, height * 0.19), White, TextAlignment.Left);
            RaceTitleText(dc, snapshot.EndLap > snapshot.StartLap ? "OR NEXT" : "NOW",
                width * 0.055, height * 0.65,
                Math.Max(15, height * 0.16), White, TextAlignment.Left);
            var center = new Point(width * 0.80, height * 0.52);
            var radius = height * 0.17;
            dc.DrawEllipse(BrushWithOpacity(amber, 0.08), new Pen(amber, Math.Max(2, height * 0.018)),
                center, radius, radius);
            RaceTitleText(dc, "!", center.X, center.Y, Math.Max(25, height * 0.29), amber,
                TextAlignment.Center);
        }
        else
        {
            RaceText(dc, "LAPS", width * 0.055, height * 0.42,
                Math.Max(11, height * 0.095), RaceSecondary, TextAlignment.Left, true);
            var windowText = snapshot.StartLap == snapshot.EndLap
                ? snapshot.StartLap.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{snapshot.StartLap}–{snapshot.EndLap}";
            RaceTitleText(dc, windowText, width * 0.055, height * 0.62,
                Math.Max(28, height * 0.25), cyan, TextAlignment.Left);
            dc.DrawLine(new Pen(BrushOf(0x9A, 0xA8, 0xB7, 0.34), 1),
                new Point(width * 0.55, height * 0.36),
                new Point(width * 0.55, height * 0.72));
            var center = new Point(width * 0.77, height * 0.51);
            var radius = height * 0.17;
            dc.DrawEllipse(BrushOf(0x11, 0x1B, 0x27, 0.92),
                new Pen(BrushOf(0x4B, 0x5A, 0x69, 0.72), Math.Max(2, height * 0.022)),
                center, radius, radius);
            var entryProgress = RaceWidgetEntryProgress(EstateRaceHudWidgetKind.PitWindowSuggestion);
            var progress = Math.Clamp((3 - snapshot.LapsUntilWindow) / 3d, 0.15, 1) * entryProgress;
            DrawPitWindowProgressArc(dc, center, radius, progress, cyan, height);
            RaceTitleText(dc, snapshot.LapsUntilWindow.ToString(System.Globalization.CultureInfo.InvariantCulture),
                center.X, center.Y, Math.Max(24, height * 0.27), White, TextAlignment.Center);
            RaceText(dc, "LAPS TO WINDOW", center.X, height * 0.72,
                Math.Max(9, height * 0.082), White, TextAlignment.Center, true);
        }

        dc.DrawLine(new Pen(BrushOf(0x9A, 0xA8, 0xB7, 0.38), 1),
            new Point(width * 0.055, height * 0.79),
            new Point(width * 0.95, height * 0.79));
        RaceText(dc, "RECENT DEGRADATION", width * 0.055, height * 0.90,
            Math.Max(9, height * 0.078), RaceSecondary, TextAlignment.Left, true);
        var degradationText = snapshot.DegradationPerLapSeconds is double degradation
            ? $"+{degradation:0.00} s/lap"
            : "— s/lap";
        RaceText(dc, degradationText, width * 0.945, height * 0.90,
            Math.Max(11, height * 0.10), White, TextAlignment.Right, true);
    }

    private static void DrawPitWindowProgressArc(
        DrawingContext dc,
        Point center,
        double radius,
        double progress,
        Brush brush,
        double height)
    {
        progress = Math.Clamp(progress, 0, 0.999);
        var startAngle = -90d;
        var endAngle = startAngle + 360 * progress;
        static Point ArcPoint(Point origin, double arcRadius, double angle)
        {
            var radians = angle * Math.PI / 180;
            return new Point(
                origin.X + Math.Cos(radians) * arcRadius,
                origin.Y + Math.Sin(radians) * arcRadius);
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ArcPoint(center, radius, startAngle), false, false);
            context.ArcTo(
                ArcPoint(center, radius, endAngle),
                new Size(radius, radius),
                0,
                progress > 0.5,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, Math.Max(2, height * 0.023)), geometry);
    }

    private void DrawRaceFullStrategy(
        DrawingContext dc,
        FullRaceStrategyHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.68;
        var height = ActualHeight * 0.36;
        var cyan = RaceStrategyCyan;
        var amber = RaceStrategyAmber;
        var border = new Pen(BrushOf(0x9A, 0xB1, 0xC8, 0.80), Math.Max(1, height * 0.004));
        dc.DrawRoundedRectangle(RaceStrategyBackground, border,
            new Rect(0, 0, width, height), height * 0.065, height * 0.065);
        dc.DrawRoundedRectangle(BrushOf(0x03, 0x08, 0x0E, 0.54), null,
            new Rect(width * 0.006, height * 0.012, width * 0.988, height * 0.976),
            height * 0.055, height * 0.055);

        DrawRaceStrategyHeader(dc, snapshot, width, height, cyan);

        DrawRaceStrategyTimeline(dc, snapshot, width, height, cyan, amber);

        var metricDividerY = height * 0.705;
        dc.DrawLine(new Pen(BrushOf(0x8C, 0xA4, 0xBC, 0.58), Math.Max(1, height * 0.0035)),
            new Point(0, metricDividerY), new Point(width, metricDividerY));
        var metricOpacity = SmoothStep((RaceWidgetEntryProgress(EstateRaceHudWidgetKind.FullRaceStrategy) - 0.22) / 0.78);
        dc.PushOpacity(metricOpacity);
        DrawRaceStrategyMetrics(dc, snapshot, width, height, cyan, amber);
        dc.Pop();
    }

    private static void DrawRaceStrategyHeader(
        DrawingContext dc,
        FullRaceStrategyHudSnapshot snapshot,
        double width,
        double height,
        Brush cyan)
    {
        var dividerY = height * 0.225;
        dc.DrawLine(new Pen(BrushOf(0x86, 0xA0, 0xB9, 0.56), Math.Max(1, height * 0.0035)),
            new Point(0, dividerY), new Point(width, dividerY));

        var slash = new StreamGeometry();
        using (var context = slash.Open())
        {
            context.BeginFigure(new Point(width * 0.026, height * 0.065), true, true);
            context.LineTo(new Point(width * 0.034, height * 0.065), true, false);
            context.LineTo(new Point(width * 0.026, height * 0.19), true, false);
            context.LineTo(new Point(width * 0.018, height * 0.19), true, false);
        }
        slash.Freeze();
        dc.DrawGeometry(cyan, null, slash);

        DrawRaceStrategyItalicText(dc, "RACE STRATEGY",
            width * 0.047, height * 0.13,
            Math.Max(22, height * 0.115), White, TextAlignment.Left);
        var titleDividerX = width * 0.325;
        dc.DrawLine(new Pen(BrushOf(0xB4, 0xC0, 0xCC, 0.72), Math.Max(1, height * 0.003)),
            new Point(titleDividerX, height * 0.075),
            new Point(titleDividerX, height * 0.18));
        RaceText(dc, "FULL-RACE PROJECTION", width * 0.355, height * 0.13,
            Math.Max(13, height * 0.062), RaceSecondary, TextAlignment.Left, true);

        var badge = new Rect(width * 0.735, height * 0.055, width * 0.235, height * 0.13);
        dc.DrawRoundedRectangle(
            BrushOf(0x07, 0x1B, 0x26, 0.90),
            new Pen(cyan, Math.Max(1.5, height * 0.006)),
            badge,
            badge.Height / 2,
            badge.Height / 2);
        var iconCenter = new Point(badge.Left + badge.Height * 0.72, badge.Top + badge.Height / 2);
        DrawStrategyWrench(dc, iconCenter, badge.Height * 0.31, cyan, Math.Max(1.8, height * 0.007));
        var plannedStops = snapshot.StopWindows.Count;
        var stopText = plannedStops == 1 ? "1 STOP" : $"{plannedStops} STOPS";
        DrawRaceStrategyItalicText(dc, stopText,
            badge.Left + badge.Width * 0.31, badge.Top + badge.Height * 0.50,
            Math.Max(13, height * 0.068), White, TextAlignment.Left);
        RaceText(dc, plannedStops == 0 ? "PLANNED" : "REQUIRED",
            badge.Right - badge.Width * 0.055, badge.Top + badge.Height * 0.50,
            Math.Max(10, height * 0.048), RaceSecondary, TextAlignment.Right, false);
    }

    private void DrawRaceStrategyTimeline(
        DrawingContext dc,
        FullRaceStrategyHudSnapshot snapshot,
        double width,
        double height,
        Brush cyan,
        Brush amber)
    {
        var totalLaps = Math.Max(1, snapshot.TotalLaps);
        var left = width * 0.03;
        var right = width * 0.97;
        var timelineWidth = right - left;
        var timelineY = height * 0.485;
        var entryProgress = RaceWidgetEntryProgress(EstateRaceHudWidgetKind.FullRaceStrategy);
        var reveal = SmoothStep((entryProgress - 0.08) / 0.72);
        double LapX(double lap) => totalLaps <= 1
            ? left
            : left + (Math.Clamp(lap, 1, totalLaps) - 1) / (totalLaps - 1d) * timelineWidth;

        DrawRaceStrategyLabels(dc, snapshot, width, height, cyan, amber, LapX);

        dc.DrawLine(new Pen(BrushOf(0x02, 0x04, 0x08, 0.96), Math.Max(8, height * 0.043)),
            new Point(left, timelineY), new Point(right, timelineY));
        dc.PushClip(new RectangleGeometry(new Rect(left - height * 0.05, height * 0.39,
            timelineWidth * reveal + height * 0.10, height * 0.18)));
        foreach (var stint in snapshot.Stints)
        {
            var startX = LapX(stint.StartLap);
            var endX = LapX(stint.EndLap);
            dc.DrawLine(new Pen(BrushWithOpacity(cyan, stint.Number % 2 == 0 ? 0.72 : 0.98),
                    Math.Max(5, height * 0.025)),
                new Point(startX, timelineY),
                new Point(Math.Max(startX + 1, endX), timelineY));
        }

        foreach (var window in snapshot.StopWindows)
        {
            var startX = LapX(window.StartLap);
            var endX = LapX(window.EndLap);
            var bounds = new Rect(startX, timelineY - height * 0.038,
                Math.Max(width * 0.025, endX - startX), height * 0.076);
            dc.DrawRectangle(BrushOf(0xB9, 0x72, 0x02, 0.88), null, bounds);
            dc.PushClip(new RectangleGeometry(bounds));
            var hatchPen = new Pen(BrushOf(0xFF, 0xC3, 0x32, 0.72), Math.Max(1, height * 0.004));
            var hatchStep = Math.Max(5, height * 0.022);
            for (var x = bounds.Left - bounds.Height; x < bounds.Right + bounds.Height; x += hatchStep)
                dc.DrawLine(hatchPen,
                    new Point(x, bounds.Bottom),
                    new Point(x + bounds.Height, bounds.Top));
            dc.Pop();
            var gatePen = new Pen(amber, Math.Max(1.5, height * 0.006));
            dc.DrawLine(gatePen,
                new Point(bounds.Left, timelineY - height * 0.075),
                new Point(bounds.Left, timelineY + height * 0.075));
            dc.DrawLine(gatePen,
                new Point(bounds.Right, timelineY - height * 0.075),
                new Point(bounds.Right, timelineY + height * 0.075));
            var targetX = LapX(window.TargetLap);
            var targetCenter = new Point(targetX, timelineY);
            dc.DrawEllipse(BrushOf(0x08, 0x0C, 0x12),
                new Pen(amber, Math.Max(2, height * 0.011)),
                targetCenter, height * 0.045, height * 0.045);
            DrawStrategyWrench(dc, targetCenter, height * 0.021, White, Math.Max(1, height * 0.004));
        }
        dc.Pop();

        var endpointRadius = height * 0.035;
        dc.DrawEllipse(BrushOf(0x06, 0x11, 0x18), new Pen(cyan, Math.Max(1.5, height * 0.007)),
            new Point(left, timelineY), endpointRadius, endpointRadius);
        dc.DrawEllipse(BrushOf(0x06, 0x11, 0x18), new Pen(cyan, Math.Max(1.5, height * 0.007)),
            new Point(right, timelineY), endpointRadius, endpointRadius);
        dc.DrawEllipse(cyan, null, new Point(left, timelineY), endpointRadius * 0.62, endpointRadius * 0.62);
        dc.DrawEllipse(BrushWithOpacity(cyan, 0.32), null,
            new Point(right, timelineY), endpointRadius * 0.62, endpointRadius * 0.62);

        var labelStride = totalLaps switch
        {
            <= 24 => 1,
            <= 40 => 2,
            <= 60 => 3,
            _ => Math.Max(4, (int)Math.Ceiling(totalLaps / 20d))
        };
        for (var lap = 1; lap <= totalLaps; lap++)
        {
            var x = LapX(lap);
            var emphasized = lap == 1 || lap == totalLaps || snapshot.StopWindows.Any(window => window.TargetLap == lap);
            dc.DrawLine(new Pen(emphasized ? White : BrushOf(0x9B, 0xAC, 0xBD, 0.82),
                    emphasized ? Math.Max(1.5, height * 0.006) : Math.Max(1, height * 0.004)),
                new Point(x, timelineY + height * 0.07),
                new Point(x, timelineY + height * (emphasized ? 0.135 : 0.115)));
            if (lap != 1 && lap != totalLaps && lap % labelStride != 0) continue;
            RaceText(dc, lap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x, height * 0.655,
                Math.Max(10, height * 0.052),
                snapshot.StopWindows.Any(window => lap >= window.StartLap && lap <= window.EndLap)
                    ? amber
                    : emphasized ? White : RaceSecondary,
                TextAlignment.Center,
                emphasized);
        }
    }

    private static void DrawRaceStrategyLabels(
        DrawingContext dc,
        FullRaceStrategyHudSnapshot snapshot,
        double width,
        double height,
        Brush cyan,
        Brush amber,
        Func<double, double> lapX)
    {
        if (snapshot.StopWindows.Count == 1 && snapshot.Stints.Count >= 2)
        {
            var first = snapshot.Stints[0];
            var window = snapshot.StopWindows[0];
            var second = snapshot.Stints[1];
            var horizontalGap = width * 0.015;
            var pitWidth = width * 0.20;
            var minimumStintWidth = width * 0.14;
            var pitCenter = Math.Clamp(
                (lapX(window.StartLap) + lapX(window.EndLap)) / 2,
                width * 0.03 + minimumStintWidth + horizontalGap + pitWidth / 2,
                width * 0.97 - minimumStintWidth - horizontalGap - pitWidth / 2);
            var pitBounds = new Rect(
                pitCenter - pitWidth / 2,
                height * 0.285,
                pitWidth,
                height * 0.105);
            var firstBounds = new Rect(
                width * 0.03,
                height * 0.285,
                Math.Max(minimumStintWidth, Math.Min(width * 0.25,
                    pitBounds.Left - horizontalGap - width * 0.03)),
                height * 0.105);
            var secondWidth = Math.Max(minimumStintWidth, Math.Min(width * 0.32,
                width * 0.97 - pitBounds.Right - horizontalGap));
            var secondBounds = new Rect(
                width * 0.97 - secondWidth,
                height * 0.285,
                secondWidth,
                height * 0.105);
            DrawRaceStrategyLabel(dc,
                firstBounds,
                $"STINT {first.Number}", $"LAP {first.StartLap}–{first.EndLap}", cyan, height);
            DrawRaceStrategyLabel(dc,
                pitBounds,
                "PIT WINDOW", $"LAP {window.StartLap}–{window.EndLap}", amber, height);
            DrawRaceStrategyLabel(dc,
                secondBounds,
                $"STINT {second.Number}", $"LAP {second.StartLap}–{second.EndLap}", cyan, height);
            return;
        }

        if (snapshot.StopWindows.Count == 0 && snapshot.Stints.Count > 0)
        {
            var stint = snapshot.Stints[0];
            DrawRaceStrategyLabel(dc,
                new Rect(width * 0.33, height * 0.285, width * 0.34, height * 0.105),
                $"STINT {stint.Number}", $"LAP {stint.StartLap}–{stint.EndLap}", cyan, height);
            return;
        }

        if (snapshot.StopWindows.Count > 1 && snapshot.Stints.Count >= 2)
        {
            var first = snapshot.Stints[0];
            var nextWindow = snapshot.StopWindows[0];
            var final = snapshot.Stints[^1];
            var pitWidth = width * 0.20;
            var pitCenter = Math.Clamp(
                lapX(nextWindow.TargetLap),
                width * 0.315,
                width * 0.685);
            DrawRaceStrategyLabel(dc,
                new Rect(width * 0.03, height * 0.285, width * 0.24, height * 0.105),
                $"STINT {first.Number}", $"LAP {first.StartLap}–{first.EndLap}", cyan, height);
            DrawRaceStrategyLabel(dc,
                new Rect(pitCenter - pitWidth / 2, height * 0.285,
                    pitWidth, height * 0.105),
                $"PIT {nextWindow.Number}", $"LAP {nextWindow.StartLap}–{nextWindow.EndLap}", amber, height);
            DrawRaceStrategyLabel(dc,
                new Rect(width * 0.73, height * 0.285, width * 0.24, height * 0.105),
                $"STINT {final.Number}", $"LAP {final.StartLap}–{final.EndLap}", cyan, height);
        }
    }

    private static void DrawRaceStrategyLabel(
        DrawingContext dc,
        Rect bounds,
        string title,
        string detail,
        Brush accent,
        double height)
    {
        dc.DrawRoundedRectangle(
            BrushOf(0x04, 0x17, 0x21, 0.88),
            new Pen(BrushWithOpacity(accent, 0.82), Math.Max(1, height * 0.004)),
            bounds,
            height * 0.025,
            height * 0.025);
        var usesWideTitleColumn = title.Length >= 9;
        var separatorRatio = usesWideTitleColumn ? 0.62 : 0.54;
        var titleCenterRatio = usesWideTitleColumn ? 0.31 : 0.30;
        var detailCenterRatio = usesWideTitleColumn ? 0.81 : 0.76;
        var separatorX = bounds.Left + bounds.Width * separatorRatio;
        dc.DrawLine(new Pen(BrushOf(0xB4, 0xC0, 0xCC, 0.68), Math.Max(1, height * 0.0035)),
            new Point(separatorX, bounds.Top + bounds.Height * 0.22),
            new Point(separatorX, bounds.Bottom - bounds.Height * 0.22));
        RaceText(dc, title, bounds.Left + bounds.Width * titleCenterRatio, bounds.Top + bounds.Height * 0.50,
            Math.Max(11, height * 0.055), accent == RaceStrategyAmber ? accent : White,
            TextAlignment.Center, true);
        RaceText(dc, detail, bounds.Left + bounds.Width * detailCenterRatio, bounds.Top + bounds.Height * 0.50,
            Math.Max(10, height * 0.049), White, TextAlignment.Center, false);

        var pointer = new StreamGeometry();
        using (var context = pointer.Open())
        {
            context.BeginFigure(new Point(bounds.Left + bounds.Width * 0.50 - height * 0.025, bounds.Bottom), true, true);
            context.LineTo(new Point(bounds.Left + bounds.Width * 0.50 + height * 0.025, bounds.Bottom), true, false);
            context.LineTo(new Point(bounds.Left + bounds.Width * 0.50, bounds.Bottom + height * 0.035), true, false);
        }
        pointer.Freeze();
        dc.DrawGeometry(accent, null, pointer);
    }

    private static void DrawRaceStrategyMetrics(
        DrawingContext dc,
        FullRaceStrategyHudSnapshot snapshot,
        double width,
        double height,
        Brush cyan,
        Brush amber)
    {
        var gap = width * 0.012;
        var cardWidth = (width * 0.974 - gap * 3) / 4;
        var top = height * 0.73;
        var cardHeight = height * 0.235;
        for (var index = 0; index < 4; index++)
        {
            var bounds = new Rect(width * 0.013 + index * (cardWidth + gap), top, cardWidth, cardHeight);
            dc.DrawRoundedRectangle(
                BrushOf(0x07, 0x10, 0x18, 0.91),
                new Pen(BrushOf(0x72, 0x89, 0x9F, 0.44), Math.Max(1, height * 0.0035)),
                bounds,
                height * 0.035,
                height * 0.035);
            var iconCenter = new Point(bounds.Left + bounds.Width * 0.18, bounds.Top + bounds.Height * 0.52);
            var iconRadius = Math.Min(bounds.Width * 0.105, bounds.Height * 0.29);
            var labelX = bounds.Left + bounds.Width * 0.38;
            switch (index)
            {
                case 0:
                    DrawStrategyStopwatch(dc, iconCenter, iconRadius, RaceSecondary, height);
                    DrawStrategyMetricText(dc, "EST. PIT LOSS",
                        snapshot.EstimatedPitLossSeconds is double pitLoss ? $"{pitLoss:0.0} s" : "— s",
                        labelX, bounds, White, height);
                    break;
                case 1:
                    DrawStrategyGainArrow(dc, iconCenter, iconRadius, cyan, height);
                    var firstStintLaps = snapshot.Stints.Count > 0
                        ? snapshot.Stints[0].EndLap - snapshot.Stints[0].StartLap + 1
                        : 0;
                    DrawStrategyMetricText(dc, "FIRST STINT",
                        firstStintLaps > 0 ? $"{firstStintLaps} LAPS" : "— LAPS",
                        labelX, bounds, cyan, height);
                    break;
                case 2:
                    DrawStrategyConfidenceBars(dc, iconCenter, iconRadius, amber, height);
                    DrawStrategyMetricText(dc, "CONFIDENCE",
                        snapshot.Confidence switch
                        {
                            EstatePitStrategyConfidence.High => "HIGH",
                            EstatePitStrategyConfidence.Medium => "MEDIUM",
                            _ => "LOW"
                        },
                        labelX, bounds, amber, height);
                    break;
                default:
                    DrawStrategyPaceGauge(dc, iconCenter, iconRadius, cyan, height);
                    DrawStrategyMetricText(dc, "PLAN BASIS",
                        snapshot.HasHistoricalEvidence ? "HISTORICAL" : "BASELINE",
                        labelX, bounds, cyan, height);
                    break;
            }
        }
    }

    private static void DrawStrategyMetricText(
        DrawingContext dc,
        string label,
        string value,
        double x,
        Rect bounds,
        Brush valueBrush,
        double height)
    {
        RaceText(dc, label, x, bounds.Top + bounds.Height * 0.33,
            Math.Max(10, height * 0.045), RaceSecondary, TextAlignment.Left, true);
        DrawRaceStrategyItalicText(dc, value, x, bounds.Top + bounds.Height * 0.66,
            Math.Max(18, height * 0.085), valueBrush, TextAlignment.Left);
    }

    private static void DrawStrategyWrench(
        DrawingContext dc,
        Point center,
        double radius,
        Brush brush,
        double thickness)
    {
        var axis = new Vector(0.70, -0.70);
        var perpendicular = new Vector(0.70, 0.70);
        var handle = center - axis * radius * 0.78;
        var jaw = center + axis * radius * 0.52;
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pen, handle, jaw);
        dc.DrawEllipse(null, new Pen(brush, Math.Max(1, thickness * 0.72)), handle,
            radius * 0.22, radius * 0.22);
        dc.DrawLine(pen, jaw, jaw + axis * radius * 0.38 + perpendicular * radius * 0.25);
        dc.DrawLine(pen, jaw, jaw + axis * radius * 0.38 - perpendicular * radius * 0.25);
    }

    private static void DrawStrategyStopwatch(
        DrawingContext dc, Point center, double radius, Brush brush, double height)
    {
        var pen = new Pen(brush, Math.Max(2, height * 0.012));
        dc.DrawEllipse(null, pen, center, radius, radius);
        dc.DrawLine(pen, new Point(center.X, center.Y - radius * 1.35),
            new Point(center.X, center.Y - radius * 0.95));
        dc.DrawLine(pen, new Point(center.X - radius * 0.25, center.Y - radius * 1.35),
            new Point(center.X + radius * 0.25, center.Y - radius * 1.35));
        dc.DrawLine(new Pen(brush, Math.Max(1.5, height * 0.008)), center,
            new Point(center.X, center.Y - radius * 0.55));
    }

    private static void DrawStrategyGainArrow(
        DrawingContext dc, Point center, double radius, Brush brush, double height)
    {
        var pen = new Pen(brush, Math.Max(2.5, height * 0.015))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var points = new[]
        {
            new Point(center.X - radius, center.Y + radius * 0.62),
            new Point(center.X - radius * 0.42, center.Y),
            new Point(center.X + radius * 0.02, center.Y + radius * 0.34),
            new Point(center.X + radius * 0.92, center.Y - radius * 0.78)
        };
        for (var index = 1; index < points.Length; index++) dc.DrawLine(pen, points[index - 1], points[index]);
        dc.DrawLine(pen, points[^1], new Point(points[^1].X - radius * 0.48, points[^1].Y + radius * 0.02));
        dc.DrawLine(pen, points[^1], new Point(points[^1].X - radius * 0.04, points[^1].Y + radius * 0.48));
    }

    private static void DrawStrategyConfidenceBars(
        DrawingContext dc, Point center, double radius, Brush brush, double height)
    {
        var barWidth = radius * 0.44;
        var gap = radius * 0.18;
        for (var index = 0; index < 3; index++)
        {
            var barHeight = radius * (0.70 + index * 0.42);
            dc.DrawRoundedRectangle(
                index < 2 ? brush : BrushOf(0x65, 0x78, 0x8A, 0.76),
                null,
                new Rect(center.X - radius + index * (barWidth + gap), center.Y + radius * 0.72 - barHeight,
                    barWidth, barHeight),
                height * 0.008,
                height * 0.008);
        }
    }

    private static void DrawStrategyPaceGauge(
        DrawingContext dc, Point center, double radius, Brush brush, double height)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X - radius, center.Y + radius * 0.42), false, false);
            context.ArcTo(new Point(center.X + radius, center.Y + radius * 0.42),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(BrushOf(0x61, 0x72, 0x82, 0.76), Math.Max(2, height * 0.013)), geometry);
        var active = new StreamGeometry();
        using (var context = active.Open())
        {
            context.BeginFigure(new Point(center.X - radius, center.Y + radius * 0.42), false, false);
            context.ArcTo(new Point(center.X + radius * 0.42, center.Y - radius * 0.82),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        active.Freeze();
        dc.DrawGeometry(null, new Pen(brush, Math.Max(2, height * 0.013)), active);
        dc.DrawLine(new Pen(brush, Math.Max(1.5, height * 0.008)), center,
            new Point(center.X + radius * 0.53, center.Y - radius * 0.52));
        dc.DrawEllipse(brush, null, center, height * 0.009, height * 0.009);
    }

    private void DrawRacePracticeProgram(
        DrawingContext dc,
        EstatePracticeTestItemState item)
    {
        var width = ActualWidth * 0.32;
        var height = ActualHeight * 0.12;
        var accent = item.Status switch
        {
            EstatePracticeTestStatus.Completed => BrushOf(0x35, 0xD0, 0x7F),
            EstatePracticeTestStatus.Failed => BrushOf(0xF2, 0x50, 0x57),
            _ => BrushOf(0x20, 0xD9, 0xEF)
        };
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.96),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.38), 1),
            new Rect(0, 0, width, height),
            9,
            9);
        dc.DrawRoundedRectangle(accent, null,
            new Rect(0, height * 0.14, Math.Max(5, width * 0.011), height * 0.72), 2, 2);
        dc.DrawRectangle(BrushWithOpacity(accent, 0.82), null,
            new Rect(width * 0.045, height * 0.16, width * 0.13, 2));
        RaceText(dc, "PRACTICE PROGRAM", width * 0.045, height * 0.245,
            Math.Max(10, height * 0.108), RaceSecondary, TextAlignment.Left, true);
        RaceText(dc, PracticeProgramStatusText(item), width * 0.955, height * 0.245,
            Math.Max(10, height * 0.108), accent, TextAlignment.Right, true);
        dc.DrawLine(
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.20), 1),
            new Point(width * 0.045, height * 0.355),
            new Point(width * 0.955, height * 0.355));
        RaceBoundedText(dc, OverlayTextLocalization.Text(item.Title),
            new Rect(width * 0.045, height * 0.405, width * 0.91, height * 0.235),
            Math.Max(14, height * 0.165), White, true, TextAlignment.Left);
        DrawRacePracticeGuidance(dc, item,
            new Rect(width * 0.045, height * 0.625, width * 0.91, height * 0.16),
            Math.Max(10.5, height * 0.112));

        var target = Math.Max(1, item.TargetSteps);
        var targetProgress = Math.Clamp(item.CompletedSteps / (double)target, 0, 1);
        if (animatedPracticeProgramKind != item.Kind)
        {
            animatedPracticeProgramKind = item.Kind;
            animatedPracticeProgress = item.Status == EstatePracticeTestStatus.Active ? 0 : targetProgress;
            previousPracticeProgramRenderSeconds = estateRaceAnimationNowSeconds;
        }
        var deltaSeconds = double.IsFinite(previousPracticeProgramRenderSeconds)
            ? Math.Clamp(estateRaceAnimationNowSeconds - previousPracticeProgramRenderSeconds, 0, 0.1)
            : 0;
        previousPracticeProgramRenderSeconds = estateRaceAnimationNowSeconds;
        animatedPracticeProgress = estateRaceReduceMotion || layoutPreview
            ? targetProgress
            : MoveTowards(animatedPracticeProgress, targetProgress, deltaSeconds / 0.18);
        practiceProgramAnimation = Math.Abs(animatedPracticeProgress - targetProgress) > 0.001;
        var progress = SmoothStep(animatedPracticeProgress);
        var progressTop = height * 0.845;
        dc.DrawRoundedRectangle(BrushOf(0x6D, 0x78, 0x84, 0.28), null,
            new Rect(width * 0.045, progressTop, width * 0.75, height * 0.055), 3, 3);
        if (progress > 0)
            dc.DrawRoundedRectangle(accent, null,
                new Rect(width * 0.045, progressTop, width * 0.75 * progress, height * 0.055), 3, 3);
        var progressText = item.Status switch
        {
            EstatePracticeTestStatus.Completed => "RETURN TO PIT",
            EstatePracticeTestStatus.Failed => "RETURN TO PIT",
            _ => $"{item.CompletedSteps} / {target}"
        };
        RaceText(dc, progressText, width * 0.955, progressTop + height * 0.027,
            Math.Max(11, height * 0.13), White, TextAlignment.Right, true);
    }

    private void DrawRacePracticeGuidance(
        DrawingContext dc,
        EstatePracticeTestItemState item,
        Rect bounds,
        double size)
    {
        // The layout editor can render once before its preview surface has received
        // a non-zero arrange size. WPF rejects zero MaxTextHeight values, so defer
        // bounded text formatting until that first layout pass has completed.
        if (!double.IsFinite(size) || size <= 0 ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height <= 0)
            return;

        var key = $"{item.Kind}:{item.Status}:{item.Guidance}";
        if (!string.Equals(practiceGuidanceAnimationKey, key, StringComparison.Ordinal))
        {
            practiceGuidanceAnimationKey = key;
            practiceGuidanceAnimationStartedSeconds = estateRaceAnimationNowSeconds;
        }
        var guidance = OverlayTextLocalization.Text(item.Guidance);
        var typeface = ContainsChinese(guidance) ? ChineseLightTypeface : RaceLightTypeface;
        var formatted = new FormattedText(
            guidance,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            RaceSecondary,
            1)
        {
            TextAlignment = TextAlignment.Left,
            MaxTextHeight = bounds.Height,
            Trimming = TextTrimming.None
        };
        var textWidth = formatted.WidthIncludingTrailingWhitespace;
        if (textWidth <= bounds.Width + 0.5)
        {
            dc.DrawText(formatted,
                new Point(bounds.Left, bounds.Top + Math.Max(0, (bounds.Height - formatted.Height) / 2)));
            return;
        }

        const double startHoldSeconds = 1.20;
        const double endHoldSeconds = 0.85;
        var overflow = textWidth - bounds.Width;
        var speed = Math.Max(30, size * 4.0);
        var travelSeconds = overflow / speed;
        var cycleSeconds = startHoldSeconds + travelSeconds + endHoldSeconds;
        var elapsed = Math.Max(0, estateRaceAnimationNowSeconds - practiceGuidanceAnimationStartedSeconds);
        var terminal = item.Status is EstatePracticeTestStatus.Completed or EstatePracticeTestStatus.Failed;
        var cycleElapsed = terminal ? Math.Min(elapsed, cycleSeconds) : elapsed % cycleSeconds;
        var offset = cycleElapsed <= startHoldSeconds
            ? 0
            : cycleElapsed >= startHoldSeconds + travelSeconds
                ? overflow
                : overflow * SmoothStep((cycleElapsed - startHoldSeconds) / travelSeconds);
        practiceProgramAnimation |= !terminal || elapsed < cycleSeconds;
        var clip = new RectangleGeometry(bounds);
        clip.Freeze();
        dc.PushClip(clip);
        dc.DrawText(formatted,
            new Point(bounds.Left - offset, bounds.Top + Math.Max(0, (bounds.Height - formatted.Height) / 2)));
        dc.Pop();
    }

    private static string PracticeProgramKindText(EstatePracticeTestKind kind) => kind switch
    {
        EstatePracticeTestKind.LongRun => "LONG RUN",
        EstatePracticeTestKind.PitStopSimulation => "PIT STOP TEST",
        _ => "QUALIFYING RUN"
    };

    private static string PracticeProgramStatusText(EstatePracticeTestItemState item) => item.Status switch
    {
        EstatePracticeTestStatus.Completed => "PROJECT SUCCESS",
        EstatePracticeTestStatus.Failed => "PROJECT FAILED",
        _ => PracticeProgramKindText(item.Kind)
    };

    private void DrawRaceBanner(DrawingContext dc, EstateRaceBanner banner)
    {
        var width = ActualWidth * 0.50;
        var height = ActualHeight * 0.09;
        var fill = banner.IsInvestigation
            ? BrushOf(0xFF, 0xCF, 0x28)
            : banner.Kind switch
        {
            RaceBannerKind.FastestLap => BrushOf(0x9C, 0x43, 0xD7),
            RaceBannerKind.Penalty or RaceBannerKind.RedFlag => BrushOf(0xF2, 0x35, 0x4F),
            RaceBannerKind.BlueFlag => BrushOf(0x42, 0x8C, 0xFF),
            RaceBannerKind.YellowFlag => BrushOf(0xFF, 0xD3, 0x28),
            RaceBannerKind.ChequeredFlag or RaceBannerKind.Winner => BrushOf(0xE8, 0xEB, 0xEF),
            _ => BrushOf(0x42, 0xD7, 0xE8)
        };
        var darkText = banner.IsInvestigation || banner.Kind is RaceBannerKind.YellowFlag or
            RaceBannerKind.ChequeredFlag or RaceBannerKind.Winner or RaceBannerKind.Information;
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.95),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.34), 1),
            new Rect(0, 0, width, height),
            8,
            8);
        dc.DrawRectangle(fill, null, new Rect(0, 0, width * 0.018, height));
        var entryProgress = RaceWidgetEntryProgress(EstateRaceHudWidgetKind.Banner);
        dc.DrawRectangle(fill, null,
            new Rect(width * 0.035, height * 0.16, width * 0.16 * entryProgress, 2));
        var foreground = darkText ? fill : White;
        RaceText(dc, banner.IsInvestigation ? "UNDER INVESTIGATION" : BannerKindText(banner.Kind),
            width * 0.035, height * 0.36,
            Math.Max(11, height * 0.15), foreground, TextAlignment.Left, true);
        RaceText(dc, OverlayTextLocalization.Text(banner.Title), width * 0.035, height * 0.70,
            Math.Max(16, height * 0.27), White, TextAlignment.Left, true);
        if (!string.IsNullOrWhiteSpace(banner.Detail))
            RaceBoundedText(dc, OverlayTextLocalization.Text(banner.Detail!),
                new Rect(width * 0.47, height * 0.08, width * 0.49, height * 0.84),
                Math.Max(13, height * 0.21), RaceSecondary, true);
    }

    private static Modules.LapAnalysis.LapHudState ApplyEstateRaceLapColors(
        Modules.LapAnalysis.LapHudState lap,
        EstateRaceHudState? race)
    {
        if (race?.Session is not { } session || race.LocalParticipantId is not Guid localId)
            return lap;
        var local = session.Participants.FirstOrDefault(candidate => candidate.Id == localId);
        if (local is null) return lap;
        var phaseFastestLapSectors = session.FastestLapSectorSeconds ?? [];
        var sectors = lap.Sectors.Select((sector, index) =>
        {
            if (sector.CurrentSeconds is not double current || !double.IsFinite(current)) return sector;
            var sessionBest = index < session.FastestSectorSeconds.Count
                ? session.FastestSectorSeconds[index]
                : null;
            var personalBest = index < local.BestSectorSeconds.Count
                ? local.BestSectorSeconds[index]
                : null;
            var phaseFastestLapSector = index < phaseFastestLapSectors.Count
                ? phaseFastestLapSectors[index]
                : null;
            var color = EstateRaceLapColorRules.Resolve(current, sessionBest, personalBest);
            return sector with
            {
                CurrentCompetitionBestSeconds = personalBest,
                HistoricalBestSeconds = sessionBest,
                DeltaSeconds = phaseFastestLapSector is double reference && reference > 0
                    ? current - reference
                    : null,
                State = color
            };
        }).ToArray();
        var comparisonCount = lap.ShowingPreviousLap
            ? sectors.Length
            : Math.Clamp(lap.CurrentSector, 0, sectors.Length);
        var cumulativeDelta = lap.CumulativeHistoricalDeltaSeconds is null
            ? null
            : EstateRaceLapDeltaRules.CumulativeToPhaseFastest(
                sectors.Select(sector => sector.CurrentSeconds).ToArray(),
                phaseFastestLapSectors,
                comparisonCount);
        return lap with
        {
            Sectors = sectors,
            CumulativeHistoricalDeltaSeconds = cumulativeDelta
        };
    }

    private static bool EstateRaceAllowsLapTiming(EstateRaceHudState? race)
    {
        if (race is null || !race.IsConnected || race.Session is not { } session) return true;
        if (session.Phase == RaceSessionPhase.Race) return true;
        if (session.Phase == RaceSessionPhase.Practice)
        {
            if (!session.PracticeTimeExpired) return true;
            return race.LocalParticipantId is Guid practiceParticipantId &&
                   session.Participants.FirstOrDefault(participant => participant.Id == practiceParticipantId)
                       ?.PracticeFinalLapPending == true;
        }
        if (session.Phase != RaceSessionPhase.Qualifying) return false;
        if (race.LocalParticipantId is Guid participantId &&
            session.Participants.FirstOrDefault(participant => participant.Id == participantId)?.QualifyingEligible == false)
            return false;
        if (!session.QualifyingTimeExpired) return true;
        return race.LocalParticipantId is Guid localId &&
               session.Participants.FirstOrDefault(participant => participant.Id == localId)?.QualifyingFinalLapPending == true;
    }

    private static string RacePhaseText(RaceSessionPhase phase) => phase switch
    {
        RaceSessionPhase.Lobby => "LOBBY",
        RaceSessionPhase.Practice => "PRACTICE",
        RaceSessionPhase.Grid => "GRID",
        RaceSessionPhase.OutLap => "OUT LAP",
        RaceSessionPhase.FormationLap => "FORMATION LAP",
        RaceSessionPhase.Countdown => "STARTING",
        RaceSessionPhase.Suspended => "SUSPENDED",
        RaceSessionPhase.Finished => "FINISHED",
        _ => phase.ToString().ToUpperInvariant()
    };

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
    }

    private static string GripConditionText(RaceGripCondition condition) => condition switch
    {
        RaceGripCondition.SlightlyReduced => "略微",
        RaceGripCondition.ModeratelyReduced => "中度",
        RaceGripCondition.SeverelyReduced => "严重",
        RaceGripCondition.AtLimit => "极限",
        _ => "采样中"
    };

    private static string BannerKindText(RaceBannerKind kind) => kind switch
    {
        RaceBannerKind.FastestLap => "FASTEST LAP",
        RaceBannerKind.Penalty => "PENALTY",
        RaceBannerKind.YellowFlag => "YELLOW FLAG",
        RaceBannerKind.RedFlag => "RED FLAG",
        RaceBannerKind.BlueFlag => "BLUE FLAG",
        RaceBannerKind.ChequeredFlag => "CHEQUERED FLAG",
        RaceBannerKind.Winner => "WINNER",
        _ => "RACE CONTROL"
    };

    private static Brush RaceThemeBrush(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Cyan;
        try
        {
            return ColorConverter.ConvertFromString(value) is Color color
                ? BrushOf(color.R, color.G, color.B)
                : Cyan;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            return Cyan;
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
            OverlayTextLocalization.Text("侧滑角"),
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
            OverlayTextLocalization.Text("控车余量"),
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
            OverlayTextLocalization.Text("油门"),
            BrushOf(0x28, 0xC6, 0x78));
        DrawDriftLevelBar(
            dc,
            new Rect(
                width * 0.52,
                height * 0.785,
                width * 0.25,
                height * 0.032),
            Math.Clamp(state.RearSlip / 1.35, 0, 1),
            OverlayTextLocalization.Text("后轮滑移"),
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
            OverlayTextLocalization.Text("方向"),
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
            OverlayTextLocalization.Text("积分速度"),
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
        var title = $"{OverlayTextLocalization.Text(state.TrackName)}  ·  {approximatePrefix}{FormatLapTime(state.CurrentLapSeconds)}";
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

    private static void RaceText(DrawingContext dc, string text, double x, double y, double size, Brush brush, TextAlignment alignment, bool strong = false)
    {
        if (size <= 0) return;
        var typeface = ContainsChinese(text)
            ? strong ? ChineseNormalTypeface : ChineseLightTypeface
            : strong ? RaceNormalTypeface : RaceLightTypeface;
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush, 1) { TextAlignment = alignment };
        dc.DrawText(formatted, new Point(x, y - formatted.Height / 2));
    }

    private static void RaceTitleText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        TextAlignment alignment)
    {
        if (size <= 0) return;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            RaceTitleTypeface,
            size,
            brush,
            1)
        {
            TextAlignment = alignment
        };
        dc.DrawText(formatted, new Point(x, y - formatted.Height / 2));
    }

    private static void DrawRaceStrategyItalicText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        TextAlignment alignment)
    {
        if (size <= 0) return;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            RaceStrategyTitleTypeface,
            size,
            brush,
            1)
        {
            TextAlignment = alignment
        };
        dc.DrawText(formatted, new Point(x, y - formatted.Height / 2));
    }

    private static void DrawRaceFinishTitle(
        DrawingContext dc,
        string text,
        Rect bounds,
        double size,
        Brush brush)
    {
        if (size <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            RaceTitleTypeface,
            size,
            brush,
            1)
        {
            TextAlignment = TextAlignment.Left,
            MaxTextWidth = bounds.Width,
            MaxTextHeight = bounds.Height,
            Trimming = TextTrimming.CharacterEllipsis
        };
        const double horizontalScale = 1.22;
        var glyphBounds = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        var baselinePoint = new Point(
            bounds.Left,
            bounds.Top + Math.Max(0, (bounds.Height - glyphBounds.Height) / 2) - glyphBounds.Top);
        dc.PushTransform(new ScaleTransform(horizontalScale, 1, bounds.Left, bounds.Top));
        dc.DrawText(formatted, baselinePoint);
        dc.Pop();
    }

    private static void RaceBoundedText(
        DrawingContext dc,
        string text,
        Rect bounds,
        double size,
        Brush brush,
        bool strong = false,
        TextAlignment alignment = TextAlignment.Center)
    {
        if (size <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var typeface = ContainsChinese(text)
            ? strong ? ChineseNormalTypeface : ChineseLightTypeface
            : strong ? RaceNormalTypeface : RaceLightTypeface;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            1)
        {
            TextAlignment = alignment,
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

    private static string FormatRaceTime(double? seconds)
    {
        if (seconds is not double value || !double.IsFinite(value) || value < 0) return "—";
        var span = TimeSpan.FromSeconds(value);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private static double EffectivePitServiceElapsed(EstatePitServiceState service, DateTimeOffset now)
    {
        var elapsed = Math.Max(0, service.ElapsedSeconds);
        if (service.IsCounting && !service.RequirementMet &&
            service.CountingUpdatedAt is DateTimeOffset updatedAt && now > updatedAt)
            elapsed += (now - updatedAt).TotalSeconds;
        return service.RequiredSeconds > 0
            ? Math.Min(service.RequiredSeconds, elapsed)
            : elapsed;
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
        return new SolidColorBrush(solid.Color) { Opacity = Math.Clamp(opacity, 0, 1) };
    }
    private static Brush FrozenLinearGradient(
        GradientStopCollection stops,
        Point startPoint,
        Point endPoint)
    {
        var brush = new LinearGradientBrush(stops, startPoint, endPoint);
        brush.Freeze();
        return brush;
    }
    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }
    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        maximumDelta = Math.Max(0, maximumDelta);
        if (Math.Abs(target - current) <= maximumDelta) return target;
        return current + Math.Sign(target - current) * maximumDelta;
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
    private static readonly Guid PreviewBannerId = Guid.Parse("4E8CD638-2365-48FC-AE45-80A4B7300001");
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
    public static EstateRaceHudState EstateRace(
        DateTimeOffset now,
        bool finished = false,
        bool chequered = false)
    {
        var participants = new[]
        {
            PreviewParticipant(1, "Antonelli", "#27F4D2", "Mercedes", 7, 0.76, 0.32, 68.424, false, false),
            PreviewParticipant(2, "Hamilton", "#E8002D", "Ferrari", 7, 0.61, 0.43, 68.612, false, false),
            PreviewParticipant(3, "Russell", "#27F4D2", "Mercedes", 7, 0.48, 0.62, 68.701, false, false),
            PreviewParticipant(4, "Leclerc", "#E8002D", "Ferrari", 7, 0.91, 0.78, 68.834, false, false),
            PreviewParticipant(5, "Norris", "#FF8700", "McLaren", 7, 0.72, 0.83, 68.956, false, false),
            PreviewParticipant(6, "Verstappen", "#3671C6", "Red Bull Racing", 7, 0.38, 0.21, 69.104, false, false),
            PreviewParticipant(7, "Piastri", "#FF8700", "McLaren", 7, 0.27, 0.36, 69.238, false, false),
            PreviewParticipant(8, "Hadjar", "#3671C6", "Red Bull Racing", 7, 0.19, 0.55, 69.407, false, false),
            PreviewParticipant(9, "Lawson", "#6692FF", "Racing Bulls", 6, 0.31, 0.74, 69.566, true, true),
            PreviewParticipant(10, "Gasly", "#FF87BC", "Alpine", 6, 0.55, 0.86, 69.712, false, false),
            PreviewParticipant(11, "Lindblad", "#6692FF", "Racing Bulls", 6, 0.82, 0.69, 69.884, false, false),
            PreviewParticipant(12, "Colapinto", "#FF87BC", "Alpine", 6, 0.88, 0.47, 70.063, true, false)
        };
        participants[1] = participants[1] with
        {
            HasPendingDriveThrough = true,
            IsServingDriveThrough = true,
            DriveThroughLapsRemaining = 2
        };
        var session = new EstateRaceSession(
            42,
            "英国塔地产大奖赛",
            RaceSessionPhase.Race,
            RaceControlFlag.Yellow,
            "第 2 分段 · 车辆在赛道上异常低速",
            "preview-track",
            "1",
            null,
            10,
            now.AddMinutes(-7),
            null,
            participants[0].Id,
            68.424,
            [18.201, 16.804, 17.312, 16.107],
            new EstateRaceBanner(
                PreviewBannerId,
                RaceBannerKind.FastestLap,
                "本场最快圈",
                "Antonelli  1:08.424",
                participants[0].Id,
                now,
                now.AddMinutes(1)),
            participants,
            now,
            [new EstateRaceYellowZone(1, true, "车辆在赛道上异常低速", participants[8].Id, participants[8].DisplayName)],
            4,
            true,
            "英国塔俱乐部环道");
        if (chequered && !finished)
        {
            session = session with
            {
                Flag = RaceControlFlag.Chequered,
                FlagMessage = "方格旗",
                Banner = null,
                YellowZones = [],
                ChequeredImminent = false
            };
        }
        if (finished)
        {
            var winnerTime = 5322.317;
            participants = participants
                .Select((participant, index) => participant with
                {
                    Status = RaceParticipantStatus.Finished,
                    CompletedLaps = 10,
                    IsInPitLane = false,
                    IsInServiceZone = false,
                    PitServiceElapsedSeconds = 0,
                    PitLaneElapsedSeconds = 0,
                    GapToLeaderSeconds = index == 0 ? 0 : index * 5.284,
                    IntervalSeconds = index == 0 ? 0 : 5.284,
                    RaceTotalSeconds = winnerTime + index * 5.284,
                    AdjustedRaceTotalSeconds = winnerTime + index * 5.284,
                    HasPendingDriveThrough = false,
                    IsServingDriveThrough = false,
                    DriveThroughLapsRemaining = null
                })
                .ToArray();
            session = session with
            {
                Phase = RaceSessionPhase.Finished,
                Flag = RaceControlFlag.Chequered,
                FlagMessage = "比赛结束",
                Participants = participants,
                Banner = null,
                YellowZones = [],
                RaceElapsedSeconds = winnerTime,
                ChequeredImminent = false
            };
        }
        var outline = Enumerable.Range(0, 121)
            .Select(index =>
            {
                var angle = index * Math.PI * 2 / 120;
                return new EstateRaceMapPoint(
                    0.5 + Math.Cos(angle) * (0.34 + 0.08 * Math.Sin(angle * 3)),
                    0.5 + Math.Sin(angle) * (0.40 + 0.06 * Math.Cos(angle * 2)));
            })
            .ToArray();
        var pitOutline = Enumerable.Range(0, 25)
            .Select(index =>
            {
                var amount = index / 24d;
                return new EstateRaceMapPoint(
                    0.77 - amount * 0.26,
                    0.64 + Math.Sin(amount * Math.PI) * 0.075);
            })
            .ToArray();
        var trackSectors = Enumerable.Range(0, 4)
            .Select(index => new EstateRaceMapSector(
                index,
                outline.Skip(index * 30).Take(31).ToArray()))
            .ToArray();
        return new EstateRaceHudState(
            now,
            EstateRaceConnectionState.Connected,
            "布局预览",
            participants[1].Id,
            session,
            outline,
            RaceGripCondition.ModeratelyReduced,
            "相比前三圈基准中度下降",
            finished
                ? EstatePitServiceState.Empty
                : new EstatePitServiceState(
                    true, false, 3, 0, false, 0, false, now,
                    8.6, true, 80, 72, false),
            pitOutline,
            new EstateRaceMapGate(
                new EstateRaceMapPoint(0.68, 0.17),
                new EstateRaceMapPoint(0.78, 0.22)),
            trackSectors,
            PracticeTests: new EstatePracticeTestPanelState(
                true,
                EstatePracticeTestKind.LongRun,
                [new EstatePracticeTestItemState(
                    EstatePracticeTestKind.LongRun,
                    "长距离轮胎管理",
                    "连续完成系统指定的干净圈。",
                    EstatePracticeTestStatus.Active,
                    "长距离进行中：保持正常比赛节奏，不要进站、暂停、回转或越过赛道边界。",
                    2,
                    6)],
                12));
    }

    private static EstateRaceParticipant PreviewParticipant(
        int position,
        string name,
        string color,
        string? team,
        int laps,
        double mapX,
        double mapY,
        double? best,
        bool inPit,
        bool inService) => new(
            PreviewParticipantId(position),
            position,
            name,
            color,
            team,
            inService ? RaceParticipantStatus.InService : inPit ? RaceParticipantStatus.InPitLane : RaceParticipantStatus.OnTrack,
            true,
            false,
            laps,
            2,
            Math.Clamp(mapX, 0, 1),
            mapX,
            mapY,
            inPit ? 24 : 178,
            41.2,
            best is null ? null : best + 0.4,
            best,
            position == 1 ? 0 : position * 1.284,
            position == 1 ? 0 : 1.284,
            inPit,
            inService,
            inService ? 2.4 : 0,
            false,
            0,
            RaceGripCondition.ModeratelyReduced,
            [18.201, 16.804, 17.312, 16.107],
            [],
            DateTimeOffset.UtcNow,
            TeamColor: color,
            PitLaneElapsedSeconds: inPit ? inService ? 9.8 : 14.2 : 0);

    private static Guid PreviewParticipantId(int position) =>
        Guid.Parse($"4E8CD638-2365-48FC-AE45-80A4B730{position:0000}");
}
