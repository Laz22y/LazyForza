using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed class SessionPaceChart : FrameworkElement
{
    private readonly IReadOnlyList<LapSummary> laps;
    private readonly Action<LapSummary> inspect;
    private int hovered = -1;

    public SessionPaceChart(IReadOnlyList<LapSummary> laps, Action<LapSummary> inspect)
    {
        this.laps = laps;
        this.inspect = inspect;
        Cursor = Cursors.Hand;
        MouseMove += (_, args) =>
        {
            if (ActualWidth <= 82 || laps.Count == 0) return;
            var next = (int)Math.Round(Math.Clamp((args.GetPosition(this).X - 62) / (ActualWidth - 82), 0, 1) * (laps.Count - 1));
            if (next == hovered) return;
            hovered = next;
            ToolTip = AppLocalization.Format("analysis.session.point", "记录圈 {0} · {1:0.000} 秒 · {2}",
                next + 1, laps[next].TotalSeconds, AppLocalization.Literal(laps[next].IsValid ? "有效" : "无效"));
            InvalidateVisual();
        };
        MouseLeave += (_, _) => { hovered = -1; InvalidateVisual(); };
        MouseLeftButtonUp += (_, _) => { if (hovered >= 0) inspect(laps[hovered]); };
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 82 || ActualHeight <= 48) return;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var usable = laps.Where(lap => double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0).ToArray();
        if (usable.Length == 0) return;
        var min = usable.Min(lap => lap.TotalSeconds);
        var max = usable.Max(lap => lap.TotalSeconds);
        var padding = Math.Max(0.5, (max - min) * .14);
        min = Math.Max(0, min - padding);
        max += padding;
        var bounds = new Rect(62, 12, ActualWidth - 82, ActualHeight - 44);
        var muted = Resource("MutedBrush");
        var accent = Resource("AccentBrush");
        var rule = new Pen(Resource("BorderBrush"), 1);
        for (var i = 0; i <= 3; i++)
        {
            var y = bounds.Top + i * bounds.Height / 3;
            drawing.DrawLine(rule, new Point(bounds.Left, y), new Point(bounds.Right, y));
            Text($"{max - i * (max - min) / 3:0.0}s", 0, y - 8, muted);
        }
        var validTimes = usable.Where(lap => lap.IsValid).Select(lap => lap.TotalSeconds).Order().ToArray();
        if (validTimes.Length > 0)
        {
            var middle = validTimes.Length / 2;
            var median = validTimes.Length % 2 == 0 ? (validTimes[middle - 1] + validTimes[middle]) / 2 : validTimes[middle];
            var y = Y(median);
            drawing.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash },
                new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
        Point? previous = null;
        for (var i = 0; i < laps.Count; i++)
        {
            var lap = laps[i];
            if (!double.IsFinite(lap.TotalSeconds) || lap.TotalSeconds <= 0) { previous = null; continue; }
            var point = new Point(bounds.Left + (laps.Count == 1 ? bounds.Width / 2 : i * bounds.Width / (laps.Count - 1)), Y(lap.TotalSeconds));
            if (previous is Point start) drawing.DrawLine(new Pen(accent, 1.6), start, point);
            var color = lap.IsValid ? accent : Resource("DangerBrush");
            drawing.DrawEllipse(color, null, point, i == hovered ? 5 : 3, i == hovered ? 5 : 3);
            if (i == hovered) drawing.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dot },
                new Point(point.X, bounds.Top), new Point(point.X, bounds.Bottom));
            previous = point;
        }
        var ticks = Math.Min(laps.Count, 6);
        for (var i = 0; i < ticks; i++)
        {
            var index = ticks == 1 ? 0 : (int)Math.Round(i * (laps.Count - 1d) / (ticks - 1));
            var x = laps.Count == 1 ? bounds.Left + bounds.Width / 2 : bounds.Left + index * bounds.Width / (laps.Count - 1);
            Text((index + 1).ToString(CultureInfo.CurrentCulture), x - 4, bounds.Bottom + 10, muted);
        }
        return;

        double Y(double seconds) => bounds.Bottom - (seconds - min) / (max - min) * bounds.Height;
        void Text(string text, double x, double y, Brush brush) => drawing.DrawText(new FormattedText(text,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }

    private Brush Resource(string key) => (Brush)FindResource(key);
}
