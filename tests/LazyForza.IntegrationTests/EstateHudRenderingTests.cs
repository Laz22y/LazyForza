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
    [DataRow(false)]
    [DataRow(true)]
    public void BroadcastReviewSceneUsesRealWidgetRenderersAndMixedScriptNames(bool mixedNames) => Sta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var state = OverlayLayoutPreviewState.EstateRace(now);
        var session = state.Session!;
        var participants = session.Participants.Select(p => p with
        {
            DisplayName = mixedNames && p.Position == 2 ? "八云 / Yakumo" : p.DisplayName.ToUpperInvariant(),
            TeamName = mixedNames && p.Position == 2 ? "长名称车队 · Sakura Racing" : p.TeamName,
            SpeedKph = p.IsInServiceZone ? 0 : p.SpeedKph,
            PitServiceElapsedSeconds = p.IsInServiceZone ? 3.842 : p.PitServiceElapsedSeconds,
            PitLaneElapsedSeconds = p.IsInPitLane ? 8.214 : 0,
            LastSeenAt = now
        }).ToArray();
        state = state with
        {
            PitService = EstatePitServiceState.Empty,
            Session = session with
            {
                Participants = participants, AllowTeams = mixedNames,
                Banner = session.Banner! with { Kind = RaceBannerKind.YellowFlag, Title = "前方事故 · 注意减速", Detail = "禁止在黄旗区内超车" },
                StartsAt = now.AddMinutes(-7), RaceElapsedSeconds = 420, TotalRaceLaps = 20
            }
        };
        var widgets = EstateRaceHudLayoutSettings.Default;
        foreach (var kind in Enum.GetValues<EstateRaceHudWidgetKind>())
            widgets = widgets.Set(kind, new(false, 0, 0, ThemeId: EstateRaceHudThemeIds.Broadcast));
        widgets = widgets
            .Set(EstateRaceHudWidgetKind.Leaderboard, new(true, 0.028, 0.15, 1.10, ThemeId: EstateRaceHudThemeIds.Broadcast))
            .Set(EstateRaceHudWidgetKind.Banner, new(true, 0.32, 0.15, 1.29, ThemeId: EstateRaceHudThemeIds.Broadcast))
            .Set(EstateRaceHudWidgetKind.PitStopInfo, new(true, 0.32, 0.35, 1.55, ThemeId: EstateRaceHudThemeIds.Broadcast))
            .Set(EstateRaceHudWidgetKind.PenaltyStatus, new(true, 0.68, 0.35, 1.05, ThemeId: EstateRaceHudThemeIds.Broadcast))
            .Set(EstateRaceHudWidgetKind.GripStatus, new(true, 0.68, 0.51, 1.4, ThemeId: EstateRaceHudThemeIds.Broadcast))
            .Set(EstateRaceHudWidgetKind.TrackMap, new(true, 0.32, 0.69, 0.8, ThemeId: EstateRaceHudThemeIds.Broadcast));
        var layout = new OverlayLayout(ReduceMotion: true, EstateRaceWidgets: widgets);
        var contribution = new Contribution(() => state);
        var surface = new HudSurface(() => [contribution], () => layout, HudSurfaceKind.EstateRace)
            { Width = 1440, Height = 900 };
        var rendered = Render(surface);
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 41, 54)), null, new Rect(0, 0, 1440, 900));
            dc.DrawImage(rendered, new Rect(0, 0, 1440, 900));
            HudTypography.Draw(dc, "转播主题 · 实际组件渲染", new Rect(40, 20, 1000, 40), 52, 24, Brushes.White, strong: true);
            HudTypography.Draw(dc, "示例赛事数据 / 自定义组件布局", new Rect(40, 65, 1000, 26), 83, 13, Brushes.LightSlateGray);
            HudTypography.Draw(dc, mixedNames ? "中文与英文混排 · 车队名称省略测试" : "无车队副标题 · 核心状态预览",
                new Rect(40, 854, 1200, 26), 873, 13, Brushes.LightSlateGray);
        }
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        SaveQa(bitmap, mixedNames ? "broadcast-review-mixed.png" : "broadcast-review.png");
        Assert.IsTrue(Pixel(rendered, 60, 145).A > 200, "The review sheet must contain the production leaderboard.");
    });

    [TestMethod]
    public void WidgetThemesPersistIndependentlyAndLegacyOrUnknownThemesRemainUsable()
    {
        var legacy = JsonSerializer.Deserialize<EstateRaceHudWidgetPlacement>("{\"IsVisible\":true,\"Left\":0.2,\"Top\":0.3}")!;
        Assert.AreEqual(EstateRaceHudThemeIds.Classic, legacy.ThemeId);
        var mixed = EstateRaceHudLayoutSettings.Default
            .Set(EstateRaceHudWidgetKind.Leaderboard, legacy with { ThemeId = EstateRaceHudThemeIds.Broadcast, Scale = 0.75, Opacity = 0.6 })
            .Set(EstateRaceHudWidgetKind.Banner, legacy with { ThemeId = "future-theme" });
        var restored = EstateRaceHudLayoutSettings.Normalize(JsonSerializer.Deserialize<EstateRaceHudLayout>(JsonSerializer.Serialize(mixed)));
        Assert.AreEqual(mixed, restored);
        Assert.AreEqual(EstateRaceHudThemeIds.Classic, restored.Get(EstateRaceHudWidgetKind.TrackMap).ThemeId);
        Assert.AreEqual("future-theme", restored.Get(EstateRaceHudWidgetKind.Banner).ThemeId, "A temporarily unavailable theme must survive saving settings.");
        Assert.AreEqual(EstateRaceHudThemeIds.Classic, EstateRaceHudThemes.Resolve(restored.Get(EstateRaceHudWidgetKind.Banner).ThemeId).Id);
        Assert.AreEqual(EstateRaceHudThemeIds.Broadcast, EstateRaceHudThemes.Resolve("BROADCAST").Id);
        Assert.AreEqual(EstateRaceHudThemes.Definitions.Count,
            EstateRaceHudThemes.Definitions.Select(theme => theme.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    [DataRow(1280, 720)]
    [DataRow(1920, 1080)]
    [DataRow(2560, 1440)]
    public void EveryThemeRendersEveryWidgetAtExistingPlacement(int width, int height) => Sta(() =>
    {
        foreach (var theme in EstateRaceHudThemes.Definitions)
        {
            foreach (var kind in Enum.GetValues<EstateRaceHudWidgetKind>())
            {
                var widgets = EstateRaceHudLayoutSettings.Default;
                foreach (var other in Enum.GetValues<EstateRaceHudWidgetKind>())
                    widgets = widgets.Set(other, new(other == kind, 0.1, 0.1, Scale: 0.9, ThemeId: theme.Id));
                var layout = new OverlayLayout(ReduceMotion: true, EstateRaceWidgets: widgets);
                var surface = new HudSurface(() => [], () => layout, HudSurfaceKind.EstateRace, layoutPreview: true)
                    { Width = width, Height = height };
                var bitmap = Render(surface);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                Assert.IsTrue(pixels.Any(value => value > 0), $"{theme.Id}/{kind} was blank.");
                // Every theme uses the same placement transform; no theme may draw at the old origin.
                Assert.AreEqual(0, Pixel(bitmap, (int)(width * 0.05), (int)(height * 0.05)).A);
                SaveQa(bitmap, $"{theme.Id}-{kind}-{width}.png");
            }
            var all = EstateRaceHudLayoutSettings.Default;
            foreach (var kind in Enum.GetValues<EstateRaceHudWidgetKind>()) all = all.Set(kind, all.Get(kind) with { ThemeId = theme.Id });
            var full = new HudSurface(() => [], () => new OverlayLayout(ReduceMotion: true, EstateRaceWidgets: all),
                HudSurfaceKind.EstateRace, layoutPreview: true) { Width = width, Height = height };
            SaveQa(Render(full), $"{theme.Id}-all-{width}.png");
        }
    });

    [TestMethod]
    public void MixedThemeSwitchDoesNotKeepOldContentAndUnknownThemeMatchesClassic() => Sta(() =>
    {
        var seconds = 0d;
        var layout = BannerLayout() with { ReduceMotion = true };
        var surface = new HudSurface(() => [], () => layout, HudSurfaceKind.EstateRace,
            layoutPreview: true, monotonicSeconds: () => seconds) { Width = 1000, Height = 600 };
        byte[] Pixels()
        {
            var bytes = new byte[1000 * 600 * 4];
            Render(surface).CopyPixels(bytes, 4000, 0);
            return bytes;
        }
        var classic = Pixels();
        layout = layout with { EstateRaceWidgets = layout.EstateRaceWidgets!.Set(EstateRaceHudWidgetKind.Banner,
            new(true, 0, 0, ThemeId: EstateRaceHudThemeIds.Broadcast)) };
        seconds = 0.01;
        var broadcast = Pixels();
        Assert.IsFalse(classic.SequenceEqual(broadcast), "Broadcast must be a distinct visual theme.");
        layout = layout with { EstateRaceWidgets = layout.EstateRaceWidgets!.Set(EstateRaceHudWidgetKind.Banner,
            new(true, 0, 0, ThemeId: "uninstalled-theme")) };
        seconds = 0.02;
        CollectionAssert.AreEqual(classic, Pixels(), "Switching themes must discard retained drawings of the previous theme.");
    });

    [TestMethod]
    public void TextColumnsShareBaselineAndClipLongMixedScriptNames() => Sta(() =>
    {
        var bounds = new Rect(10, 10, 80, 80);
        foreach (var value in new[] { "Yakumo", "车手八云", "01:23.456", "车手八云 Yakumo · very long team name" })
        {
            var layout = HudTypography.Layout(value, bounds, 55, 22, Brushes.White);
            Assert.AreEqual(55, layout.Origin.Y + layout.Text.Baseline, 0.001);
            var bitmap = Draw(dc => HudTypography.Draw(dc, value, bounds, 55, 22, Brushes.White, TextAlignment.Right));
            for (var x = 0; x < 100; x++)
                for (var y = 0; y < 100; y++)
                    if (!bounds.Contains(new Point(x + 0.5, y + 0.5)))
                        Assert.AreEqual(0, Pixel(bitmap, x, y).A, "Text must stay within its assigned column.");
        }
    });

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
