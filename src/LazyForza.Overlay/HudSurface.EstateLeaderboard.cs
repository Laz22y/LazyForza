using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed partial class HudSurface
{
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
        var reduceMotion = estateRaceReduceMotion;
        var transitioningFromChequered = finished && previousRaceHeaderSignal == RaceHeaderSignal.Chequered;
        var transitionDuration = targetSignal == RaceHeaderSignal.Chequered
            ? 0.62
            : transitioningFromChequered
                ? 0.58
                : 0.22;
        var transitionProgress = reduceMotion
            ? 1
            : Math.Clamp((estateRaceAnimationNowSeconds - raceHeaderTransitionStartedAt) / transitionDuration, 0, 1);
        transitionProgress = 1 - Math.Pow(1 - transitionProgress, 3);
        var accent = premiumFinishHeader
            ? RaceChequeredGoldAccent
            : qualifying
            ? BrushOf(0xB4, 0x63, 0xFF)
            : BrushOf(0x38, 0xD5, 0xE8);
        EstateRaceDrawingLayers.Panel(dc, 
            BrushOf(0x08, 0x0B, 0x11, 0.94),
            new Pen(BrushOf(0x8B, 0x9A, 0xAA, 0.34), 1),
            new Rect(0, 0, width, height),
            8,
            8);
        var leaderboardClip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        leaderboardClip.Freeze();
        dc.PushClip(leaderboardClip);
        EstateRaceDrawingLayers.Panel(dc, 
            premiumFinishHeader
                ? RaceChequeredHeaderBackground
                : BrushOf(0x10, 0x16, 0x20, 0.99),
            null,
            new Rect(0, 0, width, topHeaderHeight));
        EstateRaceDrawingLayers.Panel(dc, 
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
        var stageDetail = RaceStageDetail(session, participants, estimatedServerNow);
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

    private static string RaceStageDetail(EstateRaceSession session,
        IReadOnlyList<EstateRaceParticipant> participants, DateTimeOffset estimatedServerNow) =>
        session.Phase switch
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

    private void UpdateRaceHeaderSignal(RaceHeaderSignal target)
    {
        if (target == raceHeaderSignal) return;
        previousRaceHeaderSignal = raceHeaderSignal;
        raceHeaderSignal = target;
        raceHeaderTransitionStartedAt = estateRaceAnimationNowSeconds;
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
        var animationSeconds = reduceMotion ? 0 : estateRaceAnimationNowSeconds;
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
        var pulse = estateRaceReduceMotion ? 1 : 0.76 + 0.24 * Math.Sin(estateRaceAnimationNowSeconds * Math.PI * 5);
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

}
