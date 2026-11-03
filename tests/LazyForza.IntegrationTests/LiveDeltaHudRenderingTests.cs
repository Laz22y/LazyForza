using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class LiveDeltaHudRenderingTests
{
    [TestMethod]
    [DataRow("zh-Hans", 800)]
    [DataRow("en", 800)]
    [DataRow("en", 480)]
    public void LiveAndSplitDeltasRenderTogetherWithoutChangingTheDisabledLayout(string language, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var state = OverlayLayoutPreviewState.Lap(null, DateTimeOffset.UtcNow) with
        { ContinuousDeltaEnabled = true, ContinuousDeltaSeconds = -.235, CumulativeHistoricalDeltaSeconds = .120 };
        var contribution = new Contribution(state);
        var surface = new HudSurface(() => [contribution], () => new(ReduceMotion: true), HudSurfaceKind.Lap, layoutPreview: true)
        { Width = width, Height = width * 9d / 16 };
        surface.Measure(new(surface.Width, surface.Height));
        surface.Arrange(new Rect(0, 0, surface.Width, surface.Height)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(surface.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        Assert.IsTrue(HasInk(bitmap, .125, .165), "Live delta line is visible.");
        Assert.IsTrue(HasInk(bitmap, .18, .215), "Split delta line remains visible.");
        if (Environment.GetEnvironmentVariable("LAZYFORZA_DELTA_QA") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            using var file = File.Create(Path.Combine(output, $"delta-hud-{language}-{width}.png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
        }
        contribution.Value = state with { ContinuousDeltaEnabled = false };
        surface.InvalidateVisual(); surface.UpdateLayout();
        var legacy = new RenderTargetBitmap(width, (int)Math.Ceiling(surface.Height), 96, 96, PixelFormats.Pbgra32); legacy.Render(surface);
        Assert.IsFalse(HasInk(legacy, .125, .16), "Disabling continuous delta removes only its new line.");
        Assert.IsTrue(HasInk(legacy, .18, .225), "Existing split remains at the same position.");
    });

    private static bool HasInk(BitmapSource bitmap, double from, double to)
    {
        // Keep the sector arc outside the text region being checked.
        var rect = new Int32Rect(bitmap.PixelWidth * 3 / 8, (int)(bitmap.PixelHeight * from), bitmap.PixelWidth / 4,
            (int)(bitmap.PixelHeight * (to - from)));
        var bytes = new byte[rect.Width * rect.Height * 4]; bitmap.CopyPixels(rect, bytes, rect.Width * 4, 0);
        return Enumerable.Range(0, bytes.Length / 4).Any(index => bytes[index * 4 + 3] > 0);
    }
    private sealed class Contribution(LapHudState initial) : IHudContribution
    {
        public LapHudState Value { get; set; } = initial;
        public string Id => "test.delta";
        public HudContributionKind Kind => HudContributionKind.LapSectors;
        public int ZIndex => 20;
        public object Snapshot => Value;
    }
}
