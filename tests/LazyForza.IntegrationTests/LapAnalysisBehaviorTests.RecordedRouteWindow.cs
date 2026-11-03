using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;
using LazyForza.Storage;
using LazyForza.Telemetry;

namespace LazyForza.IntegrationTests;

public sealed partial class LapAnalysisBehaviorTests
{
    [TestMethod]
    [DataRow("zh-Hans", 1000)]
    [DataRow("en", 1440)]
    [DoNotParallelize]
    public void RecordedRouteAnalyzesAndEnrollsThroughTheActualWindow(string language, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var previousNames = VehicleNameCatalog.Current;
        var root = Path.Combine(Path.GetTempPath(), "lfz-recorded-window-" + Guid.NewGuid().ToString("N"));
        var directories = new DataDirectoryService(root); directories.EnsureCreated();
        MainWindow? window = null;
        TelemetryHub? telemetry = null;
        ModuleManager? modules = null;
        TelemetryRecorderController? recorder = null;
        DiagnosticCaptureService? diagnostics = null;
        try
        {
            using var store = new LazyForzaStore(directories.DatabasePath);
            using var overlay = new OverlayCoordinator();
            telemetry = new(new SimulatorTelemetrySource(60), new TelemetryOptions());
            modules = new(BuiltInModuleCatalog.Create(store, TelemetrySourceKind.Simulator),
                new RecordedWindowContext(telemetry, overlay, store, store, _ => { }));
            var lapModule = modules.Modules.OfType<LapAnalysisModule>().Single();
            lapModule.SetRecordUnmatchedTracks(true);
            var feed = new CircleFrameFeed(lapModule);
            feed.Drive(0, 0, 239); feed.Send(1, 0);
            var routeId = lapModule.CurrentTrack!.Id;
            recorder = new(telemetry, directories, store, TelemetrySourceKind.Simulator, lapModule, _ => { });
            diagnostics = new(telemetry, root, _ => { });
            using var updates = new ApplicationUpdateManager(store, directories,
                new(ApplicationDistributionKind.Development, Path.Combine(root, "profile.json"), Path.Combine(root, "initialization.json")), _ => { });
            updates.CheckOnStartup = false;
            var profiles = new StartupProfileStore(Path.Combine(root, "profile.json"));
            profiles.Save(StartupProfile.CreateDefault() with { Language = language, DataDirectory = root });
            window = new(modules, telemetry, overlay, store, directories, recorder, TelemetrySourceKind.Simulator,
                updates, diagnostics, new(modules, store), profiles)
            { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Left = -10000; window.Top = -10000;
            window.Width = width; window.Height = 800;
            window.Show();
            WindowChildren<SidebarNavigation>(window).Single().SelectedIndex = 4;
            var tabs = WindowChildren<TabControl>(window).Single(tab => tab.Items.OfType<TabItem>().Any(item =>
                Equals(item.Header, AppLocalization.Literal("单圈对比"))));
            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            Assert.IsTrue(WindowChildren<TextBlock>(window).Any(text => text.Text == AppLocalization.Literal("未收录路线")));
            Assert.IsTrue(WindowChildren<TabControl>(window).Any(tab => tab.Items.OfType<TabItem>().Any(item =>
                Equals(item.Header, AppLocalization.Literal("曲线")))));
            var enroll = WindowChildren<Button>(window).Single(button => Equals(button.Content, AppLocalization.Literal("收录到个人赛道库")));
            Assert.IsTrue(enroll.IsEnabled);
            if (Environment.GetEnvironmentVariable("LAZYFORZA_RECORDED_ROUTE_QA") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                VehicleNameUpdateWindowTests.Capture((FrameworkElement)window.Content, window.Background,
                    Path.Combine(output, $"recorded-window-{language}-{width}.png"));
            }
            enroll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(store.LoadTrack(routeId)!.Value.Track.IsRecordOnly);
            Assert.IsFalse(WindowChildren<Button>(window).Any(button => Equals(button.Content, AppLocalization.Literal("收录到个人赛道库"))));
            Assert.AreEqual(1, store.CountLaps(routeId));

            var savedTrack = store.LoadTrack(routeId)!.Value;
            var emptyTrack = savedTrack.Track with { Id = Guid.NewGuid(), Name = "Empty track" };
            store.SaveTrack(emptyTrack, savedTrack.Sectors.Select(sector => sector with { TrackId = emptyTrack.Id }).ToArray());
            lapModule.ClearTrackSelection();
            var navigation = WindowChildren<SidebarNavigation>(window).Single();
            navigation.SelectedIndex = 0;
            navigation.SelectedIndex = 4;
            SelectTrack(emptyTrack.Id);
            AssertEmptyState("无圈速记录");

            SelectTrack(routeId);
            var classFilter = WindowChildren<ToggleButton>(window).Single(button =>
                button.Style == Application.Current.Resources["PerformanceClassToggle"]);
            classFilter.IsChecked = false;
            classFilter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            AssertEmptyState("未选择性能等级");

            void SelectTrack(Guid id)
            {
                var selector = WindowChildren<ComboBox>(window).Single(combo => combo.Items.OfType<ComboBoxItem>()
                    .Any(item => Equals(item.Tag, id)));
                selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
            }

            void AssertEmptyState(string expected)
            {
                var archiveTabs = WindowChildren<TabControl>(window).Single(tab => tab.Items.OfType<TabItem>().Any(item =>
                    Equals(item.Header, AppLocalization.Literal("单圈对比"))));
                foreach (var index in new[] { 0, 1 })
                {
                    archiveTabs.SelectedIndex = index;
                    window.UpdateLayout();
                    var texts = WindowChildren<TextBlock>((DependencyObject)((TabItem)archiveTabs.Items[index]).Content)
                        .Select(text => text.Text).ToArray();
                    CollectionAssert.Contains(texts, AppLocalization.Literal(expected));
                    CollectionAssert.DoesNotContain(texts, AppLocalization.Literal(expected == "无圈速记录" ? "未选择性能等级" : "无圈速记录"));
                }
            }
        }
        finally
        {
            window?.Close();
            recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            modules?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            diagnostics?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            telemetry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            VehicleNameCatalog.Activate(previousNames);
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    });

    private static IEnumerable<T> WindowChildren<T>(DependencyObject root)
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in WindowChildren<T>(child)) yield return item;
    }
    private sealed record RecordedWindowContext(ITelemetryFeed Telemetry, IHudHost Hud, IModuleSettingsStore Settings,
        IAnalysisStore AnalysisStore, Action<string> Log) : IModuleContext;
}
