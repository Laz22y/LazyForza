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
        bool previewEstateRace = false)
    {
        var previousDriftPreview = driftSurface.LayoutPreview;
        var previousEstateRacePreview = estateRaceSurface.LayoutPreview;
        driftSurface.LayoutPreview = previewDrift;
        estateRaceSurface.LayoutPreview = previewEstateRace;
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

internal sealed class HudSurface : FrameworkElement
{
    private static readonly Typeface NormalTypeface = new(new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
    private static readonly Typeface LightTypeface = new(new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.Normal, FontStretches.Condensed);
    private static readonly Typeface ChineseNormalTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ChineseLightTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface RaceNormalTypeface = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface RaceLightTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
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
    private readonly Dictionary<Guid, PitHudRuntime> pitHudRuntime = [];
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
        limiter = new FrameRateLimiter(
            kind is HudSurfaceKind.Lap or HudSurfaceKind.EstateRace ? 30 : 60);
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
        var estateRace = orderedContributions.Select(item => item.Snapshot).OfType<EstateRaceHudState>().LastOrDefault();
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
            RenderLap(drawingContext, dashboard, lap, estateRace, now, layout);
            return;
        }

        if (kind == HudSurfaceKind.EstateRace)
        {
            RenderEstateRace(drawingContext, estateRace, now, layout);
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
        if (layoutPreview)
        {
            pitHudRuntime.Clear();
            state = OverlayLayoutPreviewState.EstateRace(now);
        }
        if (state?.Session is not { } session ||
            !state.IsConnected ||
            now - state.UpdatedAt > TimeSpan.FromSeconds(Math.Max(2, layout.LiveHudStaleSeconds * 4)))
            return;

        var widgets = layout.EstateRaceWidgets ?? EstateRaceHudLayoutSettings.Default;
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.Leaderboard),
            () => DrawRaceLeaderboard(dc, state, session));
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.TrackMap),
            () => DrawRaceTrackMap(dc, state, session));
        var localParticipant = session.Participants.FirstOrDefault(item => item.Id == state.LocalParticipantId);
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.GripStatus),
            () => DrawRaceGripStatus(dc, state),
            state.LocalGripCondition != RaceGripCondition.Unknown &&
            localParticipant is not { IsInPitLane: true } and not { IsInServiceZone: true });
        var banner = session.Banner;
        if (session.BlueFlags?.Any(item => item.RecipientParticipantId == state.LocalParticipantId) == true)
            banner = new EstateRaceBanner(
                Guid.Empty,
                RaceBannerKind.BlueFlag,
                "蓝旗 · 后方快车正在套圈",
                "请保持可预判路线，并在安全位置让行",
                state.LocalParticipantId,
                now,
                null);
        if (banner is not null &&
            (banner.ExpiresAt is null || banner.ExpiresAt > now))
        {
            DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.Banner),
                () => DrawRaceBanner(dc, banner));
        }
        var startLightSession = layoutPreview
            ? session with { IlluminatedStartLights = 5, StartLightsOut = false }
            : session;
        var estimatedServerNow = session.ServerTime + (now - state.UpdatedAt);
        var showStartLights = layoutPreview || session.Phase == RaceSessionPhase.Countdown ||
                              session.Phase == RaceSessionPhase.Race && session.StartLightsOut &&
                              session.StartsAt is DateTimeOffset lightsOutAt &&
                              estimatedServerNow - lightsOutAt < TimeSpan.FromSeconds(1);
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.StartLights),
            () => DrawRaceStartLights(dc, startLightSession), showStartLights);
        var pitViews = UpdatePitHud(session, state.LocalParticipantId, now);
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.PitStopInfo),
            () => DrawRacePitStopInfo(dc, pitViews),
            session.Phase == RaceSessionPhase.Race && pitViews.Count > 0);
        var limiterVisible = EstateRaceHudVisibilityPolicy.ShouldShowPitLimiter(state.PitService);
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.PitLimiter),
            () => DrawRacePitLimiter(dc, state.PitService), limiterVisible);
        var penaltyVisible = EstateRaceHudVisibilityPolicy.ShouldShowPenaltyStatus(
            session,
            localParticipant,
            estimatedServerNow);
        DrawRaceWidget(dc, widgets.Get(EstateRaceHudWidgetKind.PenaltyStatus),
            () => DrawRacePenaltyStatus(dc, localParticipant!), penaltyVisible);
    }

    private void DrawRaceWidget(
        DrawingContext dc,
        EstateRaceHudWidgetPlacement placement,
        Action draw,
        bool contentVisible = true)
    {
        if (!placement.IsVisible || !contentVisible || placement.Opacity <= 0.001) return;
        dc.PushOpacity(placement.Opacity);
        dc.PushTransform(new TranslateTransform(
            placement.Left * ActualWidth,
            placement.Top * ActualHeight));
        dc.PushTransform(new ScaleTransform(placement.Scale, placement.Scale));
        draw();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawRaceLeaderboard(
        DrawingContext dc,
        EstateRaceHudState state,
        EstateRaceSession session)
    {
        var width = ActualWidth * 0.235;
        var headerHeight = ActualHeight * 0.058;
        var rowHeight = Math.Max(36, ActualHeight * 0.045);
        var participants = session.Participants.Take(12).ToArray();
        var height = headerHeight + participants.Length * rowHeight;
        var qualifying = session.Phase is RaceSessionPhase.Qualifying or RaceSessionPhase.Grid ||
                         session.Phase == RaceSessionPhase.Suspended &&
                         session.SuspendedFromPhase == RaceSessionPhase.Qualifying;
        var race = session.Phase == RaceSessionPhase.Race ||
                   session.Phase == RaceSessionPhase.Suspended &&
                   session.SuspendedFromPhase == RaceSessionPhase.Race;
        var finished = session.Phase == RaceSessionPhase.Finished;
        var localBlue = session.Flag == RaceControlFlag.Green &&
                        session.BlueFlags?.Any(item => item.ApproachingParticipantId == state.LocalParticipantId) == true;
        var chequeredHeader = session.Flag == RaceControlFlag.Chequered ||
                              session.ChequeredImminent && session.Flag == RaceControlFlag.Green;
        var activeFlagHeader = localBlue || chequeredHeader ||
                               session.Flag is RaceControlFlag.Yellow or RaceControlFlag.Red;
        var accent = finished ? BrushOf(0xFF, 0xD1, 0x66) : qualifying
            ? BrushOf(0xB4, 0x63, 0xFF)
            : BrushOf(0x38, 0xD5, 0xE8);
        var headerFill = localBlue
            ? BrushOf(0x0B, 0x55, 0xD9)
            : session.Flag switch
            {
                RaceControlFlag.Yellow => BrushOf(0xF5, 0xC4, 0x00),
                RaceControlFlag.Red => BrushOf(0xE1, 0x06, 0x00),
                _ when chequeredHeader => BrushOf(0x0A, 0x0C, 0x10),
                _ when finished => BrushOf(0x25, 0x1D, 0x0D, 0.98),
                _ => BrushOf(0x11, 0x17, 0x20, 0.98)
            };
        var darkFlagText = session.Flag == RaceControlFlag.Yellow && !localBlue;
        var headerText = darkFlagText ? BrushOf(0x08, 0x0A, 0x0D) : White;
        var headerMuted = darkFlagText ? BrushOf(0x08, 0x0A, 0x0D, 0.74) : BrushOf(0xF3, 0xF6, 0xF9, 0.82);

        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.94),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.34), 1),
            new Rect(0, 0, width, height),
            8,
            8);
        dc.DrawRectangle(headerFill, null, new Rect(0, 0, width, headerHeight));
        if (chequeredHeader && !localBlue)
        {
            var cell = headerHeight / 3;
            var start = width * 0.56;
            for (var row = 0; row < 3; row++)
            for (var column = 0; start + column * cell < width; column++)
                if ((row + column) % 2 == 0)
                    dc.DrawRectangle(BrushOf(0xFF, 0xFF, 0xFF, 0.24), null,
                        new Rect(start + column * cell, row * cell, cell, cell));
            dc.DrawRectangle(new LinearGradientBrush(
                    Color.FromArgb(235, 0x0A, 0x0C, 0x10),
                    Color.FromArgb(13, 0x0A, 0x0C, 0x10),
                    new Point(0, 0.5),
                    new Point(1, 0.5)),
                null,
                new Rect(width * 0.42, 0, width * 0.38, headerHeight));
        }
        if (!activeFlagHeader)
        {
            dc.DrawRectangle(accent, null, new Rect(0, 0, Math.Max(4, width * 0.014), headerHeight));
            dc.DrawRectangle(BrushWithOpacity(accent, 0.72), null,
                new Rect(width * 0.055, headerHeight - 2, width * 0.89, 2));
        }
        var phase = localBlue
            ? "Blue Flag"
            : session.Flag == RaceControlFlag.Yellow
                ? "Yellow Flag"
                : session.Flag == RaceControlFlag.Red
                    ? "Red Flag"
                    : chequeredHeader
                        ? "Chequered Flag"
                        : finished
            ? "Final Classification"
            : qualifying
            ? "Qualifying"
            : session.Phase == RaceSessionPhase.Race
                ? "Race"
                : RacePhaseText(session.Phase);
        RaceText(dc, phase, width * 0.055, headerHeight * 0.50,
            Math.Max(12, headerHeight * 0.28), headerText, TextAlignment.Left, true);
        var headerDetail = localBlue
            ? "前方慢车收到蓝旗 · 保持可预判路线"
            : session.Flag switch
            {
                RaceControlFlag.Yellow => session.FlagMessage ?? "减速并禁止超车",
                RaceControlFlag.Red => session.FlagMessage ?? "比赛暂停 · 听从总控指令",
                RaceControlFlag.Chequered => "比赛进入完赛程序",
                _ when session.ChequeredImminent => "领跑者即将完成预定圈数",
                _ when finished => $"RACE TIME {FormatRaceTime(participants.FirstOrDefault()?.AdjustedRaceTotalSeconds)} · {participants.Count(item => item.Status == RaceParticipantStatus.Finished)} CLASSIFIED",
                _ when qualifying && session.QualifyingEndsAt is DateTimeOffset ending =>
                    $"REMAINING {FormatRemaining(ending - (session.ServerTime + (DateTimeOffset.UtcNow - state.UpdatedAt)))}",
                _ when session.Phase == RaceSessionPhase.Race && session.TotalRaceLaps > 0 =>
                    $"TIME {FormatRaceTime(session.RaceElapsedSeconds)} · LAP {Math.Max(1, participants.FirstOrDefault()?.CompletedLaps + 1 ?? 1)}/{session.TotalRaceLaps}",
                _ => session.Phase switch
                {
                    RaceSessionPhase.OutLap => "PROCEED TO THE GRID",
                    RaceSessionPhase.FormationLap => "FORMATION LAP",
                    RaceSessionPhase.Countdown => "START PROCEDURE",
                    _ => "SESSION READY"
                }
            };
        RaceBoundedText(dc, headerDetail,
            new Rect(width * 0.50, 0, width * 0.44, headerHeight),
            Math.Max(10, headerHeight * 0.22), activeFlagHeader ? headerMuted : Muted, true);

        for (var index = 0; index < participants.Length; index++)
        {
            var participant = participants[index];
            var top = headerHeight + index * rowHeight;
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
            if (participant.Id == state.LocalParticipantId)
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
            dc.DrawRectangle(RaceThemeBrush(participant.ThemeColor), null,
                new Rect(width * 0.018, top + rowHeight * 0.18, width * 0.010, rowHeight * 0.64));
            dc.DrawRoundedRectangle(
                local ? BrushOf(0xDE, 0xF8, 0xFC, 0.96) : BrushOf(0x25, 0x2E, 0x39, 0.90),
                null,
                new Rect(width * 0.043, top + rowHeight * 0.18, width * 0.095, rowHeight * 0.64),
                3,
                3);
            RaceText(dc, participant.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                width * 0.090, top + rowHeight * 0.5, Math.Max(12, rowHeight * 0.33),
                local ? BrushOf(0x08, 0x0B, 0x11) : White, TextAlignment.Center, true);
            if (participant.Id == session.FastestParticipantId)
                dc.DrawEllipse(BrushOf(0xB4, 0x63, 0xFF), null,
                    new Point(width * 0.158, top + rowHeight * 0.5),
                    Math.Max(2.5, rowHeight * 0.065),
                    Math.Max(2.5, rowHeight * 0.065));
            var nameWidth = width * 0.40;
            var hasTeam = session.AllowTeams && !string.IsNullOrWhiteSpace(participant.TeamName);
            RaceBoundedText(dc, participant.DisplayName,
                new Rect(width * 0.18, hasTeam ? top : top + rowHeight * 0.17, nameWidth, hasTeam ? rowHeight * 0.66 : rowHeight * 0.66),
                Math.Max(12, rowHeight * 0.31), White, true);
            if (hasTeam)
                RaceBoundedText(dc, participant.TeamName!,
                    new Rect(width * 0.18, top + rowHeight * 0.57, nameWidth, rowHeight * 0.34),
                    Math.Max(10, rowHeight * 0.19),
                    string.IsNullOrWhiteSpace(participant.TeamColor)
                        ? Muted
                        : RaceThemeBrush(participant.TeamColor!));
            var status = EstateRaceLeaderboardFormatter.Format(
                participant,
                qualifying,
                race,
                local,
                index + 1 < participants.Length ? participants[index + 1].IntervalSeconds : null,
                participants.FirstOrDefault()?.CompletedLaps ?? 0);
            var penaltyBadge = PendingPenaltyBadge(participant);
            var valueBrush = participant.IsInServiceZone || participant.IsInPitLane
                ? BrushOf(0xFF, 0xC4, 0x4D)
                : participant.Status == RaceParticipantStatus.Disqualified
                    ? BrushOf(0xFF, 0x45, 0x5F)
                    : participant.Id == session.FastestParticipantId && qualifying
                        ? BrushOf(0xC0, 0x63, 0xFF)
                        : White;
            dc.DrawRoundedRectangle(
                participant.IsInPitLane || participant.IsInServiceZone
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
            RaceText(dc, status, width * (penaltyBadge is null ? 0.935 : 0.81), top + rowHeight * 0.5,
                Math.Max(11, rowHeight * 0.27), valueBrush, TextAlignment.Right, true);
            if (penaltyBadge is not null)
                DrawLeaderboardPenaltyBadge(dc, penaltyBadge, width, top, rowHeight);
        }
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
        dc.DrawRectangle(BrushOf(0x38, 0xD5, 0xE8), null,
            new Rect(size * 0.065, size * 0.045, size * 0.16, 2));
        RaceText(dc, "TRACK MAP", size * 0.065, size * 0.105,
            Math.Max(11, size * 0.064), White, TextAlignment.Left, true);
        RaceText(dc, $"{session.Participants.Count(item => item.IsConnected)} LIVE",
            size * 0.935, size * 0.105, Math.Max(9, size * 0.048),
            BrushOf(0x38, 0xD5, 0xE8), TextAlignment.Right, true);
        var map = new Rect(size * 0.10, size * 0.18, size * 0.80, size * 0.70);
        if (state.TrackOutline.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var first = state.TrackOutline[0];
                context.BeginFigure(new Point(map.Left + first.X * map.Width, map.Top + first.Y * map.Height), false, false);
                context.PolyLineTo(state.TrackOutline.Skip(1)
                    .Select(point => new Point(map.Left + point.X * map.Width, map.Top + point.Y * map.Height)).ToArray(), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null,
                new Pen(BrushOf(0x00, 0x00, 0x00, 0.70), size * 0.038), geometry);
            dc.DrawGeometry(null,
                new Pen(BrushOf(0xC8, 0xD2, 0xDC, 0.82), size * 0.017), geometry);
        }
        foreach (var participant in session.Participants.Where(item => item.IsConnected))
        {
            var point = new Point(
                map.Left + Math.Clamp(participant.MapX, 0, 1) * map.Width,
                map.Top + Math.Clamp(participant.MapY, 0, 1) * map.Height);
            var local = participant.Id == state.LocalParticipantId;
            if (local)
                dc.DrawEllipse(BrushOf(0x38, 0xD5, 0xE8, 0.18), null,
                    point, size * 0.050, size * 0.050);
            dc.DrawEllipse(
                RaceThemeBrush(participant.ThemeColor),
                new Pen(local ? White : BrushOf(0x08, 0x0B, 0x11), size * 0.007),
                point,
                local ? size * 0.032 : size * 0.024,
                local ? size * 0.032 : size * 0.024);
            RaceText(dc, participant.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                point.X, point.Y, size * 0.035, BrushOf(0x05, 0x08, 0x0C), TextAlignment.Center, true);
        }
    }

    private IReadOnlyList<PitHudView> UpdatePitHud(
        EstateRaceSession session,
        Guid? localParticipantId,
        DateTimeOffset now)
    {
        if (session.Phase != RaceSessionPhase.Race)
        {
            pitHudRuntime.Clear();
            return [];
        }

        foreach (var participant in session.Participants)
        {
            var inPit = participant.IsInPitLane || participant.IsInServiceZone;
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
            if (participant.IsInServiceZone && participant.PitServiceRequirementMet &&
                runtime.ServiceCompletedAt is null)
            {
                runtime.ServiceCompletedAt = now;
                runtime.FrozenServiceSeconds = participant.PitServiceElapsedSeconds;
            }
            if (!inPit && runtime.WasInPit)
            {
                runtime.ExitedAt = now;
                runtime.FrozenPitLaneSeconds = participant.PitLaneElapsedSeconds > 0
                    ? participant.PitLaneElapsedSeconds
                    : Math.Max(0, (now - runtime.EnteredAt).TotalSeconds);
            }
            runtime.WasInPit = inPit;
            runtime.Position = participant.Position;
            runtime.DisplayName = participant.DisplayName;
            runtime.ThemeColor = participant.ThemeColor;
            runtime.IsInServiceZone = participant.IsInServiceZone;
            runtime.ServiceElapsedSeconds = participant.PitServiceElapsedSeconds;
            runtime.PitLaneElapsedSeconds = Math.Max(0, participant.PitLaneElapsedSeconds);
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
        return pitHudRuntime
            .Select(pair =>
            {
                var runtime = pair.Value;
                var showPenalty = runtime.IsServingPenalty || runtime.PenaltyServiceCompleted;
                var showService = !showPenalty && (runtime.ServiceCompletedAt is DateTimeOffset completedAt &&
                                   now - completedAt <= TimeSpan.FromSeconds(3) ||
                                   runtime.IsInServiceZone && runtime.ServiceCompletedAt is null);
                var seconds = showPenalty
                    ? runtime.PenaltyElapsedSeconds
                    : showService
                    ? runtime.ServiceCompletedAt is null
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
                    showService,
                    runtime.ServiceCompletedAt is not null && showService,
                    seconds,
                    runtime.WasInPit,
                    showPenalty,
                    runtime.PenaltyServiceCompleted,
                    runtime.PenaltyRequiredSeconds);
            })
            .OrderByDescending(item => item.ParticipantId == localParticipantId)
            .ThenBy(item => Math.Abs(item.Position - localPosition))
            .ThenBy(item => item.Position)
            .Take(2)
            .ToArray();
    }

    private void DrawRacePitStopInfo(DrawingContext dc, IReadOnlyList<PitHudView> entries)
    {
        var width = ActualWidth * 0.255;
        var headerHeight = ActualHeight * 0.033;
        var rowHeight = ActualHeight * 0.052;
        var height = headerHeight + rowHeight * entries.Count;
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.95),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.34), 1),
            new Rect(0, 0, width, height), 7, 7);
        dc.DrawRectangle(BrushOf(0xF4, 0xC5, 0x24), null, new Rect(0, 0, width, headerHeight));
        RaceText(dc, "PIT STOP", width * 0.045, headerHeight * 0.52,
            Math.Max(10, headerHeight * 0.34), BrushOf(0x08, 0x0A, 0x0D), TextAlignment.Left, true);
        RaceText(dc, entries.Count == 1 ? "1 CAR" : "2 CARS", width * 0.955, headerHeight * 0.52,
            Math.Max(9, headerHeight * 0.29), BrushOf(0x08, 0x0A, 0x0D, 0.75), TextAlignment.Right, true);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var top = headerHeight + index * rowHeight;
            if (index > 0)
                dc.DrawLine(new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.20), 1),
                    new Point(width * 0.04, top), new Point(width * 0.96, top));
            dc.DrawRectangle(RaceThemeBrush(entry.ThemeColor), null,
                new Rect(width * 0.018, top + rowHeight * 0.16, width * 0.012, rowHeight * 0.68));
            RaceText(dc, entry.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                width * 0.075, top + rowHeight * 0.50, Math.Max(12, rowHeight * 0.31), White,
                TextAlignment.Center, true);
            RaceBoundedText(dc, entry.DisplayName,
                new Rect(width * 0.12, top + rowHeight * 0.08, width * 0.36, rowHeight * 0.84),
                Math.Max(11, rowHeight * 0.29), White, true);
            var modeColor = entry.PenaltyCompleted || entry.ServiceCompleted
                ? BrushOf(0x4D, 0xD8, 0x91)
                : entry.IsPenalty ? BrushOf(0xFF, 0x45, 0x5F)
                : entry.IsService ? BrushOf(0xF4, 0xC5, 0x24) : BrushOf(0x38, 0xD5, 0xE8);
            RaceText(dc, entry.IsPenalty ? "PENALTY" : entry.IsService ? "STOP" : "PIT LANE",
                width * 0.56, top + rowHeight * 0.32, Math.Max(9, rowHeight * 0.19), modeColor,
                TextAlignment.Left, true);
            var secondsText = entry.IsPenalty && entry.PenaltyRequiredSeconds > 0
                ? $"{entry.Seconds:0.0}/{entry.PenaltyRequiredSeconds:0.#}"
                : $"{entry.Seconds:0.0}";
            RaceText(dc, secondsText, width * 0.93, top + rowHeight * 0.54,
                Math.Max(14, rowHeight * 0.36), White, TextAlignment.Right, true);
        }
    }

    private void DrawRacePitLimiter(DrawingContext dc, EstatePitServiceState pit)
    {
        var size = ActualHeight * 0.11;
        var center = new Point(size / 2, size / 2);
        var radius = size * 0.36;
        var over = pit.IsSpeeding;
        if (over)
            dc.DrawEllipse(BrushOf(0xFF, 0x2F, 0x46, 0.18), null, center, size * 0.49, size * 0.49);
        dc.DrawEllipse(BrushOf(0xF4, 0xF6, 0xF8, 0.97),
            new Pen(over ? BrushOf(0xFF, 0x2F, 0x46) : BrushOf(0xE2, 0x18, 0x2F), size * 0.075),
            center, radius, radius);
        RaceText(dc, Math.Round(pit.SpeedLimitKph).ToString(System.Globalization.CultureInfo.InvariantCulture),
            center.X, center.Y, size * 0.29, BrushOf(0x08, 0x0A, 0x0D), TextAlignment.Center, true);
        var indicator = over ? BrushOf(0xFF, 0x2F, 0x46) : pit.IsInPitLane
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

    private void DrawRacePenaltyStatus(DrawingContext dc, EstateRaceParticipant participant)
    {
        var width = ActualWidth * 0.25;
        var height = ActualHeight * 0.09;
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
        dc.DrawRoundedRectangle(accent, null,
            new Rect(0, 0, width, Math.Max(3, height * 0.055)), 8, 8);
        dc.DrawRoundedRectangle(BrushWithOpacity(accent, 0.13), null,
            new Rect(width * 0.035, height * 0.18, width * 0.13, height * 0.22), 3, 3);
        RaceText(dc, "RACE CONTROL", width * 0.10, height * 0.29,
            Math.Max(8, height * 0.105), accent, TextAlignment.Center, true);
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
            Math.Max(12, height * 0.19), White, TextAlignment.Left, true);
        RaceBoundedText(dc, detail,
            new Rect(width * 0.045, height * 0.67, width * 0.68, height * 0.22),
            Math.Max(9, height * 0.125), Muted, true);
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
            Math.Max(17, height * 0.29), completed ? accent : White, TextAlignment.Center, true);
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
        public int Position { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string ThemeColor { get; set; } = "#42D7E8";
        public double ServiceElapsedSeconds { get; set; }
        public double FrozenServiceSeconds { get; set; }
        public double FrozenPitLaneSeconds { get; set; }
        public double PitLaneElapsedSeconds { get; set; }
        public bool IsServingPenalty { get; set; }
        public bool PenaltyServiceCompleted { get; set; }
        public double PenaltyElapsedSeconds { get; set; }
        public double PenaltyRequiredSeconds { get; set; }
    }

    private sealed record PitHudView(
        Guid ParticipantId,
        int Position,
        string DisplayName,
        string ThemeColor,
        bool IsService,
        bool ServiceCompleted,
        double Seconds,
        bool IsInPit,
        bool IsPenalty,
        bool PenaltyCompleted,
        double PenaltyRequiredSeconds);

    private void DrawRaceStartLights(DrawingContext dc, EstateRaceSession session)
    {
        var width = ActualWidth * 0.30;
        var height = ActualHeight * 0.09;
        var spacing = width * 0.018;
        var cellWidth = (width - spacing * 4) / 5;
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
            if (illuminated)
            {
                dc.DrawEllipse(BrushOf(0xFF, 0x18, 0x2F, 0.18), null, center, radius * 1.55, radius * 1.55);
                dc.DrawEllipse(BrushOf(0xFF, 0x18, 0x2F, 0.42), null, center, radius * 1.22, radius * 1.22);
            }
            dc.DrawEllipse(
                illuminated ? BrushOf(0xFF, 0x21, 0x35) : BrushOf(0x35, 0x0B, 0x10, 0.78),
                new Pen(illuminated ? BrushOf(0xFF, 0x8A, 0x96, 0.95) : BrushOf(0x70, 0x32, 0x38, 0.65), 1),
                center,
                radius,
                radius);
            if (illuminated)
                dc.DrawEllipse(BrushOf(0xFF, 0xD8, 0xDC, 0.72), null,
                    new Point(center.X - radius * 0.24, center.Y - radius * 0.28),
                    radius * 0.16,
                    radius * 0.16);
        }
    }

    private void DrawRaceGripStatus(DrawingContext dc, EstateRaceHudState state)
    {
        var width = ActualWidth * 0.19;
        var height = ActualHeight * 0.085;
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
            Math.Max(10, height * 0.17), Muted, TextAlignment.Left, true);
        RaceText(dc, GripConditionText(state.LocalGripCondition), width * 0.070, height * 0.58,
            Math.Max(13, height * 0.25), color, TextAlignment.Left, true);
        RaceBoundedText(dc, state.GripExplanation,
            new Rect(width * 0.42, height * 0.08, width * 0.52, height * 0.46),
            Math.Max(9, height * 0.15),
            Muted,
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
        for (var index = 0; index < 4; index++)
        {
            dc.DrawRoundedRectangle(
                index < activeLevel ? color : BrushOf(0x5D, 0x67, 0x74, 0.25),
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

    private void DrawRaceBanner(DrawingContext dc, EstateRaceBanner banner)
    {
        var width = ActualWidth * 0.50;
        var height = ActualHeight * 0.082;
        var fill = banner.Kind switch
        {
            RaceBannerKind.FastestLap => BrushOf(0x9C, 0x43, 0xD7),
            RaceBannerKind.Penalty or RaceBannerKind.RedFlag => BrushOf(0xF2, 0x35, 0x4F),
            RaceBannerKind.BlueFlag => BrushOf(0x42, 0x8C, 0xFF),
            RaceBannerKind.YellowFlag => BrushOf(0xFF, 0xD3, 0x28),
            RaceBannerKind.ChequeredFlag or RaceBannerKind.Winner => BrushOf(0xE8, 0xEB, 0xEF),
            _ => BrushOf(0x42, 0xD7, 0xE8)
        };
        var darkText = banner.Kind is RaceBannerKind.YellowFlag or RaceBannerKind.ChequeredFlag or RaceBannerKind.Winner or RaceBannerKind.Information;
        dc.DrawRoundedRectangle(
            BrushOf(0x08, 0x0B, 0x11, 0.95),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.34), 1),
            new Rect(0, 0, width, height),
            8,
            8);
        dc.DrawRectangle(fill, null, new Rect(0, 0, width * 0.018, height));
        dc.DrawRectangle(fill, null, new Rect(width * 0.035, height * 0.16, width * 0.16, 2));
        var foreground = darkText ? fill : White;
        RaceText(dc, BannerKindText(banner.Kind), width * 0.035, height * 0.36,
            Math.Max(10, height * 0.15), foreground, TextAlignment.Left, true);
        RaceText(dc, banner.Title, width * 0.035, height * 0.70,
            Math.Max(15, height * 0.27), White, TextAlignment.Left, true);
        if (!string.IsNullOrWhiteSpace(banner.Detail))
            RaceBoundedText(dc, banner.Detail!,
                new Rect(width * 0.47, height * 0.08, width * 0.49, height * 0.84),
                Math.Max(12, height * 0.21), Muted, true);
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
        if (session.Phase != RaceSessionPhase.Qualifying) return false;
        if (!session.QualifyingTimeExpired) return true;
        return race.LocalParticipantId is Guid localId &&
               session.Participants.FirstOrDefault(participant => participant.Id == localId)?.QualifyingFinalLapPending == true;
    }

    private static string RacePhaseText(RaceSessionPhase phase) => phase switch
    {
        RaceSessionPhase.Lobby => "LOBBY",
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

    private static void RaceBoundedText(
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

    public static EstateRaceHudState EstateRace(DateTimeOffset now)
    {
        var participants = new[]
        {
            PreviewParticipant(1, "林线", "#42D7E8", "北辰车队", 7, 0.76, 0.32, 68.424, false, false),
            PreviewParticipant(2, "APEX-27", "#B86CFF", "北辰车队", 7, 0.61, 0.43, 68.932, false, false),
            PreviewParticipant(3, "青禾", "#B8F34A", "远山", 7, 0.48, 0.62, 69.180, true, true),
            PreviewParticipant(4, "NO.11", "#FF7C45", null, 6, 0.91, 0.78, 69.725, false, false),
            PreviewParticipant(5, "暮色", "#FF4057", "远山", 6, 0.72, 0.83, 70.018, true, false),
            PreviewParticipant(6, "VECTOR", "#5A8CFF", null, 6, 0.38, 0.21, null, false, false)
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
                Guid.NewGuid(),
                RaceBannerKind.FastestLap,
                "本场最快圈",
                "林线  1:08.424",
                participants[0].Id,
                now,
                now.AddMinutes(1)),
            participants,
            now,
            [new EstateRaceYellowZone(1, true, "车辆在赛道上异常低速", participants[4].Id, participants[4].DisplayName)],
            4,
            false,
            "英国塔俱乐部环道");
        var outline = Enumerable.Range(0, 121)
            .Select(index =>
            {
                var angle = index * Math.PI * 2 / 120;
                return new EstateRaceMapPoint(
                    0.5 + Math.Cos(angle) * (0.34 + 0.08 * Math.Sin(angle * 3)),
                    0.5 + Math.Sin(angle) * (0.40 + 0.06 * Math.Cos(angle * 2)));
            })
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
            new EstatePitServiceState(
                true, false, 3, 0, false, 0, false, now,
                8.6, true, 80, 72, false));
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
            Guid.NewGuid(),
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
            PitLaneElapsedSeconds: inPit ? inService ? 9.8 : 14.2 : 0);
}
