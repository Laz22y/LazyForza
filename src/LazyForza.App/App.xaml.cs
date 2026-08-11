using System.Text.Json;
using System.IO;
using System.Windows;
using System.ComponentModel;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Modules.EstateRace;
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
    private DriftDashboardActivationController? moduleActivation;
    private TelemetryRecorderController? recorder;
    private RollingLog? log;
    private ApplicationUpdateManager? updateManager;
    private DiagnosticCaptureService? diagnosticCapture;
    private SingleInstanceCoordinator? singleInstance;
    private TrayIconService? trayIcon;
    private bool exitRequested;
    private bool minimizedNoticeShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleInstance = SingleInstanceCoordinator.TryAcquire(Dispatcher, ShowMainWindow);
        if (singleInstance is null)
        {
            Shutdown();
            return;
        }
        try
        {
            var directories = new DataDirectoryService(DataRoot(e.Args));
            directories.EnsureCreated();
            var replayPath = ReplayPath(e.Args);
            var captureDirectory = CaptureDirectory(e.Args);
            var captureDriftQa = e.Args.Contains(
                "--capture-drift-qa",
                StringComparer.OrdinalIgnoreCase);
            var captureDriftOnlyQa = e.Args.Contains(
                "--capture-drift-only-qa",
                StringComparer.OrdinalIgnoreCase);
            var captureEstateQa = e.Args.Contains(
                "--capture-estate-qa",
                StringComparer.OrdinalIgnoreCase);
            var captureEstateRaceQa = e.Args.Contains(
                "--capture-estate-race-qa",
                StringComparer.OrdinalIgnoreCase);
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
            moduleActivation = new DriftDashboardActivationController(
                moduleManager,
                store);
            await moduleActivation.InitializeAsync(
                captureDirectory is not null,
                captureDriftQa,
                captureDriftOnlyQa,
                CancellationToken.None);

            var lapAnalysis = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
            recorder = new TelemetryRecorderController(
                telemetry,
                directories,
                store,
                source.Kind,
                lapAnalysis,
                message => log.Write(message));
            await recorder.InitializeAsync(CancellationToken.None);
            MainWindow = new MainWindow(
                moduleManager,
                telemetry,
                overlay,
                store,
                directories,
                recorder,
                source.Kind,
                updateManager,
                diagnosticCapture,
                moduleActivation);
            MainWindow.Closing += OnMainWindowClosing;
            MainWindow.Show();
            trayIcon = new TrayIconService(
                SourceModeText(source.Kind),
                $"{listenAddress}:{port}",
                ShowMainWindow,
                () => ExitApplication());
            if (captureDirectory is null && recordSeconds is null)
                _ = ((MainWindow)MainWindow).CheckForUpdatesOnStartupAsync();
            if (captureDirectory is not null)
            {
                _ = CaptureQaAsync(
                    captureDirectory,
                    captureDriftQa,
                    captureDriftOnlyQa,
                    captureEstateQa,
                    captureEstateRaceQa);
            }
            else if (recordSeconds is not null) _ = AutoRecordAndExitAsync(recordSeconds.Value);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"LazyForza could not start.\n\n{exception}", "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication(-1);
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
        trayIcon?.Dispose();
        singleInstance?.Dispose();
        updateManager?.Dispose();
        store?.Dispose();
        log?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        exitRequested = true;
        base.OnSessionEnding(e);
    }

    internal static void RequestExit() =>
        ((App)Current).ExitApplication();

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (exitRequested) return;
        e.Cancel = true;
        MainWindow.Hide();
        if (!minimizedNoticeShown)
        {
            trayIcon?.ShowMinimizedNotice();
            minimizedNoticeShown = true;
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null) return;
        if (!MainWindow.IsVisible) MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    private void ExitApplication(int exitCode = 0)
    {
        if (exitRequested) return;
        exitRequested = true;
        Shutdown(exitCode);
    }

    private static string SourceModeText(TelemetrySourceKind kind) => kind switch
    {
        TelemetrySourceKind.Live => "Live",
        TelemetrySourceKind.Replay => "Replay",
        TelemetrySourceKind.Simulator => "Simulator",
        _ => kind.ToString()
    };

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
        => ApplicationVersionInfo.Display;

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
            ExitApplication();
        }
    }

    private async Task CaptureQaAsync(
        string directory,
        bool captureDriftQa,
        bool captureDriftOnlyQa,
        bool captureEstateQa,
        bool captureEstateRaceQa)
    {
        try
        {
            await Task.Delay(1800);
            if (captureEstateQa)
            {
                await ((MainWindow)MainWindow).CaptureEstateQaAsync(directory);
                return;
            }
            if (captureEstateRaceQa)
                await ((MainWindow)MainWindow).CaptureEstateRacePageQaAsync(directory);
            var original = overlay!.CurrentLayout;
            foreach (var size in new[] { (Width: 1280d, Height: 720d), (Width: 1920d, Height: 1080d), (Width: 2560d, Height: 1440d) })
            {
                var captureLayout = original with
                {
                    Left = 0,
                    Top = captureDriftQa ? size.Height * 0.22 : 0,
                    Width = size.Width,
                    Height = size.Height,
                    Scale = captureDriftQa ? 0.50 : 1,
                    LapHudLeft = 0,
                    LapHudTop = 0,
                    LapHudScale = 1,
                    LapHudAttachedToDashboard = false,
                    DriftHudLeft = captureDriftQa
                        ? size.Width * 0.50
                        : 0,
                    DriftHudTop = captureDriftQa
                        ? size.Height * 0.10
                        : 0,
                    DriftHudScale = captureDriftQa
                        ? 0.50
                        : captureDriftOnlyQa
                            ? 1
                            : original.DriftHudScale,
                    EstateRaceWidgets = captureEstateRaceQa
                        ? EstateRaceHudLayoutSettings.Default
                        : original.EstateRaceWidgets,
                    EstateRaceHudLeft = captureEstateRaceQa ? 0 : original.EstateRaceHudLeft,
                    EstateRaceHudTop = captureEstateRaceQa ? 0 : original.EstateRaceHudTop,
                    EstateRaceHudWidth = captureEstateRaceQa ? size.Width : original.EstateRaceHudWidth,
                    EstateRaceHudHeight = captureEstateRaceQa ? size.Height : original.EstateRaceHudHeight
                };
                await overlay.SetLayoutAsync(
                    captureLayout,
                    CancellationToken.None);
                await Task.Delay(220);
                await overlay.CapturePngAsync(
                    Path.Combine(
                        directory,
                        $"hud-{size.Width:0}x{size.Height:0}-demo.png"),
                    CancellationToken.None,
                    previewDrift: captureDriftQa || captureDriftOnlyQa,
                    previewEstateRace: captureEstateRaceQa);
            }
            await overlay.SetLayoutAsync(original, CancellationToken.None);
        }
        finally
        {
            ExitApplication();
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
        Action<DiagnosticSignal>? diagnosticSink = null)
    {
        var estate = new EstateCircuitModule(store, sourceKind, getOverlayLayout);
        var estatePackages = new EstateTrackPackageService(
            store,
            typeof(BuiltInModuleCatalog).Assembly.GetName().Version?.ToString(3) ?? "development");
        Guid? identityTrackId = null;
        EstateTrackPackageIdentity? identity = null;
        var estateRace = new EstateRaceModule(() =>
        {
            var track = estate.ActiveTrack;
            var definition = estate.ActiveDefinition;
            if (track is null || definition is null) return null;
            var state = estate.State;
            var completed = estate.LastCompletedLap;
            if (identityTrackId != track.Id)
            {
                identity = estatePackages.Identify(track.Id);
                identityTrackId = track.Id;
            }
            return new EstateRaceTrackContext(
                track,
                definition,
                state.CurrentLapSeconds,
                state.CompletedLaps,
                estate.ActiveCurrentSector,
                state.IsTimingActive,
                completed is null
                    ? null
                    : new EstateCompletedLapEvent(
                        completed.EventId,
                        completed.LapNumber,
                        completed.LapSeconds,
                        completed.SectorSeconds,
                        completed.IsValid,
                        completed.InvalidReason,
                        completed.IsBestLapEligible),
                estate.ActiveSectorCount,
                identity?.PayloadSha256,
                estate.ActiveSectors);
        }, (trackId, enabled, invalidateLapOnDriverIntervention) =>
        {
            if (enabled)
            {
                if (!estate.State.IsTimingActive)
                    estate.StartTiming(trackId, invalidateLapOnDriverIntervention);
                else
                    estate.SetEstateRaceInterventionInvalidation(invalidateLapOnDriverIntervention);
            }
            else
                estate.PauseTimingForEstateRace();
        });
        return
        [
            new DashboardModule(),
            new LapAnalysisModule(store, sourceKind, getOverlayLayout, diagnosticSink),
            estate,
            estateRace,
            new DriftDashboardModule()
        ];
    }
}
