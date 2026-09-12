using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed partial class HudSurface
{
    private void DrawRacePitWindowSuggestion(
        DrawingContext dc,
        PitWindowHudSnapshot snapshot)
    {
        var width = ActualWidth * 0.19;
        var height = ActualHeight * 0.14;
        var cyan = BrushOf(0x20, 0xD9, 0xEF);
        var amber = BrushOf(0xFF, 0xB5, 0x21);
        var accent = snapshot.WindowOpen ? amber : cyan;
        EstateRaceDrawingLayers.Panel(dc, 
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
        EstateRaceDrawingLayers.Panel(dc, RaceStrategyBackground, border,
            new Rect(0, 0, width, height), height * 0.065, height * 0.065);
        EstateRaceDrawingLayers.Panel(dc, BrushOf(0x03, 0x08, 0x0E, 0.54), null,
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

}
