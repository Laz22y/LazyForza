using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;
using LazyForza.Storage;
using LazyForza.Telemetry;

namespace LazyForza.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class DeltaSettingsWindowTests
{
    [TestMethod]
    [DataRow("zh-Hans", 1000)]
    [DataRow("en", 1000)]
    [DataRow("en", 1440)]
    public void DeltaAndPinsWorkWithoutAnActiveRaceAndStaySynchronized(string language, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var previousNames = VehicleNameCatalog.Current;
        var root = Path.Combine(Path.GetTempPath(), "lfz-delta-ui-" + Guid.NewGuid().ToString("N"));
        var directories = new DataDirectoryService(root); directories.EnsureCreated();
        MainWindow? window = null; TelemetryHub? telemetry = null; ModuleManager? modules = null;
        TelemetryRecorderController? recorder = null; DiagnosticCaptureService? diagnostics = null;
        try
        {
            using var store = new LazyForzaStore(directories.DatabasePath);
            using var overlay = new OverlayCoordinator();
            telemetry = new(new SimulatorTelemetrySource(60), new TelemetryOptions());
            modules = new(BuiltInModuleCatalog.Create(store, TelemetrySourceKind.Simulator), new Context(telemetry, overlay, store, store, _ => { }));
            var lapModule = modules.Modules.OfType<LapAnalysisModule>().Single();
            recorder = new(telemetry, directories, store, TelemetrySourceKind.Simulator, lapModule, _ => { });
            diagnostics = new(telemetry, root, _ => { });
            using var updates = new ApplicationUpdateManager(store, directories,
                new(ApplicationDistributionKind.Development, Path.Combine(root, "profile.json"), Path.Combine(root, "init.json")), _ => { });
            updates.CheckOnStartup = false;
            var startup = new StartupProfileStore(Path.Combine(root, "profile.json"));
            startup.Save(StartupProfile.CreateDefault() with { Language = language, DataDirectory = root });
            window = new(modules, telemetry, overlay, store, directories, recorder, TelemetrySourceKind.Simulator,
                updates, diagnostics, new(modules, store), startup)
            { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Left = -10000; window.Top = -10000; window.Width = width; window.Height = 800; window.Show();
            var navigation = Children<SidebarNavigation>(window).Single();
            navigation.SelectedIndex = 2;
            Pump(() => Children<ToggleButton>(window).Any(button => AutomationProperties.GetAutomationId(button) == "delta-continuous"));
            var toggle = ById<ToggleButton>("delta-continuous");
            Pump(() => toggle.IsLoaded);
            var selector = ById<ComboBox>("delta-comparison");
            Assert.IsFalse(lapModule.Status.IsEnabled);
            Toggle(toggle, true);
            selector.SelectedValue = LapDeltaComparison.PerformanceIndexBest;
            Assert.AreEqual(new LapDeltaOptions(true, LapDeltaComparison.PerformanceIndexBest), lapModule.DeltaOptions);
            var pin = ById<ToggleButton>("quick-pin-" + SidebarQuickSettings.ContinuousDelta);
            Toggle(pin, true);
            Toggle(ById<ToggleButton>("quick-pin-" + SidebarQuickSettings.DeltaComparison), true);
            Assert.IsTrue(SidebarQuickSettings.Load(store.GetAppSetting(SidebarQuickSettings.StoreKey)).Contains(SidebarQuickSettings.ContinuousDelta));
            Click(Children<Button>(window).Single(button => AutomationProperties.GetName(button) ==
                AppLocalization.Text("delta.continuous", "连续实时 Delta")));
            Assert.IsFalse(toggle.IsChecked);
            window.UpdateLayout();
            var thumb = (FrameworkElement)toggle.Template.FindName("SwitchThumb", toggle);
            var state = (TextBlock)toggle.Template.FindName("SwitchState", toggle);
            var offThumbX = thumb.TranslatePoint(new Point(), toggle).X;
            Assert.AreEqual(language == "en" ? "Off" : "关", state.Text);
            Assert.AreEqual(state.Text, AutomationProperties.GetItemStatus(toggle));
            Capture(toggle, "switch-off");
            Click(Children<Button>(window).Single(button => AutomationProperties.GetName(button) ==
                AppLocalization.Text("delta.comparison", "Delta 对比")));
            var popup = (Popup)typeof(MainWindow).GetField("quickSettingPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Capture((FrameworkElement)popup.Child, "quick");
            Click(Children<Button>(popup.Child).Single(button => AutomationProperties.GetName(button) ==
                AppLocalization.Text("delta.carBest", "同车辆最佳")));
            Assert.AreEqual(LapDeltaComparison.CarBest, selector.SelectedValue);
            Assert.AreEqual(LapDeltaComparison.CarBest.ToString(), store.GetAppSetting(LapDeltaOptions.ComparisonSetting));
            Toggle(toggle, true);
            window.UpdateLayout();
            Assert.IsTrue(thumb.TranslatePoint(new Point(), toggle).X > offThumbX + 10,
                "The switch must distinguish on/off by position as well as color.");
            Assert.AreEqual(language == "en" ? "On" : "开", state.Text);
            Assert.AreEqual(state.Text, AutomationProperties.GetItemStatus(toggle));
            Capture(toggle, "switch-on");
            var deltaCard = Parent<Border>(toggle);
            foreach (var control in Children<Control>(deltaCard).Where(control => control.IsVisible))
            {
                var left = control.TranslatePoint(new Point(), deltaCard).X;
                Assert.IsTrue(left >= 0 && left + control.ActualWidth <= deltaCard.ActualWidth + 1, $"Clipped {control.GetType().Name}");
            }
            Capture((FrameworkElement)window.Content, "competition");

            navigation.SelectedIndex = 8;
            Click(Children<ToggleButton>(window).Single(button => Equals(button.Content, language == "en" ? "General" : "常规")));
            Pump(() => Children<CheckBox>(window).Any(check => Equals(check.Content, AppLocalization.Text("delta.continuous", "连续实时 Delta"))));
            var choice = Children<CheckBox>(window).Single(check => Equals(check.Content, AppLocalization.Text("delta.continuous", "连续实时 Delta")));
            Assert.IsTrue(choice.IsChecked);
            Toggle(choice, false);
            navigation.SelectedIndex = 2;
            Pump(() => Children<ToggleButton>(window).Any(button => AutomationProperties.GetAutomationId(button) == "delta-continuous"));
            Assert.IsFalse(ById<ToggleButton>("quick-pin-" + SidebarQuickSettings.ContinuousDelta).IsChecked);
            Assert.IsTrue(ById<ToggleButton>("delta-continuous").IsChecked, "Unpinning does not disable live Delta.");

            navigation.SelectedIndex = 8;
            Click(Children<ToggleButton>(window).Single(button => Equals(button.Content, "HUD")));
            Pump(() => Children<HudLayoutProfilesPanel>(window).Any());
            foreach (var id in new[] { SidebarQuickSettings.HudProfiles, SidebarQuickSettings.Motion, SidebarQuickSettings.ShiftIndicators,
                SidebarQuickSettings.HudOpacity, SidebarQuickSettings.EstateBackdropOpacity })
            {
                var pinButton = ById<ToggleButton>("quick-pin-" + id);
                Toggle(pinButton, true);
                Assert.IsTrue(SidebarQuickSettings.Load(store.GetAppSetting(SidebarQuickSettings.StoreKey)).Contains(id));
            }
            Capture((FrameworkElement)window.Content, "hud-pins");
            using var defaults = new OverlayCoordinator();
            Assert.AreEqual(defaults.CurrentLayout, overlay.CurrentLayout, "Pinning changes no HUD display settings.");

            // All existing quick actions expose the same pin control, including controls on other pages.
            var recording = (UIElement)typeof(MainWindow).GetMethod("BuildRecordingSettingsCard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            Assert.IsTrue(Children<ToggleButton>(recording).Any(button => AutomationProperties.GetAutomationId(button) == "quick-pin-" + SidebarQuickSettings.AutomaticRecording));
            var speech = (UIElement)typeof(MainWindow).GetMethod("BuildRaceEngineerControls", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            foreach (var id in new[] { SidebarQuickSettings.Mute, SidebarQuickSettings.Volume })
                Assert.IsTrue(Children<ToggleButton>(speech).Any(button => AutomationProperties.GetAutomationId(button) == "quick-pin-" + id));

            navigation.SelectedIndex = 4;
            Pump(() => Children<ToggleButton>(window).Any(button => AutomationProperties.GetAutomationId(button) == "record-unmatched-tracks"));
            var recordUnmatched = ById<ToggleButton>("record-unmatched-tracks");
            Pump(() => recordUnmatched.IsLoaded);
            Assert.IsFalse(recordUnmatched.IsChecked);
            window.UpdateLayout();
            var switchPosition = recordUnmatched.TranslatePoint(new Point(), window);
            Assert.IsTrue(switchPosition.Y < 300, "The switch must be visible at the top of Lap analysis without scrolling.");
            Assert.IsTrue(switchPosition.X + recordUnmatched.ActualWidth < window.ActualWidth);
            Capture((FrameworkElement)window.Content, "unmatched-off");
            Toggle(recordUnmatched, true);
            Assert.IsTrue(lapModule.RecordUnmatchedTracks);
            Assert.AreEqual(bool.TrueString, store.GetAppSetting(LapAnalysisModule.RecordUnmatchedTracksSettingKey));
            Toggle(ById<ToggleButton>("quick-pin-" + SidebarQuickSettings.RecordUnmatchedTracks), true);
            var quickRecordUnmatched = Children<Button>(window).Single(button =>
                AutomationProperties.GetName(button) == AppLocalization.Literal("记录未匹配赛道"));
            Click(quickRecordUnmatched);
            Assert.IsFalse(recordUnmatched.IsChecked);
            Assert.IsFalse(lapModule.RecordUnmatchedTracks);
            Click(quickRecordUnmatched);
            Assert.IsTrue(recordUnmatched.IsChecked);
            Capture((FrameworkElement)window.Content, "unmatched-on");
            navigation.SelectedIndex = 2;
            navigation.SelectedIndex = 4;
            Pump(() => Children<ToggleButton>(window).Any(button => AutomationProperties.GetAutomationId(button) == "record-unmatched-tracks"));
            Assert.IsTrue(ById<ToggleButton>("record-unmatched-tracks").IsChecked);
            Toggle(ById<ToggleButton>("quick-pin-" + SidebarQuickSettings.RecordUnmatchedTracks), false);
            Assert.IsTrue(lapModule.RecordUnmatchedTracks, "Removing the shortcut must not change recording preferences.");
            Assert.IsFalse(lapModule.Status.IsEnabled);
            Assert.AreEqual(0L, telemetry.Diagnostics.ValidPackets);

            T ById<T>(string id) where T : DependencyObject => Children<T>(window).Single(control => AutomationProperties.GetAutomationId(control) == id);
            void Capture(FrameworkElement element, string name)
            {
                if (Environment.GetEnvironmentVariable("LAZYFORZA_DELTA_QA") is not { Length: > 0 } output) return;
                window.UpdateLayout(); element.UpdateLayout(); Directory.CreateDirectory(output);
                VehicleNameUpdateWindowTests.Capture(element, window.Background, Path.Combine(output, $"delta-{name}-{language}-{width}.png"));
            }
        }
        finally
        {
            window?.Close(); recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult(); modules?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            diagnostics?.DisposeAsync().AsTask().GetAwaiter().GetResult(); telemetry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            VehicleNameCatalog.Activate(previousNames); Directory.Delete(root, true);
        }
    });

    private static T Parent<T>(FrameworkElement element) where T : FrameworkElement =>
        element.Parent is T parent ? parent : Parent<T>((FrameworkElement)element.Parent);
    private static void Toggle(ToggleButton button, bool value) { button.IsChecked = value; Click(button); }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Pump(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
            (_, _) => { if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(completed());
    }
    private static IEnumerable<T> Children<T>(DependencyObject root)
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var value in Children<T>(child)) yield return value;
    }
    private sealed record Context(ITelemetryFeed Telemetry, IHudHost Hud, IModuleSettingsStore Settings,
        IAnalysisStore AnalysisStore, Action<string> Log) : IModuleContext;
}
