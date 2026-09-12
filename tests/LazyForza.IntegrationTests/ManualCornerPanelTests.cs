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
    [DoNotParallelize]
    public void SavedMarkersReferenceSelectionAndDifferenceNavigationSurviveRebuildingThePanel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"corner-ui-{Guid.NewGuid():N}.db");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var application = new LazyForza.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.InitializeComponent();
            try
            {
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
                textBoxes[0].Text = "T1"; textBoxes[1].Text = "80"; textBoxes[2].Text = "400";
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
            }
            catch (Exception exception) { failure = exception; }
            finally { application.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        if (File.Exists(path)) File.Delete(path);
        Assert.IsNull(failure, failure?.ToString());
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants<T>(child)) yield return item;
    }
}
