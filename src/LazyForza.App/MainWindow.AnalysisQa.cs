using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    // Runs only through --capture-analysis-qa with an explicitly isolated data directory.
    internal async Task CaptureAnalysisQaAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var module = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
        await moduleManager.SetEnabledAsync(LapAnalysisModule.ModuleId, false, CancellationToken.None);
        var points = Enumerable.Range(0, 721).Select(index =>
        {
            var angle = index * Math.PI / 360;
            return new TrackPoint(400 * Math.Cos(angle) + 80 * Math.Cos(angle * 3),
                4 * Math.Sin(angle * 2), 300 * Math.Sin(angle), 0, 0, 0);
        }).ToArray();
        var track = TrackAlgorithms.BuildTemplate("Coastal Circuit", points) with { Source = CurrentTrackSource };
        var sectors = TrackAlgorithms.CreateSectors(track);
        store.SaveTrack(track, sectors);
        var vehicle = new VehicleProfileFingerprint(6001, 5, 850, 2, 8, 8000, "qa", "qa");
        var session = Guid.NewGuid();
        var laps = Enumerable.Range(0, 6).Select(index =>
        {
            var total = 86.21 + index * .39;
            return new LapRecord(Guid.NewGuid(), track.Id, track.Direction, TrackAlgorithms.SectorSchemaVersion, session,
                vehicle, DateTimeOffset.Now.AddMinutes(-30 + index * 2), total, true, null,
                sectors.Select(sector => new LapSegment(sector.Index, total * (sector.EndS - sector.StartS) / track.LengthMeters, true)).ToArray(),
                track.Points.Select(point =>
                {
                    var phase = point.S / track.LengthMeters * Math.PI * 6;
                    var speed = 43 + 15 * Math.Cos(phase + index * .015);
                    return new LapSample(point.S, point.S / track.LengthMeters * total, speed, 5500, 4,
                        Math.Clamp(.6 + Math.Cos(phase + .5), 0, 1), Math.Clamp(-Math.Sin(phase + .1) - .5, 0, 1),
                        0, point.X, point.Y, point.Z)
                    { Dynamics = new LapDynamics(Math.Sin(phase) * .25, default, default, new WheelValues(.1f, .15f, .1f, .15f)) };
                }).ToArray()) { TrackRevision = LapTrackRevision.Create(track), PlayerCode = "DRIVER 07" };
        }).ToArray();
        foreach (var lap in laps) store.SaveLap(lap);
        store.SetAppSetting($"cornerAnalysis.v1.{track.Id:N}.{track.Direction}.{TrackAlgorithms.SectorSchemaVersion}.{LapTrackRevision.Create(track)}",
            JsonSerializer.Serialize(new[] { new ManualCorner("T1", 100, 420), new ManualCorner("T2", 760, 1100) }));
        module.SelectTrack(track.Id);
        selectedLapIds.Clear();
        displayedLapIds.Clear();
        selectedLapIds.Add(laps[^1].Id);
        displayedLapIds.Add(laps[^1].Id);
        WindowState = WindowState.Normal;
        var audit = new SortedSet<string>();
        foreach (var (width, height) in new[] { (1440d, 900d), (960d, 640d) })
        {
            Width = width;
            Height = height;
            module.ClearTrackSelection();
            navigation.SelectedIndex = 4;
            RenderSelectedPage();
            await Capture(4, "empty");
            module.SelectTrack(track.Id);
            foreach (var page in new[] { 4, 5 })
            {
                navigation.SelectedIndex = page;
                RenderSelectedPage();
                await Capture(page, "overview");
                if (page == 4)
                {
                    var library = Descendants<Expander>(content).First(e => e.Header is TextBlock title && title.Text == AppLocalization.Literal("选择对比圈"));
                    library.IsExpanded = true;
                    await Capture(page, "lap-picker");
                    library.IsExpanded = false;
                }
                if (page == 5)
                {
                    var seek = Descendants<Slider>(content).Single();
                    seek.Value = seek.Maximum * .3;
                    var play = Descendants<Button>(content).Single(button => Equals(button.Content, AppLocalization.Text("replay.play", "播放")));
                    var before = seek.Value;
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(180);
                    if (seek.Value <= before) throw new InvalidOperationException("Replay did not advance after seeking and playing.");
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var paused = seek.Value;
                    await Task.Delay(100);
                    if (seek.Value != paused) throw new InvalidOperationException("Replay continued after pausing.");
                    await Capture(page, "paused");
                }
                var tabs = Descendants<TabControl>(content).FirstOrDefault();
                if (tabs is null) continue;
                tabs.SelectedIndex = 1;
                tabs.BringIntoView();
                await Capture(page, "map");
                tabs.SelectedIndex = 2;
                var corner = (tabs.SelectedItem as TabItem)?.Content;
                if (corner is DependencyObject cornerRoot)
                {
                    var selectors = Descendants<ComboBox>(cornerRoot).ToArray();
                    if (selectors.Length > 1 && selectors[1].Items.Count > 1) selectors[1].SelectedIndex = 1;
                }
                tabs.BringIntoView();
                await Capture(page, "corners");
                if (corner is DependencyObject editorRoot)
                {
                    var editor = Descendants<Expander>(editorRoot).FirstOrDefault();
                    if (editor is not null)
                    {
                        editor.IsExpanded = true;
                        editor.BringIntoView();
                        await Capture(page, "corner-editor");
                    }
                }
                if (page == 4)
                {
                    content.Content = Scroll(BuildCompetitionReviewCard(track.Name, 5, 850,
                        laps.Select(LapSummary.FromRecord).ToArray(), true, false));
                    await Capture(page, "review");
                    var reviewCorners = Descendants<Expander>(content).Single(expander => expander.Header is TextBlock title && title.Text == AppLocalization.Literal("弯道复盘"));
                    reviewCorners.IsExpanded = true;
                    reviewCorners.BringIntoView();
                    await Capture(page, "review-corners");
                }
            }
            async Task Capture(int page, string view)
            {
                AppLocalization.ApplyTo(content);
                UpdateLayout();
                await Task.Delay(120);
                CaptureVisual(this, Path.Combine(directory, $"{AppLocalization.CurrentLanguage}-{page}-{view}-{width:0}x{height:0}.png"));
                if (AppLocalization.CurrentLanguage == "en") CollectHanText(content, $"{page}/{view}", audit);
            }
        }
        File.WriteAllLines(Path.Combine(directory, $"{AppLocalization.CurrentLanguage}-han-audit.txt"), audit);

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T item) yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
