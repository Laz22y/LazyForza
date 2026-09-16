using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LazyForza.Analysis;

namespace LazyForza.App;

internal sealed partial class TrackMapView
{
    private Point mapGestureStart;
    private bool mapGestureMoved;
    private double? intervalStart, intervalEnd;
    private DrawingGroup? intervalDrawing;
    private MapViewportKey? intervalViewport;

    public event Action<double>? ProgressPicked;

    public void SetSelectedInterval(double? start, double? end)
    {
        if (intervalStart == start && intervalEnd == end) return;
        intervalStart = start;
        intervalEnd = end;
        intervalDrawing = null;
        InvalidateVisual();
    }

    internal bool CompleteIntervalPick(Point pointer, bool wasDragged)
    {
        if (wasDragged || ProgressPicked is null || !TryPickProgress(pointer, out var progress)) return false;
        ProgressPicked(progress);
        return true;
    }

    // Project onto visible line segments, not just sample dots, so sparse recordings remain selectable.
    internal bool TryPickProgress(Point pointer, out double progress)
    {
        progress = 0;
        if (!TryMetrics(out var metrics) || !metrics.Bounds.Contains(pointer)) return false;
        var best = 14d * 14;
        var found = false;
        var samples = laps[0].Samples;
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].S <= samples[i - 1].S) continue;
            var a = MapPoint(samples[i - 1], metrics, viewport);
            var b = MapPoint(samples[i], metrics, viewport);
            var direction = b - a;
            if (direction.LengthSquared < .001) continue;
            var t = Math.Clamp(Vector.Multiply(pointer - a, direction) / direction.LengthSquared, 0, 1);
            var distance = (pointer - (a + direction * t)).LengthSquared;
            if (distance > best) continue;
            best = distance;
            progress = samples[i - 1].S + (samples[i].S - samples[i - 1].S) * t;
            found = true;
        }
        return found;
    }

    private void DrawSelectedInterval(DrawingContext context, MapMetrics metrics)
    {
        if (intervalStart is null && intervalEnd is null) return;
        var key = CreateViewportKey(metrics);
        if (intervalDrawing is null || intervalViewport != key)
        {
            intervalDrawing = new DrawingGroup();
            using (var drawing = intervalDrawing.Open())
            {
                drawing.PushClip(new RectangleGeometry(metrics.Bounds));
                if (intervalStart is double from && intervalEnd is double to && to > from)
                {
                    var samples = laps[0].Samples;
                    var line = new StreamGeometry();
                    using (var path = line.Open())
                    {
                        path.BeginFigure(PointAt(from), false, false);
                        var first = ChartInteractionAlgorithms.FindNearestProgressSample(samples, from);
                        for (var i = first; i < samples.Count && samples[i].S < to; i++)
                            if (samples[i].S > from) path.LineTo(MapPoint(samples[i], metrics, viewport), true, false);
                        path.LineTo(PointAt(to), true, false);
                    }
                    line.Freeze();
                    drawing.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(65, 32, 200, 216)), 13), line);
                    drawing.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(32, 200, 216)), 4), line);
                }
                if (intervalStart is double start) Marker(start, "1", Color.FromRgb(32, 200, 216));
                if (intervalEnd is double end) Marker(end, "2", Color.FromRgb(184, 136, 255));
                drawing.Pop();

                void Marker(double progress, string label, Color color)
                {
                    var p = PointAt(progress);
                    drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(14, 20, 27)), new Pen(new SolidColorBrush(color), 2), p, 12, 12);
                    var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    drawing.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
                }
            }
            intervalDrawing.Freeze();
            intervalViewport = key;
        }
        context.DrawDrawing(intervalDrawing);

        Point PointAt(double progress)
        {
            var samples = laps[0].Samples;
            var i = ChartInteractionAlgorithms.FindNearestProgressSample(samples, progress);
            if (samples[i].S > progress && i > 0) i--;
            var a = MapPoint(samples[i], metrics, viewport);
            if (i + 1 >= samples.Count || samples[i + 1].S <= samples[i].S) return a;
            var b = MapPoint(samples[i + 1], metrics, viewport);
            return a + (b - a) * Math.Clamp((progress - samples[i].S) / (samples[i + 1].S - samples[i].S), 0, 1);
        }
    }
}
