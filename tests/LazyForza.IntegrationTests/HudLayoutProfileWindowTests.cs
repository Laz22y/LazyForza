using System.Text.Json;
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
public sealed class HudLayoutProfileWindowTests
{
    [TestMethod]
    [DataRow("zh-Hans", 1000)]
    [DataRow("en", 1440)]
    public void LayoutsSavePendingEditsSwitchThroughSidebarAndPreserveModules(string language, int width) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var previousNames = VehicleNameCatalog.Current;
        var root = Path.Combine(Path.GetTempPath(), "lfz-layout-window-" + Guid.NewGuid().ToString("N"));
        var directories = new DataDirectoryService(root); directories.EnsureCreated();
        MainWindow? window = null;
        TelemetryHub? telemetry = null;
        ModuleManager? modules = null;
        TelemetryRecorderController? recorder = null;
        DiagnosticCaptureService? diagnostics = null;
        try
        {
            using var store = new LazyForzaStore(directories.DatabasePath);
            var initial = OverlayLayoutGeometry.Normalize(new OverlayLayout(Opacity: .75, Left: -900));
            using var overlay = new OverlayCoordinator(initial);
            store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save([SidebarQuickSettings.HudProfiles, SidebarQuickSettings.HudOpacity]));
            telemetry = new(new SimulatorTelemetrySource(60), new TelemetryOptions());
            modules = new(BuiltInModuleCatalog.Create(store, TelemetrySourceKind.Simulator),
                new Context(telemetry, overlay, store, store, _ => { }));
            var initialStates = modules.Modules.Select(module => module.Status).ToArray();
            recorder = new(telemetry, directories, store, TelemetrySourceKind.Simulator,
                modules.Modules.OfType<LapAnalysisModule>().Single(), _ => { });
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
            Children<SidebarNavigation>(window).Single().SelectedIndex = 8;
            Click(Children<ToggleButton>(window).Single(button => Equals(button.Content, "HUD")));
            Pump(() => Children<HudLayoutProfilesPanel>(window).Any());
            HudLayoutProfilesPanel Panel() => Children<HudLayoutProfilesPanel>(window).Single();
            ComboBox Selector() => Children<ComboBox>(Panel()).Single();
            OverlayLayoutProfileCollection Saved() => JsonSerializer.Deserialize<OverlayLayoutProfileCollection>(store.GetAppSetting(OverlayLayoutProfileStore.SettingKey)!)!;
            var first = (Guid)Selector().SelectedValue;
            Click(Children<Button>(Panel()).Single(button => Equals(button.Content, language == "en" ? "New layout" : "新建方案")));
            var name = Children<TextBox>(Panel()).Single(); name.Text = "Streaming";
            var create = Children<Button>(Panel()).Single(button => Equals(button.Content, language == "en" ? "Create" : "创建"));
            Capture(Panel(), "name");
            Click(create);
            Pump(() => !ReferenceEquals(name, Children<TextBox>(Panel()).Single()));
            var second = (Guid)Selector().SelectedValue;
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(initial, overlay.CurrentLayout);

            // Change a slider and switch before its debounce timer runs.
            var opacity = Children<Slider>(window).Single(slider => AutomationProperties.GetName(slider) ==
                AppLocalization.Literal("整体不透明度"));
            opacity.Value = .45;
            Selector().SelectedValue = first;
            Pump(() => overlay.CurrentLayout.Opacity == .75 && Saved().ActiveId == first);
            Assert.AreEqual(.45, Saved().Profiles.Single(profile => profile.Id == second).Layout.Opacity);
            Pump(() => !ReferenceEquals(opacity, Children<Slider>(window).Single(slider => AutomationProperties.GetName(slider) ==
                AppLocalization.Literal("整体不透明度"))));

