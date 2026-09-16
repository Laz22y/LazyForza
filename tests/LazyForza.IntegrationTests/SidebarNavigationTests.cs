using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class SidebarNavigationTests
{
    [TestMethod]
    public void QuickChoicesAndExistingSpeechPreferencesSurviveStoreReopen()
    {
        string[] selected = [SidebarQuickSettings.ShiftRecommendations, SidebarQuickSettings.Volume,
            SidebarQuickSettings.HudOpacity, SidebarQuickSettings.EstateBackdropOpacity,
            SidebarQuickSettings.AutomaticRecording, SidebarQuickSettings.Motion];
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lazyforza-quick-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new LazyForzaStore(path))
            {
                store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save(selected));
                store.SetAppSetting("raceEngineer.muted", "True");
                store.SetAppSetting("raceEngineer.volume", "35");
                store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(new OverlayLayout(
                    Opacity: 0.65, EstateRaceBackdropOpacity: 0.35, ShowShiftIndicators: false)));
                (AutomaticRecordingOptions.Load(store) with { Enabled = true, RotateOldest = true }).Save(store);
            }
            using (var store = new LazyForzaStore(path))
            {
                CollectionAssert.AreEqual(selected, SidebarQuickSettings.Load(store.GetAppSetting(SidebarQuickSettings.StoreKey)));
                Assert.AreEqual("True", store.GetAppSetting("raceEngineer.muted"));
                Assert.AreEqual("35", store.GetAppSetting("raceEngineer.volume"));
                var layout = JsonSerializer.Deserialize<OverlayLayout>(store.GetAppSetting("overlay.layout")!)!;
                Assert.IsFalse(layout.ShowShiftIndicators);
                Assert.AreEqual(0.65, layout.Opacity);
                Assert.AreEqual(0.35, layout.EstateRaceBackdropOpacity);
                var recording = AutomaticRecordingOptions.Load(store);
                Assert.IsTrue(recording.Enabled);
                Assert.IsTrue(recording.RotateOldest);
                store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save([]));
            }
            using (var store = new LazyForzaStore(path))
                Assert.AreEqual(0, SidebarQuickSettings.Load(store.GetAppSetting(SidebarQuickSettings.StoreKey)).Length);
        }
        finally { System.IO.File.Delete(path); System.IO.File.Delete(path + "-wal"); System.IO.File.Delete(path + "-shm"); }
    }

    [TestMethod]
    public void QuickSettingsKeepEmptySelectionAndNormalizeStoredChoices()
    {
        CollectionAssert.AreEqual(new[] { SidebarQuickSettings.Mute, SidebarQuickSettings.ShiftIndicators }, SidebarQuickSettings.Load(null));
        CollectionAssert.AreEqual(Array.Empty<string>(), SidebarQuickSettings.Load("[]"));
        var saved = SidebarQuickSettings.Save([SidebarQuickSettings.Volume, "connectCue", SidebarQuickSettings.Mute, SidebarQuickSettings.Volume]);
        CollectionAssert.AreEqual(new[] { SidebarQuickSettings.Mute, SidebarQuickSettings.Volume }, SidebarQuickSettings.Load(saved));
        CollectionAssert.AreEqual(new[] { SidebarQuickSettings.Mute, SidebarQuickSettings.ShiftIndicators }, SidebarQuickSettings.Load("{"));
        CollectionAssert.AreEqual(new[] { SidebarQuickSettings.Mute }, SidebarQuickSettings.Load("[\"engineerMute\"]"),
            "Existing custom choices are not changed when a new quick action is introduced.");
    }

    [TestMethod]
    [DataRow(852)]
    [DataRow(592)]
    [DataRow(420)]
    public void SettingsRemainAtBottomWhilePageIdsAndSelectionStayStable(int height) => EstateHudRenderingTests.Sta(() =>
    {
        var sidebar = new SidebarNavigation();
        var labels = Enumerable.Range(0, 10).Select(index => new TextBlock { Text = $"Page {index}" }).ToArray();
        for (var i = 0; i < labels.Length; i++) sidebar.AddPage(i, labels[i], i == 8);
        sidebar.SetFooter(new Border { Height = 24 }, new Border { Height = 64 });
        sidebar.Measure(new Size(208, height)); sidebar.Arrange(new Rect(0, 0, 208, height)); sidebar.UpdateLayout();
        var changes = 0;
        sidebar.SelectionChanged += (_, _) => changes++;
        foreach (var id in new[] { 0, 8, 9, 3 })
        {
            sidebar.SelectedIndex = id;
            Assert.AreEqual(id, sidebar.SelectedIndex);
        }
        Assert.AreEqual(4, changes);
        sidebar.SelectedIndex = 3;
        Assert.AreEqual(4, changes);
        var settingsTop = labels[8].TranslatePoint(new Point(), sidebar).Y;
        Assert.IsTrue(settingsTop > height - 90 && settingsTop < height - 12, $"Settings top {settingsTop}, height {height}");
        Assert.IsTrue(labels[8].ActualHeight > 0);
        sidebar.ClearPages();
        for (var i = 0; i < 9; i++) sidebar.AddPage(i, new TextBlock { Text = $"Page {i}" }, i == 8);
        sidebar.SelectedIndex = 8;
        Assert.AreEqual(8, sidebar.SelectedIndex);
    });

    [TestMethod]
    public void SimulatorAndReplayNeverClaimALiveConnection()
    {
        Assert.AreNotEqual(MainWindow.SidebarStatusText(TelemetrySourceKind.Live, TelemetryStreamState.Live),
            MainWindow.SidebarStatusText(TelemetrySourceKind.Simulator, TelemetryStreamState.Replay));
        Assert.AreNotEqual(MainWindow.SidebarStatusText(TelemetrySourceKind.Live, TelemetryStreamState.Live),
            MainWindow.SidebarStatusText(TelemetrySourceKind.Live, TelemetryStreamState.Stale));
    }
}
