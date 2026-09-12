using System.Windows;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed partial class HudSurface
{
    private static readonly Brush BroadcastInk = BrushOf(0x11, 0x19, 0x23);
    private static readonly Brush BroadcastPaper = BrushOf(0xEB, 0xF0, 0xF3);
    private static readonly Brush BroadcastMuted = BrushOf(0x8A, 0xA0, 0xB1);
    private static readonly Brush BroadcastCyan = BrushOf(0x72, 0xD9, 0xEC);

    private static void BroadcastPanel(DrawingContext dc, double width, double height, Brush? accent = null)
    {
        EstateRaceDrawingLayers.Panel(dc, BrushOf(0x0B, 0x12, 0x1B, 0.94), null,
            new Rect(0, 0, width, height));
        if (accent is not null) dc.DrawRectangle(accent, null, new Rect(0, 0, Math.Max(3, width * 0.008), height));
    }

    private static void BroadcastText(DrawingContext dc, string value, Rect bounds, double size,
        Brush? color = null, TextAlignment alignment = TextAlignment.Left, double baseline = 0.70, bool strong = false) =>
        HudTypography.Draw(dc, value, bounds, bounds.Top + bounds.Height * baseline,
            size, color ?? White, alignment, strong);

    private void DrawBroadcastLeaderboard(DrawingContext dc, LeaderboardContent content)
    {
        var state = content.State;
        var session = content.Session;
        var participants = session.Participants.Take(12).ToArray();
        var local = participants.FirstOrDefault(p => p.Id == state.LocalParticipantId);
        var leader = participants.FirstOrDefault(p => p.Position == 1) ?? participants.FirstOrDefault();
        var effectivePhase = session.Phase == RaceSessionPhase.Suspended ? session.SuspendedFromPhase : session.Phase;
        var qualifying = effectivePhase is RaceSessionPhase.Qualifying or RaceSessionPhase.Grid;
        var timed = qualifying || effectivePhase == RaceSessionPhase.Practice;
        var racing = effectivePhase == RaceSessionPhase.Race;
        var finished = session.Phase == RaceSessionPhase.Finished;
        var signal = finished ? RaceHeaderSignal.None : SelectRaceHeaderSignal(session, state.LocalParticipantId, content.Network);
        UpdateRaceHeaderSignal(signal);
        var width = ActualWidth * 0.235;
        var titleHeight = ActualHeight * 0.053;
        var detailHeight = ActualHeight * 0.026;
        var header = titleHeight + detailHeight;
        var footerHeight = ActualHeight * 0.030;
        var rowHeight = Math.Max(36, ActualHeight * 0.045) - footerHeight / 12;
        var bodyBottom = header + rowHeight * participants.Length;
        BroadcastPanel(dc, width, bodyBottom + footerHeight);
        // White plates are semantic contrast surfaces, like a speed-limit sign;
        // they remain opaque when only dark panel backgrounds are reduced.
        dc.DrawRectangle(BroadcastPaper, null, new Rect(0, 0, width, titleHeight));
        var logo = new Rect(width * 0.025, titleHeight * 0.15, width * 0.13, titleHeight * 0.70);
        if (state.OrganizerLogo is not null)
        {
            dc.DrawRectangle(BroadcastInk, null, logo);
            DrawRaceOrganizerLogo(dc, state.OrganizerLogo, logo);
        }
        var title = signal != RaceHeaderSignal.None ? RaceHeaderSignalText(signal) : finished ? "FINAL" : RacePhaseText(session.Phase).ToUpperInvariant();
        var titleLeft = state.OrganizerLogo is null ? 0.05 : 0.19;
        BroadcastText(dc, title, new Rect(width * titleLeft, 0, width * (0.72 - titleLeft), titleHeight),
            titleHeight * (signal == RaceHeaderSignal.None ? 0.56 : 0.32), BroadcastInk, strong: true);
        if ((racing || finished) && session.TotalRaceLaps > 0)
        {
            BroadcastText(dc, "LAP", new Rect(width * 0.72, titleHeight * 0.04, width * 0.23, titleHeight * 0.26),
                Math.Max(10, titleHeight * 0.17), BrushOf(0x60, 0x76, 0x86), TextAlignment.Right);
            BroadcastText(dc, $"{DisplayedRaceLap(leader, session.TotalRaceLaps):00} / {session.TotalRaceLaps:00}",
                new Rect(width * 0.72, titleHeight * 0.29, width * 0.23, titleHeight * 0.55), titleHeight * 0.35,
                BroadcastInk, TextAlignment.Right, strong: true);
        }
        dc.DrawRectangle(signal == RaceHeaderSignal.None ? BroadcastCyan : RaceHeaderSignalColor(signal), null,
            new Rect(0, titleHeight - 2, width, 2));
        if (session.Phase == RaceSessionPhase.Race)
        {
            BroadcastText(dc, session.SessionName, new Rect(width * 0.04, titleHeight, width * 0.55, detailHeight),
                Math.Max(11, detailHeight * 0.40), BroadcastMuted);
            BroadcastText(dc, FormatRaceTime(EstimatedRaceElapsedSeconds(session, content.ServerNow)),
                new Rect(width * 0.61, titleHeight, width * 0.34, detailHeight), detailHeight * 0.49,
                White, TextAlignment.Right);
        }
        else BroadcastText(dc, RaceStageDetail(session, participants, content.ServerNow),
            new Rect(width * 0.04, titleHeight, width * 0.92, detailHeight), Math.Max(11, detailHeight * 0.43), BroadcastMuted);

        var initial = leaderboardRowRuntime.Count == 0;
        var ids = participants.Select(p => p.Id).ToHashSet();
        foreach (var stale in leaderboardRowRuntime.Keys.Where(id => !ids.Contains(id)).ToArray()) leaderboardRowRuntime.Remove(stale);
        foreach (var stale in leaderboardValueRuntime.Keys.Where(id => !ids.Contains(id)).ToArray()) leaderboardValueRuntime.Remove(stale);
        dc.PushClip(new RectangleGeometry(new Rect(0, header, width, rowHeight * participants.Length)));
        for (var index = 0; index < participants.Length; index++)
        {
            var participant = participants[index];
            if (!leaderboardRowRuntime.TryGetValue(participant.Id, out var row))
                leaderboardRowRuntime[participant.Id] = row = new AnimatedRowRuntime(index + (initial ? 0 : 0.22), initial ? 1 : 0, estateRaceAnimationNowSeconds);
            var visual = row.Update(index, estateRaceAnimationNowSeconds, estateRaceReduceMotion, 0.24, 0.18);
            leaderboardRowAnimation |= visual.IsAnimating;
            var top = header + visual.Position * rowHeight;
            dc.PushOpacity(visual.Opacity);
            var isLocal = participant.Id == state.LocalParticipantId;
            var elimination = QualifyingEliminationState(session, participant);
            var eliminated = elimination == QualifyingEliminationVisualState.Eliminated;
            var foreground = isLocal ? BroadcastInk : eliminated ? BroadcastMuted : White;
            if (isLocal)
            {
                dc.DrawRectangle(BroadcastPaper, null, new Rect(0, top, width, rowHeight - 1));
                dc.DrawRectangle(RaceThemeBrush(participant.ThemeColor), null, new Rect(width - 3, top, 3, rowHeight - 1));
            }
            else EstateRaceDrawingLayers.Panel(dc, index % 2 == 0 ? BrushOf(0x1C, 0x28, 0x35, 0.68)
                : BrushOf(0x16, 0x21, 0x2D, 0.56), null, new Rect(0, top, width, rowHeight - 1));
            var positionFill = elimination == QualifyingEliminationVisualState.AtRisk ? BrushOf(0xEB, 0x5A, 0x68)
                : isLocal ? BroadcastPaper : BrushOf(0xCF, 0xDA, 0xE2);
            var plateWidth = width * 0.115;
            var plateShape = new StreamGeometry();
            using (var shape = plateShape.Open())
            {
                shape.BeginFigure(new Point(0, top), true, true);
                shape.PolyLineTo([new Point(plateWidth, top), new Point(plateWidth, top + rowHeight - 8),
                    new Point(plateWidth - 7, top + rowHeight - 1), new Point(0, top + rowHeight - 1)], true, false);
            }
            plateShape.Freeze();
            dc.DrawGeometry(positionFill, null, plateShape);
            BroadcastText(dc, participant.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Rect(0, top, width * 0.115, rowHeight), rowHeight * 0.55, BroadcastInk, TextAlignment.Center, strong: true);
            dc.DrawRectangle(RaceThemeBrush(participant.ThemeColor), null,
                new Rect(width * 0.145, top + rowHeight * 0.20, Math.Max(2, width * 0.007), rowHeight * 0.56));
            if (participant.Id == session.FastestParticipantId)
                dc.DrawRectangle(BrushOf(0xC0, 0x73, 0xED), null, new Rect(width * 0.121, top + 1, width * 0.012, rowHeight - 3));

            var hasTeam = session.AllowTeams && !string.IsNullOrWhiteSpace(participant.TeamName);
            var name = new Rect(width * 0.18, top, width * 0.40, rowHeight);
            var value = new Rect(width * 0.61, top, width * 0.35, rowHeight);
            var hasBadges = PendingPenaltyBadge(participant) is not null || HasPendingInvestigation(session, participant.Id)
                || ShouldShowLeaderboardPitBadge(session, participant) || ShouldShowLeaderboardFinishBadge(session, participant);
            var mainBaseline = hasTeam || hasBadges ? 0.49 : 0.68;
            BroadcastText(dc, participant.DisplayName, name, rowHeight * 0.40, foreground, baseline: mainBaseline, strong: true);
            if (hasTeam) BroadcastText(dc, participant.TeamName!, name, Math.Max(10, rowHeight * 0.23),
                isLocal ? BrushOf(0x50, 0x64, 0x74) : BroadcastMuted, baseline: 0.87);
            var pit = ShouldShowLeaderboardPitBadge(session, participant);
            var status = finished ? EstateRaceLeaderboardFormatter.FormatFinished(participant, leader, leader?.CompletedLaps ?? 0)
                : participant.QualifyingEliminatedInSession is int q && qualifying ? $"OUT Q{q}"
                : raceComparisonCache.Format(participant, local, timed, racing, participants, DateTimeOffset.UtcNow, showPitStatus: !pit);
            if (status == "REFERENCE") status = "REF";
            BroadcastText(dc, status, value, rowHeight * 0.42, foreground, TextAlignment.Right, mainBaseline, strong: true);
            var badges = new List<string>(3);
            if (PendingPenaltyBadge(participant) is { } penalty) badges.Add(penalty);
            if (HasPendingInvestigation(session, participant.Id)) badges.Add("INV");
            if (pit) badges.Add("PIT");
            if (ShouldShowLeaderboardFinishBadge(session, participant)) badges.Add("FIN");
            if (badges.Count > 0) BroadcastText(dc, string.Join(" · ", badges), value, Math.Max(10, rowHeight * 0.23),
                isLocal ? BroadcastInk : BrushOf(0xF1, 0xC5, 0x6E), TextAlignment.Right, 0.91);
            dc.Pop();
        }
        dc.Pop();
        EstateRaceDrawingLayers.Panel(dc, BrushOf(0x06, 0x0C, 0x13, 0.90), null, new Rect(0, bodyBottom, width, footerHeight));
        dc.DrawRectangle(BrushOf(0x55, 0x73, 0x89, 0.4), null, new Rect(0, bodyBottom, width, 1));
        BroadcastText(dc, $"{session.Participants.Count} DRIVERS", new Rect(width * 0.04, bodyBottom, width * 0.5, footerHeight),
            Math.Max(10, footerHeight * 0.34), BroadcastMuted);
        var brand = HudBrandAssets.Wordmark;
        var brandWidth = width * 0.25;
        var brandHeight = brandWidth * brand.PixelHeight / brand.PixelWidth;
        dc.DrawImage(brand, new Rect(width * 0.70, bodyBottom + (footerHeight - brandHeight) / 2, brandWidth, brandHeight));
    }

    private void DrawBroadcastMap(DrawingContext dc, MapContent content) =>
        DrawRaceTrackMap(dc, content.State, content.Session, broadcast: true);

    private void DrawBroadcastBanner(DrawingContext dc, EstateRaceBanner banner)
    {
        var width = ActualWidth * 0.50;
        var height = ActualHeight * 0.09;
        var accent = banner.IsInvestigation ? BrushOf(0xF2, 0xC8, 0x60) : banner.Kind switch
        {
            RaceBannerKind.YellowFlag => BrushOf(0xF2, 0xC8, 0x60),
            RaceBannerKind.RedFlag or RaceBannerKind.Penalty => BrushOf(0xF2, 0x50, 0x60),
            RaceBannerKind.BlueFlag => BrushOf(0x69, 0xA2, 0xFF),
            RaceBannerKind.FastestLap => BrushOf(0xC0, 0x73, 0xED),
            RaceBannerKind.ChequeredFlag or RaceBannerKind.Winner => BroadcastPaper,
            _ => BroadcastCyan
        };
        BroadcastPanel(dc, width, height);
        var plate = width * 0.11;
        dc.DrawRectangle(accent, null, new Rect(0, 0, plate, height));
        var flag = new StreamGeometry();
        using (var geometry = flag.Open())
        {
            geometry.BeginFigure(new Point(plate * 0.28, height * 0.77), false, false);
            geometry.LineTo(new Point(plate * 0.28, height * 0.25), true, false);
            geometry.LineTo(new Point(plate * 0.75, height * 0.25), true, false);
            geometry.LineTo(new Point(plate * 0.63, height * 0.42), true, false);
            geometry.LineTo(new Point(plate * 0.75, height * 0.57), true, false);
            geometry.LineTo(new Point(plate * 0.28, height * 0.57), true, false);
        }
        flag.Freeze();
        dc.DrawGeometry(null, new Pen(BroadcastInk, Math.Max(2, height * 0.026)), flag);
        var hasDetail = !string.IsNullOrWhiteSpace(banner.Detail);
        BroadcastText(dc, banner.IsInvestigation ? "UNDER INVESTIGATION" : BannerKindText(banner.Kind),
            new Rect(width * 0.135, height * 0.05, width * 0.50, height * 0.29), Math.Max(11, height * 0.13), accent);
        BroadcastText(dc, OverlayTextLocalization.Text(banner.Title),
            new Rect(width * 0.135, height * 0.33, width * (hasDetail ? 0.52 : 0.83), height * 0.61),
            height * 0.32, strong: true);
        if (hasDetail) BroadcastText(dc, OverlayTextLocalization.Text(banner.Detail!),
            new Rect(width * 0.69, height * 0.18, width * 0.28, height * 0.66), Math.Max(12, height * 0.15), BroadcastMuted);
        var accentColor = ((SolidColorBrush)accent).Color;
        dc.DrawRectangle(BrushOf(accentColor.R, accentColor.G, accentColor.B, 0.30), null,
            new Rect(plate, height - 3, width - plate, 3));
    }

    private void DrawBroadcastGrip(DrawingContext dc, EstateRaceHudState state)
    {
        var width = ActualWidth * 0.20;
        var height = ActualHeight * 0.095;
        var level = state.LocalGripCondition switch
        {
            RaceGripCondition.SlightlyReduced => 1, RaceGripCondition.ModeratelyReduced => 2,
            RaceGripCondition.SeverelyReduced => 3, RaceGripCondition.AtLimit => 4, _ => 0
        };
        var accent = level >= 4 ? BrushOf(0xF2, 0x50, 0x60) : level == 3 ? BrushOf(0xF2, 0x82, 0x42)
            : level == 1 ? BrushOf(0x4D, 0xD8, 0x91) : BrushOf(0xED, 0xC1, 0x69);
        BroadcastPanel(dc, width, height);
        BroadcastText(dc, "GRIP", new Rect(width * 0.055, 0, width * 0.40, height * 0.32), Math.Max(11, height * 0.13), BroadcastMuted);
        BroadcastText(dc, OverlayTextLocalization.Text(GripConditionText(state.LocalGripCondition)),
            new Rect(width * 0.055, height * 0.29, width * 0.89, height * 0.41), height * 0.25, White, strong: true);
        BroadcastText(dc, OverlayTextLocalization.Text(state.GripExplanation),
            new Rect(width * 0.055, height * 0.72, width * 0.89, height * 0.25), Math.Max(11, height * 0.12), BroadcastMuted);
        for (var i = 0; i < 4; i++) dc.DrawRectangle(i < level ? accent : BrushOf(0x34, 0x43, 0x51), null,
            new Rect(width * (0.58 + i * 0.09), height * 0.18, width * 0.065, height * 0.045));
    }

    private void DrawBroadcastStartLights(DrawingContext dc, EstateRaceSession session)
    {
        var width = ActualWidth * 0.30;
        var height = ActualHeight * 0.09;
        BroadcastPanel(dc, width, height);
        var elapsed = double.IsFinite(previousStartLightRenderSeconds)
            ? Math.Clamp(estateRaceAnimationNowSeconds - previousStartLightRenderSeconds, 0, 0.1) : 0;
        previousStartLightRenderSeconds = estateRaceAnimationNowSeconds;
        for (var i = 0; i < 5; i++)
        {
            var lit = !session.StartLightsOut && i < session.IlluminatedStartLights;
            var target = lit ? 1d : 0d;
            startLightLevels[i] = estateRaceReduceMotion ? target : MoveTowards(startLightLevels[i], target, elapsed / (lit ? 0.10 : 0.05));
            startLightAnimation |= Math.Abs(startLightLevels[i] - target) > 0.001;
            var level = SmoothStep(startLightLevels[i]);
            var center = new Point(width * (0.1 + i * 0.2), height * 0.5);
            var radius = Math.Min(height * 0.29, width * 0.064);
            dc.DrawEllipse(BlendBrush(BrushColor(0x32, 0x14, 0x1A), BrushColor(0xFF, 0x32, 0x48), level, 1),
                new Pen(BrushOf(0x64, 0x39, 0x45), 1), center, radius, radius);
            if (level > 0) dc.DrawEllipse(null, new Pen(BrushOf(0xFF, 0x38, 0x4E, level * 0.25), Math.Max(2, height * 0.045)),
                center, radius * 1.23, radius * 1.23);
        }
    }

    private void DrawBroadcastLimiter(DrawingContext dc, EstatePitServiceState pit)
    {
        var size = ActualHeight * 0.11;
        dc.DrawRectangle(BroadcastPaper, null, new Rect(size * 0.08, size * 0.08, size * 0.84, size * 0.84));
        dc.DrawRectangle(BrushOf(0xE8, 0x25, 0x43), null, new Rect(size * 0.08, size * 0.08, size * 0.84, size * 0.07));
        BroadcastText(dc, $"{pit.SpeedLimitKph:0}", new Rect(size * 0.10, size * 0.18, size * 0.80, size * 0.51), size * 0.40,
            pit.IsSpeeding ? BrushOf(0xD6, 0x20, 0x3E) : BroadcastInk, TextAlignment.Center);
        BroadcastText(dc, pit.IsSpeeding ? "SLOW DOWN" : "KM/H", new Rect(size * 0.10, size * 0.65, size * 0.80, size * 0.21),
            size * 0.115, BroadcastInk, TextAlignment.Center);
    }

    private void DrawBroadcastPitStop(DrawingContext dc, PitHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.215;
        var header = ActualHeight * 0.041;
        var rowHeight = ActualHeight * 0.0665;
        BroadcastPanel(dc, width, header + rowHeight * snapshot.Entries.Count);
        dc.DrawRectangle(BroadcastCyan, null, new Rect(0, 0, width, 2));
        BroadcastText(dc, "PIT STOP", new Rect(width * 0.045, 0, width * 0.50, header), header * 0.47, strong: true);
        BroadcastText(dc, $"{snapshot.ActiveParticipantCount} PLAYERS", new Rect(width * 0.57, 0, width * 0.38, header),
            Math.Max(11, header * 0.26), BroadcastMuted, TextAlignment.Right);
        var initial = pitStopRowRuntime.Count == 0;
        var ids = snapshot.Entries.Select(e => e.ParticipantId).ToHashSet();
        foreach (var id in pitStopRowRuntime.Keys.Where(id => !ids.Contains(id)).ToArray()) pitStopRowRuntime.Remove(id);
        for (var i = 0; i < snapshot.Entries.Count; i++)
        {
            var entry = snapshot.Entries[i];
            if (!pitStopRowRuntime.TryGetValue(entry.ParticipantId, out var row))
                pitStopRowRuntime[entry.ParticipantId] = row = new AnimatedRowRuntime(i + (initial ? 0 : 0.18), initial ? 1 : 0, estateRaceAnimationNowSeconds);
            var visual = row.Update(i, estateRaceAnimationNowSeconds, estateRaceReduceMotion, 0.20, 0.18);
            pitStopRowAnimation |= visual.IsAnimating;
            var top = header + visual.Position * rowHeight;
            dc.PushOpacity(visual.Opacity);
            BroadcastText(dc, entry.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Rect(width * 0.035, top, width * 0.105, rowHeight), rowHeight * 0.43, baseline: 0.51, strong: true);
            dc.DrawRectangle(RaceThemeBrush(entry.ThemeColor), null, new Rect(width * 0.15, top + rowHeight * 0.20, 2, rowHeight * 0.5));
            var name = new Rect(width * 0.18, top, width * 0.42, rowHeight);
            BroadcastText(dc, entry.DisplayName, name, rowHeight * 0.29, baseline: 0.51, strong: true);
            var status = entry.IsPenalty ? entry.PenaltyCompleted ? "PENALTY SERVED" : "PENALTY" : entry.ServiceState switch
            {
                PitHudServiceState.Completed => "TYRE STOP OK", PitHudServiceState.Paused => "HOLD STILL",
                PitHudServiceState.WaitingForStop => "STOP CAR", PitHudServiceState.Counting => "TYRE STOP", _ => "PIT LANE"
            };
            BroadcastText(dc, status, name, Math.Max(11, rowHeight * 0.18), entry.IsPenalty ? BrushOf(0xF2, 0xC8, 0x60) : BroadcastCyan, baseline: 0.82);
            BroadcastText(dc, $"{entry.Seconds:0.000}", new Rect(width * 0.64, top, width * 0.31, rowHeight),
                rowHeight * 0.50, White, TextAlignment.Right, 0.51, strong: true);
            BroadcastText(dc, entry.IsPenalty ? "PENALTY TIME" : entry.IsService ? "SERVICE TIME" : "TOTAL TIME",
                new Rect(width * 0.64, top, width * 0.31, rowHeight), Math.Max(10, rowHeight * 0.14), BroadcastMuted, TextAlignment.Right, 0.82);
            if (entry.IsService || entry.IsPenalty)
            {
                var required = entry.IsPenalty ? entry.PenaltyRequiredSeconds : entry.ServiceRequiredSeconds;
                var progress = required > 0 ? Math.Clamp(entry.Seconds / required, 0, 1) : 0;
                dc.DrawRectangle(BrushOf(0x2B, 0x37, 0x43), null, new Rect(width * 0.045, top + rowHeight * 0.93, width * 0.91, 2));
                dc.DrawRectangle(BroadcastCyan, null, new Rect(width * 0.045, top + rowHeight * 0.93, width * 0.91 * progress, 2));
            }
            dc.Pop();
        }
    }

    private void DrawBroadcastPenalty(DrawingContext dc, EstateRaceParticipant participant)
    {
        var width = ActualWidth * 0.27;
        var height = ActualHeight * 0.105;
        var presentation = PenaltyPresentation(participant);
        BroadcastPanel(dc, width, height, presentation.Accent);
        BroadcastText(dc, presentation.ValueText, new Rect(width * 0.035, height * 0.10, width * 0.22, height * 0.75),
            height * 0.44, presentation.Accent, strong: true);
        BroadcastText(dc, presentation.Title, new Rect(width * 0.29, height * 0.14, width * 0.66, height * 0.39), height * 0.21, strong: true);
        BroadcastText(dc, OverlayTextLocalization.Text(presentation.Detail), new Rect(width * 0.29, height * 0.53, width * 0.66, height * 0.34),
            height * 0.12, BroadcastMuted);
        if (presentation.Active && participant.PenaltyServiceRequiredSeconds > 0)
        {
            var progress = Math.Clamp(participant.PenaltyServiceElapsedSeconds / participant.PenaltyServiceRequiredSeconds, 0, 1);
            dc.DrawRectangle(presentation.Accent, null, new Rect(width * 0.035, height - 3, width * 0.92 * progress, 3));
        }
    }

    private void DrawBroadcastPractice(DrawingContext dc, EstatePracticeTestItemState item)
    {
        var width = ActualWidth * 0.32;
        var height = ActualHeight * 0.12;
        var accent = item.Status == EstatePracticeTestStatus.Failed ? BrushOf(0xF2, 0x50, 0x60)
            : item.Status == EstatePracticeTestStatus.Completed ? BrushOf(0x4D, 0xD8, 0x91) : BroadcastCyan;
        BroadcastPanel(dc, width, height, accent);
        BroadcastText(dc, PracticeProgramKindText(item.Kind), new Rect(width * 0.045, 0, width * 0.55, height * 0.29), height * 0.12, accent);
        BroadcastText(dc, OverlayTextLocalization.Text(item.Title), new Rect(width * 0.045, height * 0.25, width * 0.70, height * 0.35), height * 0.20);
        BroadcastText(dc, $"{item.CompletedSteps} / {item.TargetSteps}", new Rect(width * 0.78, height * 0.25, width * 0.17, height * 0.35),
            height * 0.20, White, TextAlignment.Right);
        DrawRacePracticeGuidance(dc, item, new Rect(width * 0.045, height * 0.61, width * 0.90, height * 0.22), height * 0.125);
        var progress = item.TargetSteps > 0 ? Math.Clamp(item.CompletedSteps / (double)item.TargetSteps, 0, 1) : 0;
        dc.DrawRectangle(BrushOf(0x2B, 0x37, 0x43), null, new Rect(width * 0.045, height * 0.93, width * 0.91, 3));
        dc.DrawRectangle(accent, null, new Rect(width * 0.045, height * 0.93, width * 0.91 * progress, 3));
    }

    private void DrawBroadcastPitWindow(DrawingContext dc, PitWindowHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.19;
        var height = ActualHeight * 0.14;
        var accent = snapshot.WindowOpen ? BrushOf(0xF2, 0xC8, 0x60) : BroadcastCyan;
        BroadcastPanel(dc, width, height, accent);
        BroadcastText(dc, snapshot.WindowOpen ? "WINDOW OPEN" : "PIT WINDOW", new Rect(width * 0.055, 0, width * 0.9, height * 0.25), height * 0.11, BroadcastMuted);
        var window = snapshot.StartLap == snapshot.EndLap ? $"{snapshot.StartLap}" : $"{snapshot.StartLap}–{snapshot.EndLap}";
        BroadcastText(dc, window, new Rect(width * 0.055, height * 0.24, width * 0.53, height * 0.43), height * 0.29, accent);
        BroadcastText(dc, snapshot.WindowOpen ? "PIT NOW" : $"{snapshot.LapsUntilWindow}",
            new Rect(width * 0.61, height * 0.24, width * 0.33, height * 0.43), height * (snapshot.WindowOpen ? 0.15 : 0.29), White, TextAlignment.Right);
        BroadcastText(dc, "LAPS", new Rect(width * 0.055, height * 0.64, width * 0.3, height * 0.18), height * 0.085, BroadcastMuted);
        BroadcastText(dc, snapshot.WindowOpen ? "SUGGESTED" : "LAPS TO WINDOW", new Rect(width * 0.47, height * 0.64, width * 0.47, height * 0.18), height * 0.080, BroadcastMuted, TextAlignment.Right);
        BroadcastText(dc, "RECENT DEGRADATION", new Rect(width * 0.055, height * 0.82, width * 0.55, height * 0.17), height * 0.072, BroadcastMuted);
        BroadcastText(dc, snapshot.DegradationPerLapSeconds is double d ? $"+{d:0.00} s/lap" : "— s/lap",
            new Rect(width * 0.62, height * 0.82, width * 0.32, height * 0.17), height * 0.08, White, TextAlignment.Right);
    }

    private void DrawBroadcastStrategy(DrawingContext dc, FullRaceStrategyHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.68;
        var height = ActualHeight * 0.36;
        BroadcastPanel(dc, width, height, BroadcastCyan);
        BroadcastText(dc, "RACE STRATEGY", new Rect(width * 0.035, 0, width * 0.57, height * 0.21), height * 0.10);
        BroadcastText(dc, $"{snapshot.RemainingRequiredStops} STOPS REMAINING", new Rect(width * 0.62, 0, width * 0.34, height * 0.21),
            height * 0.053, BroadcastMuted, TextAlignment.Right);
        DrawRaceStrategyTimeline(dc, snapshot, width, height, BroadcastCyan, BrushOf(0xF2, 0xC8, 0x60));
        var labels = new[] { "EST. PIT LOSS", "PROJECTED GAIN", "CONFIDENCE", "PLAN BASIS" };
        var values = new[]
        {
            snapshot.EstimatedPitLossSeconds is double loss ? $"{loss:0.0} s" : "—",
            snapshot.ProjectedAdvantageSeconds is double gain ? $"{gain:+0.0;-0.0;0.0} s" : "—",
            snapshot.Confidence.ToString().ToUpperInvariant(),
            snapshot.HasLiveEvidence ? snapshot.HasHistoricalEvidence ? "LIVE + HISTORY" : "LIVE"
                : snapshot.HasHistoricalEvidence ? "HISTORICAL" : "LIMITED DATA"
        };
        for (var i = 0; i < 4; i++)
        {
            var x = width * (0.035 + i * 0.242);
            BroadcastText(dc, labels[i], new Rect(x, height * 0.74, width * 0.22, height * 0.09), height * 0.037, BroadcastMuted);
            BroadcastText(dc, values[i], new Rect(x, height * 0.84, width * 0.22, height * 0.14), height * (i < 2 ? 0.078 : 0.056),
                i == 2 ? BrushOf(0xF2, 0xC8, 0x60) : White);
        }
    }
}
