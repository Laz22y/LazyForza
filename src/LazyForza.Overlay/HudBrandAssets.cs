using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace LazyForza.Overlay;

internal static class HudBrandAssets
{
    internal static BitmapSource Wordmark { get; } = LoadWordmark();

    private static BitmapSource LoadWordmark()
    {
        using var stream = typeof(HudBrandAssets).Assembly.GetManifestResourceStream("LazyForza.Wordmark.png")!;
        var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        // Detach the pixels from the decoder's dispatcher before sharing between
        // the live overlay, editor and STA render checks.
        var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
