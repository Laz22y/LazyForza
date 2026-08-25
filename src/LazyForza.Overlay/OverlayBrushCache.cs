using System.Collections.Concurrent;
using System.Windows.Media;

namespace LazyForza.Overlay;

internal static class OverlayBrushCache
{
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> Brushes = new();

    public static SolidColorBrush Get(byte red, byte green, byte blue, double opacity = 1)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * byte.MaxValue);
        var key = (uint)(alpha << 24 | red << 16 | green << 8 | blue);
        return Brushes.GetOrAdd(key, _ =>
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue))
            {
                Opacity = alpha / (double)byte.MaxValue
            };
            brush.Freeze();
            return brush;
        });
    }
}
