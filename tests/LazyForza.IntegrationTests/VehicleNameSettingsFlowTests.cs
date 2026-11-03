using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
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
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class VehicleNameSettingsFlowTests
{
    [TestMethod]
    [DataRow("zh-Hans")]
    [DataRow("en")]
    public void StartupAndSettingsShareConsentIgnoreAndDownload(string language) => WpfTestHost.Run(() =>
    {
        AppLocalization.UseLanguage(language);
        var previous = VehicleNameCatalog.Current;
        var root = Path.Combine(Path.GetTempPath(), "lfz-name-flow-" + Guid.NewGuid().ToString("N"));
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
            telemetry = new TelemetryHub(new SimulatorTelemetrySource(60), new TelemetryOptions());
            modules = new ModuleManager(BuiltInModuleCatalog.Create(store, TelemetrySourceKind.Simulator),
                new Context(telemetry, overlay, store, store, _ => { }));
            recorder = new(telemetry, directories, store, TelemetrySourceKind.Simulator,
                modules.Modules.OfType<LapAnalysisModule>().Single(), _ => { });
            diagnostics = new(telemetry, root, _ => { });
            using var appUpdates = new ApplicationUpdateManager(store, directories,
                new(ApplicationDistributionKind.Development, Path.Combine(root, "profile.json"), Path.Combine(root, "initialization.json")), _ => { });
            appUpdates.CheckOnStartup = false; // Vehicle checks are independent of the app update preference.
            var handler = new GistHandler();
            var catalogUpdates = new VehicleNameUpdateManager(store, directories, new(handler));
            var profiles = new StartupProfileStore(Path.Combine(root, "profile.json"));
            profiles.Save(StartupProfile.CreateDefault() with { Language = language, DataDirectory = root });
            window = new(modules, telemetry, overlay, store, directories, recorder, TelemetrySourceKind.Simulator,
                appUpdates, diagnostics, new(modules, store), profiles, catalogUpdates)
            { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Left = -10000; window.Top = -10000;
            window.Show();

            var choice = VehicleNameUpdateChoice.Later;
            var offers = 0;
            Exception? callbackFailure = null;
            var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) =>
            {
                try
                {
                    var offer = Application.Current.Windows.OfType<VehicleNameUpdateWindow>().FirstOrDefault();
                    if (offer is null) return;
                    offers++;
                    var actions = ((Grid)offer.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                    actions[choice switch { VehicleNameUpdateChoice.Ignore => 0, VehicleNameUpdateChoice.Later => 1, _ => 2 }]
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { callbackFailure = error; }
            }, window.Dispatcher);
            try
            {
                var startup = window.CheckStartupUpdatesAsync();
                PumpUntil(() => startup.IsCompleted);
                startup.GetAwaiter().GetResult();
                Assert.AreEqual(1, offers);
                Assert.AreEqual(0, handler.Downloads, "Later must not download data.");

                Descendants<SidebarNavigation>(window).Single().SelectedIndex = 8;
                Descendants<ToggleButton>(window).Single(button => Equals(button.Content,
                    language == "en" ? "Maintenance" : "维护")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Button CheckButton() => Descendants<Button>(window).Single(button => Equals(button.Content,
                    language == "en" ? "Check vehicle name updates" : "检查车辆名称更新"));
                void Check()
                {
                    var requests = handler.Checks;
                    CheckButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => handler.Checks > requests && CheckButton().IsEnabled);
                    if (callbackFailure is not null) throw callbackFailure;
                }

                choice = VehicleNameUpdateChoice.Ignore;
                Check();
                Assert.AreEqual(2, offers);
                Check();
                Assert.AreEqual(2, offers, "Manual checks must respect the ignored revision.");
                Assert.AreEqual(0, handler.Downloads);
                Assert.AreEqual(handler.Revision, store.GetAppSetting(VehicleNameUpdateManager.IgnoredRevisionSetting));

                handler.Revision = new string('b', 40);
                choice = VehicleNameUpdateChoice.Download;
                Check();
                Assert.AreEqual(3, offers);
                Assert.AreEqual(1, handler.Downloads);
                Assert.AreEqual("New catalog vehicle", VehicleNameCatalog.TryGetName(999991));
                Assert.IsTrue(File.Exists(Path.Combine(root, "Cache", "vehicle-names.json")));
                Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.Text.Contains(handler.Revision, StringComparison.Ordinal)));
                Check();
                Assert.AreEqual(3, offers, "An installed revision must not prompt or download again.");
                Assert.AreEqual(1, handler.Downloads);

                if (Environment.GetEnvironmentVariable("LAZYFORZA_VEHICLE_NAMES_QA") is { Length: > 0 } output)
                {
                    window.UpdateLayout();
                    var card = Ancestor<Border>(CheckButton());
                    card.BringIntoView();
                    window.UpdateLayout();
                    Directory.CreateDirectory(output);
                    VehicleNameUpdateWindowTests.Capture((FrameworkElement)window.Content, window.Background,
                        Path.Combine(output, $"vehicle-name-settings-{language}.png"));
                }
            }
            finally { closer.Stop(); window.Close(); }
        }
        finally
        {
            window?.Close();
            recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            modules?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            diagnostics?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            telemetry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            VehicleNameCatalog.Activate(previous);
            Directory.Delete(root, true);
        }
    });

    private static void PumpUntil(Func<bool> finished)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background,
            (_, _) => { if (finished() || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(finished(), "Vehicle name UI operation did not finish.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
    private static T Ancestor<T>(FrameworkElement element) where T : FrameworkElement =>
        element.Parent is T result ? result : Ancestor<T>((FrameworkElement)element.Parent);

    private sealed record Context(ITelemetryFeed Telemetry, IHudHost Hud, IModuleSettingsStore Settings,
        IAnalysisStore AnalysisStore, Action<string> Log) : IModuleContext;

    private sealed class GistHandler : HttpMessageHandler
    {
        public string Revision { get; set; } = new('a', 40);
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object response;
            if (request.RequestUri!.AbsoluteUri.Contains("/commits?", StringComparison.Ordinal))
            {
                Checks++;
                response = new[] { new { version = Revision, committed_at = "2030-01-01T00:00:00Z", change_status = new { additions = 1, deletions = 0 } } };
            }
            else
            {
                Downloads++;
                var names = Enumerable.Range(1, 100).ToDictionary(i => "Car " + i, i => i);
                names["New catalog vehicle"] = 999991;
                response = new { id = VehicleNameCatalogClient.GistId, history = new[] { new { version = Revision } },
                    files = new Dictionary<string, object> { [VehicleNameCatalogClient.FileName] = new { truncated = false, content = JsonSerializer.Serialize(names) } } };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
}
