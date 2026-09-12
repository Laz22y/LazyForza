using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateHudRenderingTests
{
    [TestMethod]
    public void BackdropFadesOnceAndLeavesContentOpaque() => Sta(() =>
    {
        var layers = EstateRaceDrawingLayers.Record(dc =>
        {
            EstateRaceDrawingLayers.Panel(dc, Brushes.Black, null, new Rect(0, 0, 100, 100));
            dc.PushClip(new RectangleGeometry(new Rect(20, 20, 60, 60)));
            dc.PushTransform(new TranslateTransform(10, 10));
            EstateRaceDrawingLayers.Panel(dc, Brushes.Blue, null, new Rect(0, 0, 100, 100));
            dc.Pop(); dc.Pop();
            dc.DrawRectangle(Brushes.White, null, new Rect(5, 5, 10, 10));
        });
        var bitmap = Draw(dc => layers.Draw(dc, 0.4));
        Assert.AreEqual(102, Pixel(bitmap, 50, 50).A, 1, "Overlapping panels must fade as one backdrop.");
        Assert.AreEqual(102, Pixel(bitmap, 90, 90).A, 1);
        Assert.AreEqual(0, Pixel(bitmap, 90, 90).B, "The nested clip must survive layer separation.");
        Assert.AreEqual(255, Pixel(bitmap, 10, 10).A, "Text/indicators must not inherit backdrop opacity.");
        var clear = Draw(dc => layers.Draw(dc, 0));
        Assert.AreEqual(0, Pixel(clear, 50, 50).A);
        Assert.AreEqual(255, Pixel(clear, 10, 10).A);
    });

    [TestMethod]
    public void ContentTransitionDoesNotDimOrDuplicateBackdrop() => Sta(() =>
    {
        EstateRaceDrawingLayers Create(Brush foreground) => EstateRaceDrawingLayers.Record(dc =>
        {
            EstateRaceDrawingLayers.Panel(dc, Brushes.Black, null, new Rect(0, 0, 100, 100));
            dc.DrawRectangle(foreground, null, new Rect(5, 5, 10, 10));
        });
        var outgoing = Create(Brushes.Red);
        var current = Create(Brushes.White);
        foreach (var progress in new[] { 0d, 0.25, 0.5, 0.75, 1 })
        {
            var bitmap = Draw(dc => current.Draw(dc, 0.65, outgoing, progress));
            Assert.AreEqual(166, Pixel(bitmap, 50, 50).A, 1,
                $"Panel opacity must remain constant throughout a content transition ({progress}).");
        }
    });

    [TestMethod]
    public void CaptureUsesGlobalAndWidgetOpacityExactlyOnce() => Sta(() =>
    {
        var layout = BannerLayout() with { ReduceMotion = true };
        var window = new TelemetryOverlayWindow(() => [], layout);
        var folder = Path.Combine(Path.GetTempPath(), $"lfz-opacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            BitmapSource Capture(string name)
            {
                var path = Path.Combine(folder, name + ".png");
                window.CapturePng(path, 1000, 600, previewEstateRace: true);
                using var stream = File.OpenRead(path);
                return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            var baseline = Capture("full");
            layout = layout with { Opacity = 0.5, EstateRaceWidgets = layout.EstateRaceWidgets!.Set(
                EstateRaceHudWidgetKind.Banner, new(true, 0, 0, Opacity: 0.5)) };
            window.ApplyLayout(layout);
            var faded = Capture("faded");
            Assert.IsTrue(Pixel(baseline, 450, 48).A > 200);
            Assert.AreEqual(Pixel(baseline, 450, 48).A * 0.25, Pixel(faded, 450, 48).A, 2);
        }
        finally
        {
            window.Close();
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    });

    [TestMethod]
    [DataRow("event")]
    [DataRow("stage")]
    [DataRow("disconnect")]
    public void SceneBoundaryCannotReplayPreviousBanner(string boundary) => Sta(() =>
    {
        var seconds = 0d;
        var state = OverlayLayoutPreviewState.EstateRace(DateTimeOffset.UtcNow);
        var session = state.Session! with { EventId = Guid.NewGuid(), StageId = Guid.NewGuid() };
        state = state with { Session = session };
        var contribution = new Contribution(() => state);
        var surface = new HudSurface(() => [contribution], BannerLayout,
            HudSurfaceKind.EstateRace, monotonicSeconds: () => seconds) { Width = 1000, Height = 600 };
        Render(surface);
        seconds = 0.25;
        Assert.IsTrue(Pixel(Render(surface), 450, 48).A > 200);
        if (boundary == "disconnect")
        {
            state = state with { ConnectionState = EstateRaceConnectionState.Disconnected };
            seconds += 0.01;
            Assert.AreEqual(0, Pixel(Render(surface), 450, 48).A);
        }
        state = state with
        {
            ConnectionState = EstateRaceConnectionState.Connected,
            Session = session with
            {
                EventId = boundary == "event" ? Guid.NewGuid() : session.EventId,
                StageId = boundary == "stage" ? Guid.NewGuid() : session.StageId,
                Banner = null
            }
        };
        seconds += 0.01;
        Assert.AreEqual(0, Pixel(Render(surface), 450, 48).A, "Old event contents must not animate out in a new scene.");
    });

    [TestMethod]
    public void ReducedMotionDrawsFirstFrameImmediatelyAndResetDropsExitState()
    {
        var animations = new EstateRaceHudAnimationController();
        var first = animations.Update(EstateRaceHudWidgetKind.Banner, true, 0, reduceMotion: true);
        Assert.AreEqual(1, first.Opacity);
        Assert.IsFalse(first.IsAnimating);
        animations.Reset();
        Assert.IsFalse(animations.Update(EstateRaceHudWidgetKind.Banner, false, 0.01, false).ShouldDraw);
        Assert.IsFalse(animations.AnyAnimating);
    }

    [TestMethod]
    public void LegacyLayoutKeepsAppearanceAndNewBackdropSettingRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<OverlayLayout>("{\"Opacity\":0.55,\"ReduceMotion\":true}")!;
        Assert.AreEqual(1, legacy.EstateRaceBackdropOpacity);
        Assert.AreEqual(0.55, OverlayLayoutGeometry.Normalize(legacy).Opacity);
        var updated = legacy with { EstateRaceBackdropOpacity = 0.35 };
        Assert.AreEqual(updated, JsonSerializer.Deserialize<OverlayLayout>(JsonSerializer.Serialize(updated)));
        Assert.AreEqual(1, OverlayLayoutGeometry.Normalize(updated with { EstateRaceBackdropOpacity = double.NaN }).EstateRaceBackdropOpacity);
        Assert.AreEqual(0, OverlayLayoutGeometry.Normalize(updated with { EstateRaceBackdropOpacity = -1 }).EstateRaceBackdropOpacity);
    }

    [TestMethod]
    public void ReducedMotionKeepsLongPracticeGuidanceStationary() => Sta(() =>
    {
        var seconds = 0d;
        var state = OverlayLayoutPreviewState.EstateRace(DateTimeOffset.UtcNow);
        state = state with
        {
            Session = state.Session! with { Phase = RaceSessionPhase.Practice },
            PracticeTests = state.PracticeTests! with
            {
                Items = [state.PracticeTests!.Items[0] with
                {
                    Guidance = string.Concat(Enumerable.Repeat("Keep a steady pace and follow the racing line. ", 12))
                }]
            }
        };
        var layout = BannerLayout() with
        {
            ReduceMotion = true,
            EstateRaceWidgets = BannerLayout().EstateRaceWidgets!
                .Set(EstateRaceHudWidgetKind.Banner, new(false, 0, 0))
                .Set(EstateRaceHudWidgetKind.PracticeProgram, new(true, 0, 0))
        };
        var contribution = new Contribution(() => state);
        var surface = new HudSurface(() => [contribution], () => layout,
            HudSurfaceKind.EstateRace, monotonicSeconds: () => seconds) { Width = 1000, Height = 600 };
        var first = Render(surface);
        seconds = 6;
        var later = Render(surface);
        var before = new byte[1000 * 600 * 4];
        var after = new byte[before.Length];
        first.CopyPixels(before, 4000, 0);
        later.CopyPixels(after, 4000, 0);
        Assert.IsTrue(before.Any(value => value > 0));
        CollectionAssert.AreEqual(before, after, "Reduced motion must disable the guidance marquee too.");
    });

    [TestMethod]
    public void TransparentEstatePanelsKeepVisibleTextAndSignals() => Sta(() =>
    {
        foreach (var opacity in new[] { 1d, 0.45, 0 })
        {
            var layout = new OverlayLayout(EstateRaceBackdropOpacity: opacity);
            var surface = new HudSurface(() => [], () => layout, HudSurfaceKind.EstateRace, layoutPreview: true)
                { Width = 1920, Height = 1080 };
            var bitmap = Render(surface);
            var pixels = new byte[1920 * 1080 * 4];
            bitmap.CopyPixels(pixels, 1920 * 4, 0);
            Assert.IsTrue(Enumerable.Range(0, pixels.Length / 4).Count(i =>
                pixels[i * 4 + 3] > 240 && pixels[i * 4 + 2] > 220) > 1000,
                "Opaque text and semantic indicators remain present at every backdrop setting.");
            SaveQa(bitmap, $"estate-backdrop-{opacity:0.00}.png");
        }
    });

    private static OverlayLayout BannerLayout()
    {
        var widgets = EstateRaceHudLayoutSettings.Default;
        foreach (var kind in Enum.GetValues<EstateRaceHudWidgetKind>())
            widgets = widgets.Set(kind, new(kind == EstateRaceHudWidgetKind.Banner, 0, 0));
        return new OverlayLayout(Left: 0, Top: 0, Width: 1000, Height: 600, Scale: 1,
            EstateRaceWidgets: widgets, EstateRaceHudLeft: 0, EstateRaceHudTop: 0,
            EstateRaceHudWidth: 1000, EstateRaceHudHeight: 600);
    }

    private static RenderTargetBitmap Render(FrameworkElement visual)
    {
        visual.InvalidateVisual();
        visual.Measure(new Size(visual.Width, visual.Height));
        visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)visual.Width, (int)visual.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static RenderTargetBitmap Draw(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void SaveQa(BitmapSource bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("LAZYFORZA_HUD_QA") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    internal static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF rendering did not complete.");
        Assert.IsNull(failure, failure?.ToString());
    }

    private sealed class Contribution(Func<EstateRaceHudState> state) : IHudContribution
    {
        public string Id => "estate-render-test";
        public HudContributionKind Kind => HudContributionKind.EstateRace;
        public int ZIndex => 0;
        public object Snapshot => state();
    }
}
