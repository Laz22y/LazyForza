using System.Windows;
using System.Windows.Media;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

// Reusable pit-window, pit-stop and penalty styles. Kept outside the theme registry.
internal sealed partial class HudSurface
{
    private static readonly Brush TimingPanelMuted = BrushOf(0x8A, 0xA0, 0xB1);
    private static readonly Brush TimingPanelCyan = BrushOf(0x72, 0xD9, 0xEC);

    private static void TimingPanelBackground(DrawingContext dc, double width, double height, Brush? accent = null)
    {
        EstateRaceDrawingLayers.Panel(dc, BrushOf(0x0B, 0x12, 0x1B, 0.94), null,
            new Rect(0, 0, width, height));
        if (accent is not null) dc.DrawRectangle(accent, null, new Rect(0, 0, Math.Max(3, width * 0.008), height));
    }

    private static void TimingPanelText(DrawingContext dc, string value, Rect bounds, double size,
        Brush? color = null, TextAlignment alignment = TextAlignment.Left, double baseline = 0.70, bool strong = false) =>
        HudTypography.Draw(dc, value, bounds, bounds.Top + bounds.Height * baseline,
            size, color ?? White, alignment, strong);

    private void DrawTimingPanelPitStop(DrawingContext dc, PitHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.215;
        var header = ActualHeight * 0.041;
        var rowHeight = ActualHeight * 0.0665;
        TimingPanelBackground(dc, width, header + rowHeight * snapshot.Entries.Count);
        dc.DrawRectangle(TimingPanelCyan, null, new Rect(0, 0, width, 2));
        TimingPanelText(dc, "PIT STOP", new Rect(width * 0.045, 0, width * 0.50, header), header * 0.47, strong: true);
        TimingPanelText(dc, $"{snapshot.ActiveParticipantCount} PLAYERS", new Rect(width * 0.57, 0, width * 0.38, header),
            Math.Max(11, header * 0.26), TimingPanelMuted, TextAlignment.Right);
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
            TimingPanelText(dc, entry.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Rect(width * 0.035, top, width * 0.105, rowHeight), rowHeight * 0.43, baseline: 0.51, strong: true);
            dc.DrawRectangle(RaceThemeBrush(entry.ThemeColor), null, new Rect(width * 0.15, top + rowHeight * 0.20, 2, rowHeight * 0.5));
            var name = new Rect(width * 0.18, top, width * 0.42, rowHeight);
            TimingPanelText(dc, entry.DisplayName, name, rowHeight * 0.29, baseline: 0.51, strong: true);
            var status = entry.IsPenalty ? entry.PenaltyCompleted ? "PENALTY SERVED" : "PENALTY" : entry.ServiceState switch
            {
                PitHudServiceState.Completed => "TYRE STOP OK", PitHudServiceState.Paused => "HOLD STILL",
                PitHudServiceState.WaitingForStop => "STOP CAR", PitHudServiceState.Counting => "TYRE STOP", _ => "PIT LANE"
            };
            TimingPanelText(dc, status, name, Math.Max(11, rowHeight * 0.18), entry.IsPenalty ? BrushOf(0xF2, 0xC8, 0x60) : TimingPanelCyan, baseline: 0.82);
            TimingPanelText(dc, $"{entry.Seconds:0.000}", new Rect(width * 0.64, top, width * 0.31, rowHeight),
                rowHeight * 0.50, White, TextAlignment.Right, 0.51, strong: true);
            TimingPanelText(dc, entry.IsPenalty ? "PENALTY TIME" : entry.IsService ? "SERVICE TIME" : "TOTAL TIME",
                new Rect(width * 0.64, top, width * 0.31, rowHeight), Math.Max(10, rowHeight * 0.14), TimingPanelMuted, TextAlignment.Right, 0.82);
            if (entry.IsService || entry.IsPenalty)
            {
                var required = entry.IsPenalty ? entry.PenaltyRequiredSeconds : entry.ServiceRequiredSeconds;
                var progress = required > 0 ? Math.Clamp(entry.Seconds / required, 0, 1) : 0;
                dc.DrawRectangle(BrushOf(0x2B, 0x37, 0x43), null, new Rect(width * 0.045, top + rowHeight * 0.93, width * 0.91, 2));
                dc.DrawRectangle(TimingPanelCyan, null, new Rect(width * 0.045, top + rowHeight * 0.93, width * 0.91 * progress, 2));
            }
            dc.Pop();
        }
    }

    private void DrawTimingPanelPenalty(DrawingContext dc, EstateRaceParticipant participant)
    {
        var width = ActualWidth * 0.27;
        var height = ActualHeight * 0.105;
        var presentation = PenaltyPresentation(participant);
        TimingPanelBackground(dc, width, height, presentation.Accent);
        TimingPanelText(dc, presentation.ValueText, new Rect(width * 0.035, height * 0.10, width * 0.22, height * 0.75),
            height * 0.44, presentation.Accent, strong: true);
        TimingPanelText(dc, presentation.Title, new Rect(width * 0.29, height * 0.14, width * 0.66, height * 0.39), height * 0.21, strong: true);
        TimingPanelText(dc, OverlayTextLocalization.Text(presentation.Detail), new Rect(width * 0.29, height * 0.53, width * 0.66, height * 0.34),
            height * 0.12, TimingPanelMuted);
        if (presentation.Active && participant.PenaltyServiceRequiredSeconds > 0)
        {
            var progress = Math.Clamp(participant.PenaltyServiceElapsedSeconds / participant.PenaltyServiceRequiredSeconds, 0, 1);
            dc.DrawRectangle(presentation.Accent, null, new Rect(width * 0.035, height - 3, width * 0.92 * progress, 3));
        }
    }

    private void DrawTimingPanelPitWindow(DrawingContext dc, PitWindowHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.19;
        var height = ActualHeight * 0.14;
        var accent = snapshot.WindowOpen ? BrushOf(0xF2, 0xC8, 0x60) : TimingPanelCyan;
        TimingPanelBackground(dc, width, height, accent);
        TimingPanelText(dc, snapshot.WindowOpen ? "WINDOW OPEN" : "PIT WINDOW", new Rect(width * 0.055, 0, width * 0.9, height * 0.25), height * 0.11, TimingPanelMuted);
        var window = snapshot.StartLap == snapshot.EndLap ? $"{snapshot.StartLap}" : $"{snapshot.StartLap}–{snapshot.EndLap}";
        TimingPanelText(dc, window, new Rect(width * 0.055, height * 0.24, width * 0.53, height * 0.43), height * 0.29, accent);
        TimingPanelText(dc, snapshot.WindowOpen ? "PIT NOW" : $"{snapshot.LapsUntilWindow}",
            new Rect(width * 0.61, height * 0.24, width * 0.33, height * 0.43), height * (snapshot.WindowOpen ? 0.15 : 0.29), White, TextAlignment.Right);
        TimingPanelText(dc, "LAPS", new Rect(width * 0.055, height * 0.64, width * 0.3, height * 0.18), height * 0.085, TimingPanelMuted);
        TimingPanelText(dc, snapshot.WindowOpen ? "SUGGESTED" : "LAPS TO WINDOW", new Rect(width * 0.47, height * 0.64, width * 0.47, height * 0.18), height * 0.080, TimingPanelMuted, TextAlignment.Right);
        TimingPanelText(dc, "RECENT DEGRADATION", new Rect(width * 0.055, height * 0.82, width * 0.55, height * 0.17), height * 0.072, TimingPanelMuted);
        TimingPanelText(dc, snapshot.DegradationPerLapSeconds is double d ? $"+{d:0.00} s/lap" : "— s/lap",
            new Rect(width * 0.62, height * 0.82, width * 0.32, height * 0.17), height * 0.08, White, TextAlignment.Right);
    }

}
