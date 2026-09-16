using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ShiftIndicatorVisibilityTests
{
    [TestMethod]
    public void OldLayoutsKeepShiftIndicatorsVisible()
    {
        var legacy = JsonSerializer.Deserialize<OverlayLayout>("{\"Opacity\":0.7}")!;
        Assert.IsTrue(legacy.ShowShiftIndicators);
        var hidden = OverlayLayoutGeometry.Normalize(legacy with { ShowShiftIndicators = false });
        Assert.IsFalse(hidden.ShowShiftIndicators);
        Assert.AreEqual(.7, hidden.Opacity);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HidingDashboardSymbolsKeepsRecommendationAndOtherPixels(bool downshift) => EstateHudRenderingTests.Sta(() =>
    {
        var state = OverlayLayoutPreviewState.Dashboard(null, DateTimeOffset.UtcNow) with
        {
            RawGear = (byte)(downshift ? 4 : 3), ForwardGear = downshift ? 4 : 3,
            GearDisplay = downshift ? "4" : "3", Rpm = downshift ? 4800 : 7500,
            ShiftLearning = new ShiftLearningSnapshot(LearningState.Ready, 1, .9, null, [], [],
                [new ShiftTarget(3, 4, 7800, 7400, 5600, .9, false)], new Dictionary<string, int>(), "ready")
        };
        Assert.AreEqual(downshift ? 3 : 4, state.RecommendedGear);
        var visible = Render(state, HudSurfaceKind.Dashboard, true);
        var hidden = Render(state, HudSurfaceKind.Dashboard, false);
        var withoutRecommendation = Render(state with { ShiftRecommendationsEnabled = false }, HudSurfaceKind.Dashboard, true);
        Assert.IsFalse(visible.SequenceEqual(hidden), "The active arrow disappears.");
        CollectionAssert.AreEqual(withoutRecommendation, hidden, "Only the arrow changes; gear, speed and other HUD content remain.");
        Assert.IsTrue(state.ShiftRecommendationsEnabled);
        Assert.AreEqual(downshift ? 3 : 4, state.RecommendedGear, "Visibility does not alter the calculated recommendation.");
    });

    [TestMethod]
    public void HidingDriftSymbolsPreservesTheRestOfTheHud() => EstateHudRenderingTests.Sta(() =>
    {
        var state = OverlayLayoutPreviewState.Drift(null, DateTimeOffset.UtcNow);
        var visible = Render(state, HudSurfaceKind.Drift, true);
        var hidden = Render(state, HudSurfaceKind.Drift, false);
        Assert.IsFalse(visible.SequenceEqual(hidden));
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 500; x++)
        {
            // The cue sits to the right of the gear number inside this small region.
            if (x is >= 368 and <= 400 && y is >= 185 and <= 217) continue;
            for (var channel = 0; channel < 4; channel++)
                Assert.AreEqual(visible[(y * 500 + x) * 4 + channel], hidden[(y * 500 + x) * 4 + channel]);
        }
    });

    private static byte[] Render(object state, HudSurfaceKind kind, bool show)
    {
        var contribution = new Contribution(state);
        var surface = new HudSurface(() => [contribution], () => new OverlayLayout(ShowShiftIndicators: show), kind, layoutPreview: true)
            { Width = 500, Height = 300 };
        surface.Measure(new Size(500, 300));
        surface.Arrange(new Rect(0, 0, 500, 300));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(500, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var bytes = new byte[500 * 300 * 4];
        bitmap.CopyPixels(bytes, 500 * 4, 0);
        return bytes;
    }

    private sealed class Contribution(object state) : IHudContribution
    {
        public string Id => "shift-indicator-test";
        public HudContributionKind Kind => HudContributionKind.Dashboard;
        public int ZIndex => 0;
        public object Snapshot => state;
    }
}
