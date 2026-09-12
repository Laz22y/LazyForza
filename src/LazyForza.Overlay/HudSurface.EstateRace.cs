using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed partial class HudSurface
{
    private EstateRaceSceneKey? raceScene;
    private Guid? raceStageId;

    private readonly record struct EstateRaceSceneKey(
        Guid? EventId, Guid? ParticipantId, string? TrackId, string SessionName, bool Preview);

    private void ResetRaceScene(bool preserveStrategyClock = false)
    {
        raceScene = null;
        raceStageId = null;
        estateRaceWidgetAnimations.Reset();
        raceWidgetDrawings.Clear();
        raceWidgetVisuals.Clear();
        raceMapPointRuntime.Clear();
        leaderboardRowRuntime.Clear();
        leaderboardValueRuntime.Clear();
        pitStopRowRuntime.Clear();
        pitHudRuntime.Clear();
        pitWindowHudRuntime.Reset();
        if (!preserveStrategyClock) fullRaceStrategyHudRuntime.Reset();
        raceComparisonCache.Reset();
        raceHeaderSignal = previousRaceHeaderSignal = RaceHeaderSignal.None;
        raceHeaderTransitionStartedAt = double.NegativeInfinity;
        raceMapFlagVisualKey = previousRaceMapFlagVisualKey = null;
        raceMapFlagTransitionStartedSeconds = double.NegativeInfinity;
        Array.Clear(startLightLevels);
        previousStartLightRenderSeconds = previousPracticeProgramRenderSeconds = double.NaN;
        animatedPracticeProgramKind = null;
        animatedPracticeProgress = 0;
        practiceGuidanceAnimationKey = null;
        smoothAnimationUntilSeconds = double.NegativeInfinity;
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
        estateRaceAnimationNowSeconds = monotonicSeconds?.Invoke() ?? clock.Elapsed.TotalSeconds;
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
        {
            ResetRaceScene();
            return;
        }
        var networkQuality = SelectRaceNetworkQuality(state, now);
        if (now - state.UpdatedAt > (networkQuality != EstateRaceNetworkQuality.Normal
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromSeconds(Math.Max(2, layout.LiveHudStaleSeconds * 4))))
        {
            ResetRaceScene();
            return;
        }

        var scene = new EstateRaceSceneKey(session.EventId, state.LocalParticipantId,
            session.TrackId, session.SessionName, layoutPreview);
        if (raceScene != scene || raceStageId != session.StageId)
        {
            ResetRaceScene(preserveStrategyClock: raceScene == scene);
            raceScene = scene;
            raceStageId = session.StageId;
        }

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
            var group = EstateRaceDrawingLayers.Record(draw);
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
        if (runtime.Outgoing is not null && contentProgress < 1) raceWidgetContentAnimation = true;
        else runtime.Outgoing = null;
        drawing.Draw(dc, getLayout().EstateRaceBackdropOpacity, runtime.Outgoing, contentProgress);
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

    private void DrawRaceTrackMap(
        DrawingContext dc,
        EstateRaceHudState state,
        EstateRaceSession session)
    {
        var size = Math.Min(ActualWidth * 0.19, ActualHeight * 0.28);
        EstateRaceDrawingLayers.Panel(dc, 
            BrushOf(0x08, 0x0B, 0x11, 0.91),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.32), 1),
            new Rect(0, 0, size, size),
            9,
            9);
        var map = new Rect(size * 0.10, size * 0.12, size * 0.80, size * 0.76);
        if (state.TrackOutline.Count >= 2)
        {
            var geometry = raceMapGeometryCache.Track(state.TrackOutline, map);
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
            var geometry = raceMapGeometryCache.Pit(pitLane, map);
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

    private void DrawRaceMapFlagOverlay(
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
                raceMapGeometryCache.Sector(sector.Points, map));
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
        EstateRaceDrawingLayers.Panel(dc, 
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
            EstateRaceDrawingLayers.Panel(dc, 
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
        EstateRaceDrawingLayers.Panel(dc, BrushOf(0x00, 0x00, 0x00, 0.34),
            null,
            new Rect(2, 3, width, height),
            9,
            9);
        EstateRaceDrawingLayers.Panel(dc, 
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
        public EstateRaceDrawingLayers? Current { get; set; }
        public EstateRaceDrawingLayers? Outgoing { get; set; }
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
        EstateRaceDrawingLayers.Panel(dc, BrushOf(0x08, 0x0B, 0x11, 0.93),
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
        EstateRaceDrawingLayers.Panel(dc,
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
        if (textWidth <= bounds.Width + 0.5 || estateRaceReduceMotion)
        {
            formatted.MaxTextWidth = bounds.Width;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
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
        EstateRaceDrawingLayers.Panel(dc, 
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

}
