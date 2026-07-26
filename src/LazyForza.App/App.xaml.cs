using System.Text.Json;
using System.IO;
using System.Windows;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Overlay;
using LazyForza.Storage;
using LazyForza.Telemetry;

namespace LazyForza.App;

public partial class App : Application
{
    private LazyForzaStore? store;
    private TelemetryHub? telemetry;
    private OverlayCoordinator? overlay;
    private ModuleManager? moduleManager;
    private TelemetryRecorderController? recorder;
    private RollingLog? log;
    private ApplicationUpdateManager? updateManager;
    private DiagnosticCaptureService? diagnosticCapture;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var directories = new DataDirectoryService(DataRoot(e.Args));
            directories.EnsureCreated();
            var replayPath = ReplayPath(e.Args);
            var captureDirectory = CaptureDirectory(e.Args);
            var recordSeconds = AutoRecordSeconds(e.Args);
            var simulatorRequested = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase) || captureDirectory is not null || recordSeconds is not null;
            var isolatedData = simulatorRequested || replayPath is not null;
            var databasePath = isolatedData ? Path.Combine(directories.Root, "lazyforza-sandbox.db") : directories.DatabasePath;
            var applicationVersion = ApplicationVersion();
            var migrationBackup = isolatedData
                ? null
                : DataBackupService.CreatePreMigrationSnapshotIfNeeded(
                    databasePath,
                    directories.BackupsPath,
                    applicationVersion);
            store = new LazyForzaStore(databasePath);
            if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
                await EnsureDemoVehicleProfilesAsync(store);
            var catalogImport = PlaygroundOfficialTrackCatalog.EnsureImported(store);
            var listenAddress = store.GetAppSetting("telemetry.listenAddress") ?? LazyForzaDefaults.TelemetryListenAddress;
            var port = int.TryParse(store.GetAppSetting("telemetry.port"), out var savedPort) && savedPort is > 0 and <= 65535
                ? savedPort
                : LazyForzaDefaults.TelemetryPort;
            var options = new TelemetryOptions(listenAddress, port, SubscriberCapacity: 1024);
            var source = replayPath is not null
                ? (ITelemetrySource)new TelemetryReplaySource(replayPath, 1, e.Args.Contains("--loop-replay", StringComparer.OrdinalIgnoreCase))
                : simulatorRequested
                    ? new SimulatorTelemetrySource(60)
                    : new UdpTelemetrySource(options);
            telemetry = new TelemetryHub(source, options);
            var savedLayout = DeserializeLayout(store.GetAppSetting("overlay.layout"));
            overlay = new OverlayCoordinator(savedLayout);
            log = new RollingLog(directories.LogsPath);
            log.Write($"Data database {databasePath}");
            if (migrationBackup is not null)
                log.Write($"Pre-migration database snapshot created: {migrationBackup}");
            log.Write(
                $"{PlaygroundOfficialTrackCatalog.DisplayName} catalog {catalogImport.Version}: " +
                $"{catalogImport.TotalTracks} tracks available, {catalogImport.ImportedTracks} imported or refreshed.");
            log.Write($"Starting with source {source.Description}");
            diagnosticCapture = new DiagnosticCaptureService(
                telemetry,
                directories.Root,
                message => log.Write(message));
            await diagnosticCapture.StartAsync(CancellationToken.None);
            updateManager = new ApplicationUpdateManager(store, directories, message => log.Write(message));
            var context = new ModuleContext(
                telemetry,
                overlay,
                store,
                store,
                message =>
                {
                    log.Write(message);
                    diagnosticCapture.RecordLog("module", message);
                });
            var modules = BuiltInModuleCatalog.Create(
                store,
                source.Kind,
                () => overlay.TimingLayout,
                diagnosticCapture.RecordSignal);
            moduleManager = new ModuleManager(modules, context);
            await moduleManager.InitializeAsync(CancellationToken.None);
            foreach (var module in moduleManager.Modules)
            {
                var saved = await store.GetAsync(module.Descriptor.Id, "enabled", CancellationToken.None);
                var enabled = captureDirectory is not null || saved is null || bool.TryParse(saved, out var parsed) && parsed;
                if (enabled) await moduleManager.SetEnabledAsync(module.Descriptor.Id, true, CancellationToken.None);
            }

            recorder = new TelemetryRecorderController(telemetry, directories);
            MainWindow = new MainWindow(
                moduleManager,
                telemetry,
                overlay,
                store,
                directories,
                recorder,
                source.Kind,
                updateManager,
                diagnosticCapture);
            MainWindow.Show();
            if (captureDirectory is null && recordSeconds is null)
                _ = ((MainWindow)MainWindow).CheckForUpdatesOnStartupAsync();
            if (captureDirectory is not null) _ = CaptureQaAsync(captureDirectory);
            else if (recordSeconds is not null) _ = AutoRecordAndExitAsync(recordSeconds.Value);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"LazyForza could not start.\n\n{exception}", "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (store is not null && overlay is not null)
        {
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(overlay.CurrentLayout));
        }

        if (recorder is not null) await recorder.DisposeAsync();
        if (moduleManager is not null) await moduleManager.DisposeAsync();
        if (diagnosticCapture is not null) await diagnosticCapture.DisposeAsync();
        if (telemetry is not null) await telemetry.DisposeAsync();
        overlay?.Dispose();
        updateManager?.Dispose();
        store?.Dispose();
        log?.Dispose();
        base.OnExit(e);
    }

    private static string? ReplayPath(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("--replay=", StringComparison.OrdinalIgnoreCase)) return arguments[index][9..];
            if (arguments[index].Equals("--replay", StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count) return arguments[index + 1];
        }
        return null;
    }

    private static string? CaptureDirectory(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("--capture-qa=", StringComparison.OrdinalIgnoreCase)) return arguments[index][13..];
            if (arguments[index].Equals("--capture-qa", StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count) return arguments[index + 1];
        }
        return null;
    }

    private static int? AutoRecordSeconds(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            string? value = null;
            if (arguments[index].StartsWith("--auto-record-seconds=", StringComparison.OrdinalIgnoreCase)) value = arguments[index][22..];
            else if (arguments[index].Equals("--auto-record-seconds", StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count) value = arguments[index + 1];
            if (int.TryParse(value, out var seconds) && seconds is >= 1 and <= 600) return seconds;
        }
        return null;
    }

    private static string? DataRoot(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                return arguments[index][11..];
            if (arguments[index].Equals("--data-dir", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
                return arguments[index + 1];
        }

        return Environment.GetEnvironmentVariable("LAZYFORZA_DATA_DIR");
    }

    private static string ApplicationVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        return version is null
            ? "unknown"
            : version.Build >= 0
                ? version.ToString(3)
                : version.ToString(2);
    }

    private async Task AutoRecordAndExitAsync(int seconds)
    {
        try
        {
            await recorder!.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await recorder.StopAsync(CancellationToken.None);
            log?.Write($"QA automatic recording completed: {recorder.CurrentPath}");
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task CaptureQaAsync(string directory)
    {
        try
        {
            await Task.Delay(1800);
            var original = overlay!.CurrentLayout;
            foreach (var size in new[] { (Width: 1280d, Height: 720d), (Width: 1920d, Height: 1080d), (Width: 2560d, Height: 1440d) })
            {
                await overlay.SetLayoutAsync(original with { Width = size.Width, Height = size.Height, Scale = 1 }, CancellationToken.None);
                await Task.Delay(100);
                await overlay.CapturePngAsync(Path.Combine(directory, $"hud-{size.Width:0}x{size.Height:0}-demo.png"), CancellationToken.None);
            }
            await overlay.SetLayoutAsync(original, CancellationToken.None);
        }
        finally
        {
            Shutdown();
        }
    }

    private static OverlayLayout DeserializeLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return LazyForzaDefaults.CreateOverlayLayout();
        try { return JsonSerializer.Deserialize<OverlayLayout>(json) ?? LazyForzaDefaults.CreateOverlayLayout(); }
        catch (JsonException) { return LazyForzaDefaults.CreateOverlayLayout(); }
    }

    private static async ValueTask EnsureDemoVehicleProfilesAsync(LazyForzaStore store)
    {
        var streetFingerprint = new VehicleProfileFingerprint(
            6001, 6, 917, 2, 8, 8_500,
            "g4_136-g5_106-g6_86",
            "p80_t61_r7000");
        var raceFingerprint = streetFingerprint with
        {
            GearSlopeSignature = "g4_144-g5_112-g6_91",
            CurveSignature = "p86_t65_r7200"
        };

        static ShiftLearningSnapshot Snapshot(
            VehicleProfileFingerprint fingerprint,
            double confidence,
            double powerMultiplier) => new(
                LearningState.Ready,
                1,
                confidence,
                fingerprint,
                Enumerable.Range(0, 10)
                    .Select(index =>
                    {
                        var rpm = 3_000 + index * 500;
                        var torque = Math.Max(420, 650 - Math.Abs(rpm - 5_500) * 0.035);
                        return new EngineCurveBin(
                            rpm, 24,
                            torque * rpm * (2 * Math.PI / 60) * powerMultiplier,
                            torque * powerMultiplier,
                            13.5,
                            3,
                            confidence);
                    })
                    .ToArray(),
                [
                    new GearModel(4, 136 * powerMultiplier, 48, confidence),
                    new GearModel(5, 106 * powerMultiplier, 48, confidence),
                    new GearModel(6, 86 * powerMultiplier, 48, confidence)
                ],
                [
                    new ShiftTarget(4, 5, 7_400, 7_050, 5_770, confidence, false),
                    new ShiftTarget(5, 6, 7_250, 6_900, 5_885, confidence, false)
                ],
                new Dictionary<string, int>(),
                "示例调校已就绪。")
            {
                AcceptedSamples = 480,
                ReadyBins = 10,
                RequiredBins = 10,
                ReadyGears = 3,
                Guidance = "这是隔离 Demo 数据，仅用于检查车辆配置管理界面。"
            };

        await store.SaveShiftLearningAsync(
            Snapshot(streetFingerprint, 0.91, 1),
            CancellationToken.None);
        await store.SaveShiftLearningAsync(
            Snapshot(raceFingerprint, 0.87, 1.06),
            CancellationToken.None);

        var streetId = VehicleProfileIdentity.Create(streetFingerprint);
        var raceId = VehicleProfileIdentity.Create(raceFingerprint);
        store.RenameVehicleProfile(streetId, "公路调校（Demo）");
        store.RenameVehicleProfile(raceId, "赛事调校（Demo）");
        store.SetShiftRecommendationsEnabled(raceId, false);
    }

    private sealed record ModuleContext(
        ITelemetryFeed Telemetry,
        IHudHost Hud,
        IModuleSettingsStore Settings,
        IAnalysisStore AnalysisStore,
        Action<string> Log) : IModuleContext;
}

internal static class BuiltInModuleCatalog
{
    public static IReadOnlyList<ILazyForzaModule> Create(
        LazyForzaStore store,
        TelemetrySourceKind sourceKind,
        Func<OverlayLayout>? getOverlayLayout = null,
        Action<DiagnosticSignal>? diagnosticSink = null) =>
    [
        new DashboardModule(),
        new LapAnalysisModule(store, sourceKind, getOverlayLayout, diagnosticSink)
    ];
}
