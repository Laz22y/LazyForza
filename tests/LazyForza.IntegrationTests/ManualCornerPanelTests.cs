using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ManualCornerPanelTests
{
    [TestMethod]
    [DataRow("zh-Hans", false, 600)]
    [DataRow("en", true, 900)]
    [DataRow("en", false, 600)]
    [DataRow("zh-Hans", true, 900)]
    [DoNotParallelize]
    public void LegacyLapMetricsAndOwnMarkersWorkWithoutAReference(string language, bool missingTrack, int width)
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-corner-ui-{Guid.NewGuid():N}.db");
        try
        {
            WpfTestHost.Run(() =>
            {
                AppLocalization.UseLanguage(language);
                using var store = new LazyForzaStore(path);
                var track = TestTrack();
                var source = TestLap(track);
                var legacy = source with { TrackRevision = null, Vehicle = source.Vehicle with { DrivetrainType = -1, NumCylinders = -1 } };
                var other = legacy with { Id = Guid.NewGuid() };
                store.SaveTrack(track, []); store.SaveLap(legacy); store.SaveLap(other);
                // A newer track is shorter. Historical analysis must use its own samples and markers.
                var current = missingTrack ? null : track with { LengthMeters = 300, UpdatedAt = track.UpdatedAt.AddHours(1) };
                var card = MainWindow.BuildManualCornerAnalysisCard(store, current, [legacy, other]);
                var selectors = Descendants<ComboBox>(card).ToArray();
                Assert.AreEqual(AppLocalization.Literal("仅分析本圈"), ((ComboBoxItem)selectors[1].SelectedItem).Content);
                Assert.IsTrue(Descendants<TextBlock>(card).Any(text => text.Text.Contains(AppLocalization.Literal(missingTrack
                    ? "缺少保存的赛道信息，无法确认两圈使用同一路线。" : "缺少路线修订信息，无法确认两圈使用同一路线。"))));
                SaveInterval(card);
                Assert.IsTrue(Descendants<TextBlock>(card).Any(text => text.Text == "6.400 s"));
                Assert.IsTrue(Descendants<TextBlock>(card).Any(text => text.Text == "72.0 km/h"));
                Assert.HasCount(1, Descendants<LapTelemetryChart>(card));
                Assert.IsNotNull(store.GetAppSetting($"cornerAnalysis.lap.v1.{legacy.Id:N}"));
                Assert.IsNull(store.LoadLap(legacy.Id)!.TrackRevision);
                Assert.AreEqual(-1, store.LoadLap(legacy.Id)!.Vehicle.DrivetrainType);
                selectors[0].SelectedIndex = 1;
                Assert.AreEqual(0, selectors[2].Items.Count, "Unknown routes must not share markers across laps.");
                selectors[0].SelectedIndex = 0;
                Assert.AreEqual(1, selectors[2].Items.Count);
                var rebuilt = MainWindow.BuildManualCornerAnalysisCard(store, current, [store.LoadLap(legacy.Id)!]);
                Assert.AreEqual(1, Descendants<ComboBox>(rebuilt).ElementAt(2).Items.Count);
                Assert.IsTrue(Descendants<TextBlock>(rebuilt).Any(text => text.Text == "6.400 s"));
                Descendants<TabControl>(rebuilt).Single().SelectedIndex = 1;
                Assert.HasCount(1, Descendants<LapInputChart>(rebuilt));
                rebuilt.Width = width;
                rebuilt.Measure(new Size(width, double.PositiveInfinity));
                rebuilt.Arrange(new Rect(new Point(), rebuilt.DesiredSize)); rebuilt.UpdateLayout();
                Assert.AreEqual(width, rebuilt.ActualWidth);
                if (Environment.GetEnvironmentVariable("LAZYFORZA_LEGACY_CORNER_QA") is { Length: > 0 } preview)
                {
                    Directory.CreateDirectory(preview);
                    PngReportExporter.Save(rebuilt, Path.Combine(preview, $"legacy-{language}-{width}.png"));
                }
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ReferenceSearchIncludesOlderHistoryAndInsufficientReferenceKeepsSingleLapMetrics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"corner-history-ui-{Guid.NewGuid():N}.db");
        try
        {
            WpfTestHost.Run(() =>
            {
                AppLocalization.UseLanguage("zh-Hans");
                using var store = new LazyForzaStore(path);
                var track = TestTrack();
                var selected = TestLap(track);
                var oldReference = selected with { Id = Guid.NewGuid(), StartedAt = selected.StartedAt.AddHours(-1),
                    Samples = selected.Samples.Where(sample => sample.S < 150 || sample.S > 200).ToArray() };
                store.SaveTrack(track, []); store.SaveLap(oldReference); store.SaveLap(selected);
                for (var i = 1; i <= 55; i++)
                    store.SaveLap(selected with { Id = Guid.NewGuid(), StartedAt = selected.StartedAt.AddMinutes(i),
                        Vehicle = selected.Vehicle with { PerformanceIndex = 900 } });
                Assert.IsFalse(store.LoadLapSummaries(track.Id).Any(lap => lap.Id == oldReference.Id));
                store.UpdateLapAnnotation(oldReference.Id, new(IsReference: true));
                var card = MainWindow.BuildManualCornerAnalysisCard(store, track, [selected]);
                SaveInterval(card);
                var reference = Descendants<ComboBox>(card).ElementAt(1);
                var oldItem = reference.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, oldReference.Id));
                Assert.AreSame(oldItem, reference.SelectedItem, "A compatible pinned reference outside the recent page must be selected automatically.");
                reference.SelectedItem = oldItem;
                Assert.IsTrue(Descendants<TextBlock>(card).Any(text => text.Text == "6.400 s"), "A missing reference interval must not hide this lap's metrics.");
                Assert.IsTrue(Descendants<TextBlock>(card).Any(text => text.Text.Contains("参考圈在此区间缺少可用样本")));
                reference.SelectedIndex = 0;
                Assert.IsFalse(Descendants<TextBlock>(card).Any(text => text.Text.Contains("参考圈在此区间缺少可用样本")));

                var missingVehicle = selected with { Vehicle = selected.Vehicle with { DrivetrainType = -1 } };
                var blocked = MainWindow.BuildManualCornerAnalysisCard(store, track, [missingVehicle]);
                Assert.IsTrue(Descendants<TextBlock>(blocked).Any(text => text.Text.Contains("缺少完整车辆信息")));
                SaveInterval(blocked);
                Assert.IsTrue(Descendants<TextBlock>(blocked).Any(text => text.Text == "6.400 s"));
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static TrackTemplate TestTrack()
    {
        var time = DateTimeOffset.UtcNow.AddDays(-1);
        return new(Guid.NewGuid(), "test", 1, "fh6_udp_live", null, [], 500, 0, 0, 0, 500, 0, 0, 10, 1, 1, time, time);
    }

    private static LapRecord TestLap(TrackTemplate track) => new(Guid.NewGuid(), track.Id, 1, 2, Guid.NewGuid(),
        new(123, 5, 850, 2, 8, 8000, "g3_200", "p300_t400_r7000"), track.UpdatedAt.AddHours(1), 20, true, null, [],
        Enumerable.Range(0, 101).Select(i => new LapSample(i * 5, i * .1, 20 + Math.Abs(i - 50) * .5, 5000, 3,
            i < 16 ? 1 : Math.Clamp((i - 60) / 2d, 0, 1), i is >= 20 and < 44 ? 1 : 0, 0, i * 5, 0, 0)).ToArray())
        { TrackRevision = LapTrackRevision.Create(track) };

    private static void SaveInterval(Border card)
    {
        var fields = Descendants<TextBox>(card).ToArray();
        fields[0].Text = "T1"; fields[1].Text = "80"; fields[2].Text = "400";
        Click(Descendants<Button>(card).Single(button => Equals(button.Content, AppLocalization.Literal("保存区间"))));
    }

    [TestMethod]
    [DoNotParallelize]
    public void SavedMarkersReferenceSelectionAndDifferenceNavigationSurviveRebuildingThePanel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"corner-ui-{Guid.NewGuid():N}.db");
        try
        {
            WpfTestHost.Run(() =>
            {
                AppLocalization.UseLanguage("zh-Hans");
                using var store = new LazyForzaStore(path);
                var time = DateTimeOffset.UtcNow.AddDays(-1);
                var track = new TrackTemplate(Guid.NewGuid(), "test", 1, "fh6_udp_live", null, [],
                    500, 0, 0, 0, 500, 0, 0, 10, 1, 1, time, time);
                store.SaveTrack(track, []);
                var vehicle = new VehicleProfileFingerprint(123, 5, 850, 2, 8, 8000, "learning", "learning");
                LapRecord Lap(double scale) => new(Guid.NewGuid(), track.Id, 1, 2, Guid.NewGuid(), vehicle,
                    time.AddHours(1), 20, true, null, [], Enumerable.Range(0, 101).Select(i =>
                        new LapSample(i * 5, i * .1 * scale, 20 + Math.Abs(i - 50) * .5, 5000, 3,
                            i < 16 ? 1 : Math.Clamp((i - 60) / 2d, 0, 1), i is >= 20 and < 44 ? 1 : 0,
                            0, i * 5, 0, 0)).ToArray()) { TrackRevision = LapTrackRevision.Create(track) };
                var selected = Lap(1.1);
                var reference = Lap(1);
                store.SaveLap(selected); store.SaveLap(reference);
                var navigated = false;
                var card = MainWindow.BuildManualCornerAnalysisCard(store, track, [selected], (id, progress) =>
                {
                    Assert.AreEqual(selected.Id, id);
                    Assert.IsTrue(progress >= 80 && progress <= 400);
                    navigated = true;
                });
                var textBoxes = Descendants<TextBox>(card).ToArray();
                var map = Descendants<TrackMapView>(card).Single();
                map.Measure(new Size(800, 300));
                map.Arrange(new Rect(0, 0, 800, 300));
                // This lap is a straight 500 m line, mapped into the viewport's 18 px padding.
                Point At(double s) => new(18.5 + s * 763 / 500, 150);
                Assert.IsFalse(map.CompleteIntervalPick(new Point(400, 20), wasDragged: false));
                Assert.AreEqual("", textBoxes[1].Text, "Empty canvas must not choose the last hovered position.");
                Assert.IsFalse(map.CompleteIntervalPick(At(80), wasDragged: true));
                Assert.AreEqual("", textBoxes[1].Text, "Panning must not choose an endpoint.");
                Assert.IsTrue(map.CompleteIntervalPick(At(80), wasDragged: false));
                Assert.AreEqual("80.0", textBoxes[1].Text);
                Assert.IsTrue(map.CompleteIntervalPick(At(90), wasDragged: false));
                Assert.AreEqual("", textBoxes[2].Text, "Too-short or reversed intervals remain in endpoint selection.");
                Assert.IsTrue(map.CompleteIntervalPick(At(400), wasDragged: false));
                Assert.AreEqual("400.0", textBoxes[2].Text);
                Assert.IsTrue(map.TryPickProgress(At(82.5), out var betweenSamples));
                Assert.AreEqual(82.5, betweenSamples, .001, "Pick between samples by projecting onto the line segment.");
                var viewportField = typeof(TrackMapView).GetField("viewport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                viewportField.SetValue(map, new ChartViewport(2, 60, 0));
                Assert.IsTrue(map.TryPickProgress(new Point(460, 151), out var zoomed));
                Assert.AreEqual(250, zoomed, .001, "Picking uses the current zoom and pan transform.");
                viewportField.SetValue(map, new ChartViewport(1, 0, 0));
                Click(Descendants<Button>(card).Single(button => Equals(button.Content, "保存区间")));
                var selectors = Descendants<ComboBox>(card).ToArray();
                Assert.AreEqual(2, selectors[1].Items.Count);
                selectors[1].SelectedIndex = 1;
                var jump = Descendants<Button>(card).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button).Contains("查看曲线"));
                Click(jump);
                Assert.IsTrue(navigated);
                Assert.HasCount(0, Descendants<LapInputChart>(card), "Input charts load only when their tab is opened.");
                var tabs = Descendants<TabControl>(card).Single();
                tabs.SelectedIndex = 1;
                Assert.HasCount(1, Descendants<LapInputChart>(card));
                tabs.SelectedIndex = 2;
                Assert.HasCount(2, Descendants<LapInputChart>(card));
                Click(jump);
                Assert.AreEqual(0, tabs.SelectedIndex, "Difference navigation reveals its chart even when an input tab is selected.");
                Assert.IsFalse(Descendants<Expander>(card).Single().IsExpanded, "Saving a marker closes the editor.");
                card.Measure(new Size(900, double.PositiveInfinity));
                card.Arrange(new Rect(new Point(), card.DesiredSize));
                card.UpdateLayout();
                if (Environment.GetEnvironmentVariable("LAZYFORZA_CORNER_PREVIEW") is { Length: > 0 } preview)
                    PngReportExporter.Save(card, preview);

                var rebuilt = MainWindow.BuildManualCornerAnalysisCard(store, track, [store.LoadLap(selected.Id)!]);
                Assert.AreEqual(1, Descendants<ComboBox>(rebuilt).ElementAt(2).Items.Count);
                Assert.AreEqual("80.0", Descendants<TextBox>(rebuilt).ElementAt(1).Text);
                Assert.AreEqual("400.0", Descendants<TextBox>(rebuilt).ElementAt(2).Text);
                Click(Descendants<Button>(rebuilt).Single(button => Equals(button.Content, "新增区间")));
                Assert.AreEqual("T2", Descendants<TextBox>(rebuilt).First().Text);
                Assert.AreEqual("", Descendants<TextBox>(rebuilt).ElementAt(1).Text);
                var revised = MainWindow.BuildManualCornerAnalysisCard(store, track with { UpdatedAt = time.AddSeconds(1) }, [selected]);
                Assert.AreEqual(0, Descendants<ComboBox>(revised).ElementAt(2).Items.Count);
                Assert.AreEqual(1, Descendants<ComboBox>(revised).ElementAt(1).Items.Count);

                // Tabs unload their old visuals. Returning must reconnect the shared cursor,
                // catch up to the current position and avoid accumulating subscriptions.
                var cursor = new LapAnalysisCursor();
                FrameworkElement[] visuals =
                [
                    new LapTelemetryChart([selected], 500, linkedCursor: cursor),
                    new LapInputChart(selected, 500, cursor),
                    new TrackMapView([selected], track, linkedCursor: cursor)
                ];
                foreach (var visual in visuals)
                {
                    visual.Measure(new Size(800, 300));
                    visual.Arrange(new Rect(0, 0, 800, 300));
                    var hover = visual.GetType().GetField("hover", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    for (var cycle = 0; cycle < 3; cycle++)
                    {
                        visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        cursor.Set(this, selected.Id, 100 + cycle * 10);
                        Assert.IsNotNull(hover.GetValue(visual));
                        visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                        var previous = hover.GetValue(visual);
                        cursor.Set(this, selected.Id, 300 + cycle * 10);
                        Assert.AreSame(previous, hover.GetValue(visual), "Hidden charts detach from cursor updates.");
                        visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        Assert.AreNotSame(previous, hover.GetValue(visual), "A returning chart catches up to the current cursor.");
                    }
                    visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                }

                var builds = 0;
                var lazyTabs = MainWindow.AnalysisTabs(("曲线", () => new TextBlock()), ("弯道", () => { builds++; return new TextBox { Text = "draft" }; }));
                Assert.AreEqual(0, builds);
                lazyTabs.SelectedIndex = 1;
                var draft = ((TabItem)lazyTabs.Items[1]).Content;
                lazyTabs.SelectedIndex = 0;
                lazyTabs.SelectedIndex = 1;
                Assert.AreSame(draft, ((TabItem)lazyTabs.Items[1]).Content, "Tab switches preserve local edits.");
                Assert.AreEqual(1, builds);
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants<T>(child)) yield return item;
    }
}
