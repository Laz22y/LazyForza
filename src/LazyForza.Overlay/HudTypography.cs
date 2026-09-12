using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LazyForza.Overlay;

internal static class HudTypography
{
    private static readonly Typeface Latin = new("Bahnschrift");
    private static readonly Typeface Chinese = new("Microsoft YaHei UI");
    private static readonly Typeface LatinStrong = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ChineseStrong = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    internal static (FormattedText Text, Point Origin) Layout(string value, Rect bounds, double baseline,
        double size, Brush brush, TextAlignment alignment = TextAlignment.Left, bool strong = false)
    {
        var hasChinese = value.Any(c => c is >= '\u3400' and <= '\u9fff');
        var font = hasChinese ? strong ? ChineseStrong : Chinese : strong ? LatinStrong : Latin;
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            font, size, brush, 1)
        {
            MaxTextWidth = Math.Max(1, bounds.Width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment
        };
        // A shared baseline aligns Latin digits and Chinese labels despite their
        // different font ascenders. Column width and clipping stay explicit.
        return (text, new Point(bounds.Left, baseline - text.Baseline));
    }

    internal static void Draw(DrawingContext dc, string value, Rect bounds, double baseline,
        double size, Brush brush, TextAlignment alignment = TextAlignment.Left, bool strong = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || string.IsNullOrEmpty(value)) return;
        var layout = Layout(value, bounds, baseline, size, brush, alignment, strong);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawText(layout.Text, layout.Origin);
        dc.Pop();
    }
}