            Click(Children<Button>(window).Single(button => AutomationProperties.GetName(button) == HudLayoutProfilesPanel.Title));
            var popupField = typeof(MainWindow).GetField("quickSettingPopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Pump(() => popupField.GetValue(window) is Popup { IsOpen: true });
            var popup = (Popup)popupField.GetValue(window)!;
            Capture((FrameworkElement)popup.Child, "quick");
            Click(Children<Button>(popup.Child).Single(button => AutomationProperties.GetName(button) == "Streaming"));
            Pump(() => overlay.CurrentLayout.Opacity == .45 && Saved().ActiveId == second);
            Pump(() => (Guid)Selector().SelectedValue == second);

            // The rebuilt page must read the new profile and leave the first one untouched.
            var currentOpacity = Children<Slider>(window).Single(slider => AutomationProperties.GetName(slider) ==
                AppLocalization.Literal("整体不透明度"));
            Assert.AreEqual(.45, currentOpacity.Value);
            currentOpacity.Value = .6;
            PumpTask(window.FlushSettingsAsync());
            Assert.AreEqual(.75, Saved().Profiles.Single(profile => profile.Id == first).Layout.Opacity);
            Assert.AreEqual(.6, Saved().Profiles.Single(profile => profile.Id == second).Layout.Opacity);
            var count = Saved().Profiles.Count;
            var beforeRenamePanel = Panel();
            Click(Children<Button>(Panel()).Single(button => AutomationProperties.GetName(button) ==
                (language == "en" ? "Rename layout" : "重命名方案")));
            Children<TextBox>(Panel()).Single().Text = "Stream HUD";
            Click(Children<Button>(Panel()).Single(button => Equals(button.Content, AppLocalization.Literal("保存"))));
            Pump(() => Saved().Profiles.Single(profile => profile.Id == second).Name == "Stream HUD");
            Pump(() => !ReferenceEquals(beforeRenamePanel, Panel()) && Panel().IsEnabled);
            window.UpdateLayout();
            Assert.IsTrue(VisualChildren<TextBlock>(Selector()).Any(text => text.Text == "Stream HUD"),
                "The shipping ComboBox template must render the profile name, not the record's debug text.");
            Selector().IsDropDownOpen = true;
            window.UpdateLayout();
            var dropdown = (Popup)Selector().Template.FindName("Popup", Selector());
            Pump(() => dropdown.IsOpen && ((FrameworkElement)dropdown.Child).ActualWidth > 0);
            Capture((FrameworkElement)dropdown.Child, "dropdown");
            Selector().IsDropDownOpen = false;
            Assert.AreEqual(count, Saved().Profiles.Count);
            CollectionAssert.AreEqual(initialStates, modules.Modules.Select(module => module.Status).ToArray());
            Assert.AreEqual(0L, telemetry.Diagnostics.ValidPackets);
            window.UpdateLayout();
            var panel = Panel();
            foreach (var control in Children<Control>(panel).Where(control => control.IsVisible))
            {
                var left = control.TranslatePoint(new Point(), panel).X;
                Assert.IsTrue(left >= 0 && left + control.ActualWidth <= panel.ActualWidth + 1,
                    $"Clipped control: {control.GetType().Name}, {left}, {control.ActualWidth}, panel {panel.ActualWidth}");
            }
            Capture((FrameworkElement)window.Content, "window");

            // Cancel leaves the active layout intact; confirmation applies the remaining layout.
            var confirm = false;
            var confirmations = 0;
            var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<AppDialogWindow>().FirstOrDefault();
                if (dialog is null) return;
                confirmations++;
                Click(Children<Button>(dialog).Single(button => Equals(button.Content,
                    AppLocalization.Text(confirm ? "dialog.yes" : "dialog.no", confirm ? "是" : "否"))));
            }, Dispatcher.CurrentDispatcher);
            try
            {
                Button Delete() => Children<Button>(Panel()).Single(button => AutomationProperties.GetName(button) ==
                    (language == "en" ? "Delete layout" : "删除方案"));
                Click(Delete());
                Assert.AreEqual(second, Saved().ActiveId);
                confirm = true; Click(Delete());
                Pump(() => Saved().Profiles.Count == 1 && (Guid)Selector().SelectedValue == first && Panel().IsEnabled);
                Assert.AreEqual(.75, overlay.CurrentLayout.Opacity);
                Assert.IsFalse(Delete().IsEnabled);
                Assert.AreEqual(2, confirmations);
            }
            finally { closer.Stop(); }

            void Capture(FrameworkElement element, string name)
            {
                if (Environment.GetEnvironmentVariable("LAZYFORZA_HUD_PROFILES_QA") is not { Length: > 0 } output) return;
                window.UpdateLayout(); element.UpdateLayout(); Directory.CreateDirectory(output);
                VehicleNameUpdateWindowTests.Capture(element, window.Background, Path.Combine(output, $"hud-profiles-{name}-{language}-{width}.png"));
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
            Directory.Delete(root, true);
        }
    });

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void PumpTask(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Pump(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
            (_, _) => { if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(completed(), "HUD layout operation timed out.");
    }
    private static IEnumerable<T> Children<T>(DependencyObject root)
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item2 in Children<T>(child)) yield return item2;
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root)
    {
        if (root is T item) yield return item;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualChildren<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed record Context(ITelemetryFeed Telemetry, IHudHost Hud, IModuleSettingsStore Settings,
        IAnalysisStore AnalysisStore, Action<string> Log) : IModuleContext;
}
