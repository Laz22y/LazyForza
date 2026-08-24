using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text.Json;
using System.IO;
using System.Windows.Data;
using Microsoft.Win32;
using VectorPath = System.Windows.Shapes.Path;
using VectorShape = System.Windows.Shapes.Shape;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.App;

internal sealed partial class MainWindow : Window
{
    private const string ShowDiagnosticsNavigationSetting = "ui.showDiagnosticsNavigation";
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI");
    private static readonly (string IconData, string Key, string Title)[] PrimaryPages =
    [
        ("M 4 18 A 8 8 0 0 1 20 18 M 7 18 A 5 5 0 0 1 17 18 M 12 18 L 16.5 12.5 M 4 18 L 20 18", "nav.overview", "概览"),
        ("M 12 3 L 21 8 L 12 13 L 3 8 Z M 5 12 L 12 16 L 19 12 M 5 16 L 12 20 L 19 16", "nav.modules", "模块"),
        ("M 5 21 L 5 4 M 6 5 C 9 3.5 11 6.5 14 5 C 16.5 3.8 18.5 4.5 20 5.5 L 20 13 C 18 12 16.5 11.8 14.5 13 C 11.5 14.5 9 11.5 6 13 Z M 10 4.8 L 10 12.7 M 15 4.8 L 15 12.8 M 6 8.8 C 9 7.3 11.5 10.3 14.5 8.8 C 16.5 7.8 18 8.1 20 9", "nav.competition", "当前比赛"),
        ("M 4 18 L 4 10 L 8 10 L 8 6 L 12 6 L 12 3 M 12 6 L 16 6 L 16 10 L 20 10 L 20 18 M 3 18 L 21 18 M 7 21 L 17 21", "nav.estateRace", "地产赛事"),
        ("M 12 6 A 7 7 0 1 1 12 20 A 7 7 0 1 1 12 6 M 9 3 L 15 3 M 12 3 L 12 6 M 12 10 L 12 13 L 16 15", "nav.lapAnalysis", "圈速分析"),
        ("M 5 5 L 5 19 L 17 12 Z M 19 5 L 19 19", "nav.replay", "回放工作台"),
        ("M 11 3 C 16 2 20 5 21 9 C 22 13 20 18 16 20 C 12 22 6 21 4 17 C 2 13 3 8 6 5 C 8 3 9 3 11 3 Z M 11 7 C 8 7 7 9 7 12 C 7 15 9 17 12 17 C 15 17 17 15 17 12 C 17 9 15 7 11 7 Z M 16 16 L 18.5 18 M 17.2 14.8 L 19.7 16.8", "nav.tracks", "赛道"),
        ("M 4 16 L 5.5 11 L 8 8 L 16 8 L 18.5 11 L 20 16 L 20 19 L 18 19 L 18 17 L 6 17 L 6 19 L 4 19 Z M 6 12 L 18 12 M 8 15 L 8.01 15 M 16 15 L 16.01 15", "nav.vehicles", "车辆与换挡"),
        ("M 4 7 L 9 7 M 15 7 L 20 7 M 12 4 L 12 10 M 4 17 L 13 17 M 19 17 L 20 17 M 16 14 L 16 20", "nav.settings", "设置")
    ];
    private static readonly (string IconData, string Key, string Title) DiagnosticsPageEntry =
        ("M 3 12 L 7 12 L 9 7 L 13 17 L 16 10 L 18 12 L 21 12 M 4 4 L 20 4 L 20 20 L 4 20 Z", "nav.diagnostics", "诊断");
    private readonly ModuleManager moduleManager;
    private readonly ITelemetryFeed telemetry;
    private readonly OverlayCoordinator overlay;
    private readonly LazyForzaStore store;
    private readonly DataDirectoryService directories;
    private readonly TelemetryRecorderController recorder;
    private readonly TelemetrySourceKind sourceKind;
    private readonly ApplicationUpdateManager updateManager;
    private readonly DiagnosticCaptureService diagnosticCapture;
    private readonly DriftDashboardActivationController moduleActivation;
    private readonly StartupProfileStore startupProfileStore;
    private readonly MainWindowPageRefreshState pageRefresh = new();
    private readonly ContentControl content = new();
    private readonly ListBox navigation = new();
    private readonly DispatcherTimer refreshTimer;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<Guid> selectedLapIds = [];
    private readonly HashSet<Guid> displayedLapIds = [];
    private readonly Dictionary<Guid, HashSet<int>> selectedLapPerformanceClasses = [];
    private readonly HashSet<Guid> customizedLapPerformanceFilters = [];
    private readonly Dictionary<Guid, TrackTemplate> trackPreviewCache = [];
    private string? pendingReplayRecordingPath;
    private Action? refreshVisiblePage;
    private bool changingModule;
    private bool showDiagnosticsNavigation;
    private SettingsCategory selectedSettingsCategory;
    private bool hudAppearanceExpanded = true;
    private bool hudComponentsExpanded;
    private bool hudOpacityExpanded;
    private bool hudTimingExpanded;
    private string CurrentTrackSource => TelemetryDataPartition.TrackSource(sourceKind);

    private enum SettingsCategory
    {
        General,
        Telemetry,
        Hud,
        Maintenance
    }

    public MainWindow(
        ModuleManager moduleManager,
        ITelemetryFeed telemetry,
        OverlayCoordinator overlay,
        LazyForzaStore store,
        DataDirectoryService directories,
        TelemetryRecorderController recorder,
        TelemetrySourceKind sourceKind,
        ApplicationUpdateManager updateManager,
        DiagnosticCaptureService diagnosticCapture,
        DriftDashboardActivationController moduleActivation,
        StartupProfileStore startupProfileStore)
    {
        this.moduleManager = moduleManager;
        this.telemetry = telemetry;
        this.overlay = overlay;
        this.store = store;
        this.directories = directories;
        this.recorder = recorder;
        this.sourceKind = sourceKind;
        this.updateManager = updateManager;
        this.diagnosticCapture = diagnosticCapture;
        this.moduleActivation = moduleActivation;
        this.startupProfileStore = startupProfileStore;
        showDiagnosticsNavigation = bool.TryParse(
            store.GetAppSetting(ShowDiagnosticsNavigationSetting),
            out var showDiagnostics) && showDiagnostics;
        Title = $"LazyForza {ApplicationVersionInfo.Display}";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LazyForza.png", UriKind.Absolute));
        Width = 1280;
        Height = 800;
        MinWidth = 960;
        MinHeight = 640;
        Background = Brush("WindowBrush");
        Foreground = Brush("TextBrush");
        FontFamily = UiFont;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Content = BuildShell();
        navigation.SelectionChanged += (_, _) => RenderSelectedPage();
        navigation.SelectedIndex = 0;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            var lapModule = moduleManager.Modules.OfType<LapAnalysisModule>().FirstOrDefault();
            if (lapModule is not null) diagnosticCapture.UpdateTrackMatch(lapModule.MatchDiagnostics);
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            refreshVisiblePage?.Invoke();
        }, Dispatcher);
        refreshTimer.Start();
        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            lifetimeCancellation.Cancel();
            lifetimeCancellation.Dispose();
        };
    }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (!updateManager.CheckOnStartup) return;
        try
        {
            await Task.Delay(650, lifetimeCancellation.Token);
            var release = await updateManager.CheckAsync(lifetimeCancellation.Token);
            if (release is not null) await OfferUpdateAsync(release);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            updateManager.ReportFailure("Startup update check failed", exception);
        }
    }

    internal async Task CaptureEstateQaAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        EnsureEstateQaTrack();
        WindowState = WindowState.Normal;
        Width = 1440;
        Height = 900;
        navigation.SelectedIndex = 6;
        UpdateLayout();
        if (content.Content is ScrollViewer trackPageScroll) trackPageScroll.ScrollToHome();
        await Task.Delay(250);
        CaptureVisual(this, Path.Combine(directory, "estate-tracks-1440x900.png"));

        var lapModule = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
        lapModule.ResetTrackLearning();
        RenderSelectedPage(true);
        UpdateLayout();
        if (content.Content is ScrollViewer waitingTrackPageScroll) waitingTrackPageScroll.ScrollToHome();
        await Task.Delay(200);
        CaptureVisual(this, Path.Combine(directory, "track-learning-ready-1440x900.png"));
        lapModule.CancelTrackLearning();
        RenderSelectedPage(true);

        var module = moduleManager.Modules.OfType<EstateCircuitModule>().Single();
        var enrollment = new EstateCircuitEnrollmentWindow(
            module,
            new EstateEnrollmentDraftStore(directories.Root)) { Owner = this };
        enrollment.Show();
        enrollment.UpdateLayout();
        await Task.Delay(250);
        CaptureVisual(enrollment, Path.Combine(directory, "estate-enrollment-1060x780.png"));
        enrollment.Close();

        var qaTrack = store.ListTracks()
            .First(summary => summary.TimingKind == TrackTimingKind.EstateGeometry);
        var loaded = store.LoadTrack(qaTrack.Id)!.Value;
        var definition = store.LoadEstateTrackDefinition(qaTrack.Id)!;
        var editor = new EstateTrackEditorWindow(store, module, loaded.Track, loaded.Sectors, definition) { Owner = this };
        editor.Show();
        editor.UpdateLayout();
        await Task.Delay(200);
        CaptureVisual(editor, Path.Combine(directory, "estate-editor-1040x720.png"));
        editor.Close();

        var startFinish = new EstateStartFinishRevisionWindow(
            module, loaded.Track, definition, store.CountLaps(loaded.Track.Id)) { Owner = this };
        startFinish.Show();
        startFinish.UpdateLayout();
        await Task.Delay(150);
        CaptureVisual(startFinish, Path.Combine(directory, "estate-start-finish-editor-900x660.png"));
        module.CancelEnrollment();
        startFinish.Close();

        var pitEntry = new EstatePitEnrollmentWindow(
            module, loaded.Track, definition, EstatePitEditScope.EntryGate) { Owner = this };
        pitEntry.Show();
        pitEntry.UpdateLayout();
        await Task.Delay(300);
        CaptureVisual(pitEntry, Path.Combine(directory, "estate-pit-entry-editor-940x780.png"));
        pitEntry.Close();

        var pitSettings = new EstatePitEnrollmentWindow(
            module, loaded.Track, definition, EstatePitEditScope.Settings) { Owner = this };
        pitSettings.Show();
        pitSettings.UpdateLayout();
        await Task.Delay(300);
        CaptureVisual(pitSettings, Path.Combine(directory, "estate-pit-settings-editor-940x780.png"));
        pitSettings.Close();
    }

    internal async Task CaptureEstateRacePageQaAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var englishHanAudit = new SortedSet<string>(StringComparer.Ordinal);
        EnsureEstateQaTrack();
        WindowState = WindowState.Normal;
        Width = 1440;
        Height = 900;
        navigation.SelectedIndex = 3;
        UpdateLayout();
        if (content.Content is ScrollViewer scroll) scroll.ScrollToHome();
        await Task.Delay(300);
        CaptureVisual(this, Path.Combine(directory, "estate-race-page-1440x900.png"));

        var summary = store.ListTracks(CurrentTrackSource)
            .First(track => track.TimingKind == TrackTimingKind.EstateGeometry);
        var loaded = store.LoadTrack(summary.Id);
        var definition = store.LoadEstateTrackDefinition(summary.Id);
        if (loaded is null || definition is null) return;
        var estate = moduleManager.Modules.OfType<EstateCircuitModule>().Single();
        var pit = new EstatePitEnrollmentWindow(estate, loaded.Value.Track, definition) { Owner = this };
        pit.Show();
        pit.UpdateLayout();
        await Task.Delay(250);
        CaptureVisual(pit, Path.Combine(directory, "estate-pit-enrollment-940x780.png"));
        pit.Close();

        navigation.SelectedIndex = 1;
        UpdateLayout();
        await Task.Delay(200);
        CaptureVisual(this, Path.Combine(directory, "modules-without-estate-1440x900.png"));

        navigation.SelectedIndex = 4;
        UpdateLayout();
        if (content.Content is ScrollViewer lapAnalysisScroll)
            lapAnalysisScroll.ScrollToHome();
        await Task.Delay(200);
        CaptureVisual(this, Path.Combine(directory, "lap-analysis-exchange-1440x900.png"));

        navigation.SelectedIndex = 8;
        var settingsCategories = new[]
        {
            (SettingsCategory.General, "general"),
            (SettingsCategory.Telemetry, "telemetry"),
            (SettingsCategory.Hud, "hud"),
            (SettingsCategory.Maintenance, "maintenance")
        };
        foreach (var (width, height) in new[] { (1440d, 900d), (960d, 640d) })
        {
            Width = width;
            Height = height;
            foreach (var (category, fileName) in settingsCategories)
            {
                selectedSettingsCategory = category;
                RenderSelectedPage();
                UpdateLayout();
                if (content.Content is ScrollViewer settingsScroll)
                    settingsScroll.ScrollToHome();
                await Task.Delay(200);
                CaptureVisual(
                    this,
                    Path.Combine(directory, $"settings-{fileName}-{width:0}x{height:0}.png"));
                if (category == SettingsCategory.Hud && content.Content is ScrollViewer hudSettingsScroll)
                {
                    hudSettingsScroll.ScrollToEnd();
                    UpdateLayout();
                    await Task.Delay(150);
                    CaptureVisual(
                        this,
                        Path.Combine(directory, $"settings-hud-footer-{width:0}x{height:0}.png"));
                }
                CollectHanText(this, $"settings {category}", englishHanAudit);
            }
        }
        Width = 1440;
        Height = 900;

        for (var pageIndex = 0; pageIndex < PrimaryPages.Length; pageIndex++)
        {
            navigation.SelectedIndex = pageIndex;
            UpdateLayout();
            if (content.Content is ScrollViewer pageScroll)
                pageScroll.ScrollToHome();
            await Task.Delay(180);
            CaptureVisual(this, Path.Combine(directory, $"main-page-{pageIndex + 1}-1440x900.png"));
            CollectHanText(this, $"page {pageIndex + 1}", englishHanAudit);
        }
        File.WriteAllLines(Path.Combine(directory, "english-han-audit.txt"), englishHanAudit);
    }

    private static void CollectHanText(DependencyObject node, string scope, ISet<string> output)
    {
        static bool ContainsHan(string value) => value.Any(character => character is >= '\u3400' and <= '\u9fff');

        if (node is TextBlock { Text: { Length: > 0 } text } && ContainsHan(text))
            output.Add($"[{scope}] {text.Replace('\r', ' ').Replace('\n', ' ')}");
        if (node is ContentControl { Content: string content } && ContainsHan(content))
            output.Add($"[{scope}] {content.Replace('\r', ' ').Replace('\n', ' ')}");
        if (node is HeaderedContentControl { Header: string header } && ContainsHan(header))
            output.Add($"[{scope}] {header.Replace('\r', ' ').Replace('\n', ' ')}");
        if (node is FrameworkElement { ToolTip: string toolTip } && ContainsHan(toolTip))
            output.Add($"[{scope}] {toolTip.Replace('\r', ' ').Replace('\n', ' ')}");
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            CollectHanText(VisualTreeHelper.GetChild(node, index), scope, output);
    }

    private void EnsureEstateQaTrack()
    {
        if (store.ListTracks(CurrentTrackSource).Any(track => track.TimingKind == TrackTimingKind.EstateGeometry))
            return;

        var route = Enumerable.Range(0, 181)
            .Select(index =>
            {
                var angle = index * Math.PI * 2 / 180;
                return new TrackPoint(100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 0, 0, 0);
            })
            .ToArray();
        var track = TrackAlgorithms.BuildTemplate("英国塔地产环道", route) with
        {
            Source = CurrentTrackSource,
            TimingKind = TrackTimingKind.EstateGeometry,
            Category = "地产环道",
            CaptureLapCount = 2,
            Confidence = 0.99
        };
        var gate = new EstateTimingGate(
            new EstateGatePoint(88, 2, 0),
            new EstateGatePoint(112, 2, 0),
            0,
            1,
            0.06,
            0.04,
            0.12);
        var definition = new EstateTrackDefinition(
            track.Id,
            "英国塔地产环道",
            "LazyForza QA",
            "QA-ESTATE-001",
            "3",
            gate,
            EstateTrackAlgorithms.CreateCheckpoints(track, 8),
            new EstatePitDefinition(
                new EstateTimingGate(
                    new EstateGatePoint(114, 2, -24),
                    new EstateGatePoint(122, 2, -24),
                    0, 1, 0.04, 0.02, 0.08),
                new EstateTimingGate(
                    new EstateGatePoint(114, 2, 24),
                    new EstateGatePoint(122, 2, 24),
                    0, 1, 0.04, 0.02, 0.08),
                [
                    new EstateGatePoint(118, 2, -26),
                    new EstateGatePoint(118, 2, -12),
                    new EstateGatePoint(118, 2, 0),
                    new EstateGatePoint(118, 2, 12),
                    new EstateGatePoint(118, 2, 26)
                ],
                new EstateGatePoint(118, 2, 9),
                3,
                80,
                3,
                4,
                [
                    new EstateGatePoint(114.5, 2, 5),
                    new EstateGatePoint(121.5, 2, 5),
                    new EstateGatePoint(121.5, 2, 13),
                    new EstateGatePoint(114.5, 2, 13)
                ],
                new EstateTimingGate(
                    new EstateGatePoint(114, 2, 0),
                    new EstateGatePoint(122, 2, 0),
                    0, 1, 0.04, 0.02, 0.08)),
            72.314,
            72.502,
            0.99,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        store.SaveTrack(track, TrackAlgorithms.CreateSectors(track, requestedCount: 7), definition);
    }

    private static void CaptureVisual(FrameworkElement visual, string path)
    {
        InvalidateVisualTree(visual);
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void InvalidateVisualTree(DependencyObject parent)
    {
        if (parent is UIElement element) element.InvalidateVisual();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            InvalidateVisualTree(VisualTreeHelper.GetChild(parent, index));
    }

    private UIElement BuildShell()
    {
        var root = new Grid { Background = Brush("WindowBrush") };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = new Border { Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideStack = new DockPanel { Margin = new Thickness(14, 18, 14, 14) };
        var brand = new StackPanel { Margin = new Thickness(8, 0, 8, 22) };
        brand.Children.Add(new Image
        {
            Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LazyForzaWordmark.png", UriKind.Absolute)),
            Width = 200,
            Height = 35,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true
        });
        var sourceLabel = Label(sourceKind == TelemetrySourceKind.Live ? $"LIVE UDP · {ConfiguredLiveEndpoint()}" : "模拟 / 回放", 12, FontWeights.Normal, "AccentBrush");
        sourceLabel.Margin = new Thickness(0, 7, 0, 0);
        brand.Children.Add(sourceLabel);
        DockPanel.SetDock(brand, Dock.Top);
        sideStack.Children.Add(brand);
        PopulateNavigation();
        sideStack.Children.Add(navigation);
        sidebar.Child = sideStack;
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);

        var main = new Grid { Margin = new Thickness(30, 24, 30, 26), Background = Brush("WindowBrush") };
        content.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        content.VerticalContentAlignment = VerticalAlignment.Stretch;
        main.Children.Add(content);
        var sourceChip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(36, 49, 60)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = Label(sourceKind == TelemetrySourceKind.Live ? "LIVE" : "模拟 / 回放", 12, FontWeights.SemiBold)
        };
        Panel.SetZIndex(sourceChip, 1);
        main.Children.Add(sourceChip);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        return root;
    }

    private void PopulateNavigation()
    {
        navigation.Items.Clear();
        foreach (var page in PrimaryPages)
            navigation.Items.Add(NavigationEntry(
                page.IconData,
                AppLocalization.Text(page.Key, page.Title)));
        if (showDiagnosticsNavigation)
            navigation.Items.Add(NavigationEntry(
                DiagnosticsPageEntry.IconData,
                AppLocalization.Text(DiagnosticsPageEntry.Key, DiagnosticsPageEntry.Title)));
    }

    private void RenderSelectedPage(bool preserveScroll = false)
    {
        if (changingModule || navigation.SelectedIndex < 0) return;
        var previousOffset = preserveScroll && content.Content is ScrollViewer currentScroll ? currentScroll.VerticalOffset : 0;
        refreshVisiblePage = null;
        var page = navigation.SelectedIndex switch
        {
            0 => OverviewPage(),
            1 => ModulesPage(),
            2 => CurrentCompetitionPage(),
            3 => EstateRacePage(),
            4 => LapAnalysisPage(),
            5 => ReplayWorkbenchPage(),
            6 => TracksPage(),
            7 => ShiftPage(),
            8 => SettingsPage(),
            _ => DiagnosticsPage()
        };
        AppLocalization.ApplyTo(page);
        content.Content = page;
        if (preserveScroll && page is ScrollViewer newScroll)
        {
            Dispatcher.BeginInvoke(() => newScroll.ScrollToVerticalOffset(previousOffset), DispatcherPriority.Loaded);
        }
    }

    private UIElement CurrentCompetitionPage()
    {
        var module = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
        var showingRecent = module.IsShowingRecentCompetition;
        var stack = PageStack("当前比赛", showingRecent
            ? "上一场比赛结果将在结束后保留 5 分钟。"
            : "查看本场分段与已完成圈；暂停、菜单或倒转不会结束比赛。");
        if (module.Snapshot is not LapHudState)
        {
            stack.Children.Add(EmptyCard("圈速分析未启用", "请先在“模块”中启用。"));
            refreshVisiblePage = () =>
            {
                if (module.Snapshot is LapHudState) RenderSelectedPage(true);
            };
            return Scroll(stack);
        }

        var hud = module.CompetitionPageSnapshot;
        if (!module.HasCompetitionPageContent || hud is null)
        {
            stack.Children.Add(EmptyCard("暂无比赛", "进入赛事后自动显示；上一场结果保留 5 分钟。"));
            refreshVisiblePage = () =>
            {
                if (module.HasCompetitionPageContent && module.CompetitionPageSnapshot is not null) RenderSelectedPage(true);
            };
            return Scroll(stack);
        }

        var sessionId = module.CurrentSessionId;
        var pointToPointTimingApproximate = module.CurrentTrack?.LayoutKind == TrackLayoutKind.PointToPoint;
        if (pointToPointTimingApproximate) stack.Children.Add(PointToPointTimingNotice());
        var sessionLaps = module.CurrentSessionLaps;
        var competitionClass = module.CurrentCompetitionPerformanceClass ?? sessionLaps.LastOrDefault()?.Vehicle.CarClass;
        var competitionPi = module.CurrentCompetitionPerformanceIndex ?? sessionLaps.LastOrDefault()?.Vehicle.PerformanceIndex;
        var sessionBestByClass = sessionLaps
            .Where(lap => lap.IsValid)
            .GroupBy(lap => lap.Vehicle.CarClass)
            .ToDictionary(group => group.Key, group => group.Min(lap => lap.TotalSeconds));
        var historicalBestByClass = module.VisibleLaps
            .Where(lap => lap.IsValid)
            .GroupBy(lap => lap.Vehicle.CarClass)
            .ToDictionary(group => group.Key, group => group.Min(lap => lap.TotalSeconds));
        var sessionBest = competitionClass is int classCode && sessionBestByClass.TryGetValue(classCode, out var classSessionBest)
            ? classSessionBest
            : (double?)null;
        var historicalBest = competitionClass is int historyClassCode && historicalBestByClass.TryGetValue(historyClassCode, out var classHistoricalBest)
            ? classHistoricalBest
            : (double?)null;
        var summary = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 14) };
        summary.Children.Add(MetricCard("赛道", Label(module.CurrentTrack?.Name ?? hud.TrackName, 18, FontWeights.SemiBold), Label("当前识别", 11, FontWeights.Normal, "MutedBrush")));
        summary.Children.Add(MetricCard("性能等级 / PI", Label(competitionClass is int value ? $"{PerformanceClassName(value)}  {competitionPi?.ToString() ?? "—"}" : "—", 22, FontWeights.SemiBold), Label("按性能等级分别比较", 11, FontWeights.Normal, "MutedBrush")));
        summary.Children.Add(MetricCard(showingRecent ? "上场最快" : "本场最快", Label(AnalysisTime(sessionBest, pointToPointTimingApproximate), 22, FontWeights.SemiBold, "SuccessBrush"), Label(AppLocalization.Format("competition.completedLaps", "已完成 {0} 圈", sessionLaps.Count), 11, FontWeights.Normal, "MutedBrush")));
        summary.Children.Add(MetricCard("同等级历史最快", Label(AnalysisTime(historicalBest, pointToPointTimingApproximate), 22, FontWeights.SemiBold, "PurpleBrush"), Label("本机保存记录", 11, FontWeights.Normal, "MutedBrush")));
        stack.Children.Add(summary);

        var liveTable = new Grid { Margin = new Thickness(4) };
        foreach (var width in new[] { 0.6, 1.1, 1.1, 1.1, 1.0 }) liveTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        var liveRows = new List<TextBlock[]>();
        if (showingRecent)
        {
            var expiresAt = module.RecentCompetitionExpiresAt?.ToLocalTime();
            stack.Children.Add(EmptyCard("上一场比赛",
                $"结果保留至 {expiresAt:HH:mm:ss}；新比赛开始后自动切换。"));
        }
        else
        {
            if (!module.IsCompetitionActive)
                stack.Children.Add(EmptyCard("比赛已暂停", "返回驾驶后继续记录当前比赛。"));

            AddLiveRow(["段", "当前圈", "本场最快", "历史最快", "状态"], 0, true);
            for (var index = 0; index < hud.Sectors.Count; index++)
            {
                var sector = hud.Sectors[index];
                liveRows.Add(AddLiveRow([
                    (sector.Index + 1).ToString(), AnalysisTime(sector.CurrentSeconds, pointToPointTimingApproximate), AnalysisTime(sector.CurrentCompetitionBestSeconds, pointToPointTimingApproximate),
                    AnalysisTime(sector.HistoricalBestSeconds, pointToPointTimingApproximate), SectorStateText(sector.State)
                ], index + 1, false));
            }
            var liveStack = new StackPanel();
            liveStack.Children.Add(Label("当前圈分段", 16, FontWeights.SemiBold));
            liveStack.Children.Add(liveTable);
            stack.Children.Add(Card(liveStack));
        }

        if (sessionLaps.Count == 0)
        {
            stack.Children.Add(EmptyCard("还没有完成圈", "完成一圈后显示圈速与分段。"));
        }
        else
        {
            var lapTable = new Grid { Margin = new Thickness(4) };
            foreach (var width in new[] { 0.4, 0.75, 1.05, 0.9, 0.9, 0.75, 2.35 }) lapTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            AddCompetitionLapRow(["圈", "等级 / PI", "完成时间", "圈速", "对本场", "状态", "分段"], 0, true, null, null);
            var ordered = sessionLaps.OrderBy(lap => lap.StartedAt).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var lap = ordered[index];
                var lapSessionBest = sessionBestByClass.GetValueOrDefault(lap.Vehicle.CarClass);
                var lapHistoricalBest = historicalBestByClass.GetValueOrDefault(lap.Vehicle.CarClass);
                var state = !lap.IsValid ? SectorColorState.Gray :
                    lapHistoricalBest > 0 && Math.Abs(lap.TotalSeconds - lapHistoricalBest) < 0.0005 ? SectorColorState.Purple :
                    lapSessionBest > 0 && Math.Abs(lap.TotalSeconds - lapSessionBest) < 0.0005 ? SectorColorState.Green : SectorColorState.Yellow;
                AddCompetitionLapRow([
                    (index + 1).ToString(), $"{PerformanceClassName(lap.Vehicle.CarClass)} {lap.Vehicle.PerformanceIndex}",
                    lap.StartedAt.ToLocalTime().ToString("HH:mm:ss"), AnalysisTime(lap.TotalSeconds, pointToPointTimingApproximate),
                    lap.IsValid && lapSessionBest > 0 ? $"{lap.TotalSeconds - lapSessionBest:+0.000;-0.000;0.000}" : "—",
                    lap.IsValid ? SectorStateText(state) : "无效",
                    string.Join("  ", lap.Segments.Select(segment => $"S{segment.Index + 1} {AnalysisTime(segment.TimeSeconds, pointToPointTimingApproximate)}"))
                ], index + 1, false, lap, state);
            }
            var lapStack = new StackPanel();
            lapStack.Children.Add(Label(showingRecent ? "上一场已完成圈" : "本场已完成圈", 16, FontWeights.SemiBold));
            lapStack.Children.Add(lapTable);
            stack.Children.Add(Card(lapStack));

            void AddCompetitionLapRow(string[] cells, int row, bool header, LapSummary? lap, SectorColorState? state)
            {
                lapTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var column = 0; column < cells.Length; column++)
                {
                    var brush = header || state is null ? null : SectorStateBrush(state.Value);
                    var text = Label(cells[column], header ? 12 : 11, header ? FontWeights.SemiBold : FontWeights.Normal, brush);
                    text.Margin = new Thickness(7, 6, 7, 6);
                    Grid.SetRow(text, row); Grid.SetColumn(text, column); lapTable.Children.Add(text);
                }
            }
        }

        if (sessionLaps.Count > 0)
        {
            var reviewLaps = competitionClass is int reviewClass
                ? sessionLaps.Where(lap => lap.Vehicle.CarClass == reviewClass).ToArray()
                : sessionLaps;
            stack.Children.Add(BuildCompetitionReviewCard(
                module.CurrentTrack?.Name ?? hud.TrackName,
                competitionClass,
                competitionPi,
                reviewLaps,
                showingRecent,
                pointToPointTimingApproximate));
        }

        var initialLapCount = sessionLaps.Count;
        var initialCompetitionActive = module.IsCompetitionActive;
        refreshVisiblePage = () =>
        {
            if (!module.HasCompetitionPageContent || module.IsShowingRecentCompetition != showingRecent ||
                module.IsCompetitionActive != initialCompetitionActive || module.CurrentSessionId != sessionId ||
                module.CurrentSessionLaps.Count != initialLapCount)
            {
                RenderSelectedPage(true);
                return;
            }

            if (showingRecent) return;
            if (module.CompetitionPageSnapshot is not LapHudState current || current.Sectors.Count != liveRows.Count)
            {
                RenderSelectedPage(true);
                return;
            }
            for (var index = 0; index < current.Sectors.Count; index++)
            {
                var sector = current.Sectors[index];
                var cells = liveRows[index];
                cells[1].Text = AnalysisTime(sector.CurrentSeconds, pointToPointTimingApproximate);
                cells[2].Text = AnalysisTime(sector.CurrentCompetitionBestSeconds, pointToPointTimingApproximate);
                cells[3].Text = AnalysisTime(sector.HistoricalBestSeconds, pointToPointTimingApproximate);
                cells[4].Text = SectorStateText(sector.State);
                cells[4].Foreground = Brush(SectorStateBrush(sector.State));
            }
        };
        refreshVisiblePage();
        return Scroll(stack);

        TextBlock[] AddLiveRow(string[] cells, int row, bool header)
        {
            var controls = new TextBlock[cells.Length];
            liveTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < cells.Length; column++)
            {
                var text = Label(cells[column], header ? 12 : 11, header ? FontWeights.SemiBold : FontWeights.Normal);
                text.Margin = new Thickness(8, 6, 8, 6);
                Grid.SetRow(text, row); Grid.SetColumn(text, column); liveTable.Children.Add(text);
                controls[column] = text;
            }
            return controls;
        }
    }

    private UIElement LapAnalysisPage()
    {
        var stack = PageStack("圈速分析", "选择赛道，对比已保存的圈速、分段和走线。");
        var module = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
        stack.Children.Add(BuildLapAnalysisExchangeCard(module, out var exportSelectedLaps));
        var hud = module.Snapshot as LapHudState;
        if (hud is null)
        {
            stack.Children.Add(EmptyCard("圈速分析未启用", "请先在“模块”中启用。"));
            refreshVisiblePage = () =>
            {
                if (module.Snapshot is LapHudState) RenderSelectedPage(true);
            };
            return Scroll(stack);
        }

        var pointToPointTimingApproximate = module.CurrentTrack?.LayoutKind == TrackLayoutKind.PointToPoint;
        var compatibleTracks = store.ListTracks(CurrentTrackSource)
            .Select(item => (
                Summary: item,
                Loaded: store.LoadTrack(item.Id),
                RecordedLaps: Math.Max(
                    item.Laps,
                    module.CurrentTrack?.Id == item.Id
                        ? module.VisibleLaps.Count(lap => lap.TrackId == item.Id)
                        : 0)))
            .Where(item => item.Loaded is { } loaded && loaded.Sectors.Count > 0 && loaded.Sectors.All(sector =>
                sector.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion &&
                sector.AlgorithmVersion == TrackAlgorithms.SectorAlgorithmVersion))
            .OrderByDescending(item => item.RecordedLaps)
            .ThenBy(item => item.Summary.Name, StringComparer.CurrentCulture)
            .ToArray();
        Button? deleteSelectedLapsButton = null;
        Button? displaySelectedLapsButton = null;
        if (compatibleTracks.Length > 0)
        {
            var selectorGrid = new Grid();
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            var selectorText = new StackPanel();
            selectorText.Children.Add(Label("分析赛道", 16, FontWeights.SemiBold));
            selectorText.Children.Add(Label(module.HasCurrentCompetitionSession
                ? "比赛中会自动识别赛道；手动切换将结束当前分析。"
                : "普通赛事会自动识别；地产环道需要在这里手动选择后查看圈速。", 11, FontWeights.Normal, "MutedBrush"));
            selectorGrid.Children.Add(selectorText);
            var selector = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            var emptySelection = new ComboBoxItem
            {
                Content = "未选择赛道",
                ToolTip = "保持空选；进入比赛后自动识别"
            };
            selector.Items.Add(emptySelection);
            ComboBoxItem? selectedItem = null;
            foreach (var candidate in compatibleTracks)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{candidate.Summary.Name} · {TrackAnalysisKind(candidate.Summary)} · {candidate.RecordedLaps} 圈",
                    Tag = candidate.Summary.Id
                };
                selector.Items.Add(item);
                if (candidate.Summary.Id == module.CurrentTrack?.Id) selectedItem = item;
            }
            selector.SelectedItem = selectedItem ?? emptySelection;
            selector.SelectionChanged += (_, _) =>
            {
                var selectedTrackId = selector.SelectedItem is ComboBoxItem { Tag: Guid selectedId }
                    ? selectedId
                    : (Guid?)null;
                if (selectedTrackId == module.CurrentTrack?.Id ||
                    selectedTrackId is null && module.CurrentTrack is null) return;
                if (module.HasCurrentCompetitionSession && MessageBox.Show(
                        "切换赛道会结束当前比赛的分析。仍要继续吗？",
                        "切换分析赛道", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    selector.SelectedItem = module.CurrentTrack is null
                        ? emptySelection
                        : selector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, module.CurrentTrack.Id));
                    return;
                }
                try
                {
                    if (selectedTrackId is Guid trackId) module.SelectTrack(trackId);
                    else module.ClearTrackSelection();
                    selectedLapIds.Clear();
                    displayedLapIds.Clear();
                    RenderSelectedPage(true);
                }
                catch (InvalidOperationException exception)
                {
                    MessageBox.Show(exception.Message, "无法选择赛道", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            var selectedTrackIdForActions = selector.SelectedItem is ComboBoxItem { Tag: Guid selectedId }
                ? selectedId
                : Guid.Empty;
            LapSummary[] TrackLaps(Guid trackId) => trackId == module.CurrentTrack?.Id
                ? module.VisibleLaps.Where(lap => lap.TrackId == trackId).ToArray()
                : trackId == Guid.Empty
                    ? []
                : store.LoadLapSummaries(trackId, LazyForzaStore.MaxLapsPerTrack).ToArray();

            var selectedTrackLaps = TrackLaps(selectedTrackIdForActions);
            HashSet<int> selectedClasses;
            if (selectedTrackIdForActions == Guid.Empty)
            {
                selectedClasses = [];
            }
            else if (customizedLapPerformanceFilters.Contains(selectedTrackIdForActions) &&
                     selectedLapPerformanceClasses.TryGetValue(selectedTrackIdForActions, out var savedSelectedClasses))
            {
                selectedClasses = savedSelectedClasses;
            }
            else
            {
                selectedClasses = selectedTrackLaps
                    .Select(lap => lap.Vehicle.CarClass)
                    .Where(performanceClass => performanceClass is >= 0 and <= 7)
                    .ToHashSet();
                selectedLapPerformanceClasses[selectedTrackIdForActions] = selectedClasses;
            }
            if (selectedTrackIdForActions != Guid.Empty)
            {
                var classFilter = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                foreach (var performanceClass in Enumerable.Range(0, 8))
                {
                    var selected = selectedClasses.Contains(performanceClass);
                    var classColor = PerformanceClassColor(performanceClass);
                    var chip = new ToggleButton
                    {
                        Content = PerformanceClassName(performanceClass),
                        Style = (Style)Application.Current.Resources["PerformanceClassToggle"],
                        IsChecked = selected,
                        Width = 52,
                        Height = 32,
                        Margin = new Thickness(0, 0, 7, 7),
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(150, 157, 169)),
                        Background = new SolidColorBrush(selected ? classColor : DimmedPerformanceClassColor(classColor)),
                        BorderBrush = new SolidColorBrush(selected ? Colors.White : DimmedPerformanceClassColor(classColor)),
                        BorderThickness = new Thickness(selected ? 2 : 1),
                        ToolTip = selected ? $"点击隐藏 {PerformanceClassName(performanceClass)} 级" : $"点击显示 {PerformanceClassName(performanceClass)} 级"
                    };
                    chip.Click += (_, _) =>
                    {
                        customizedLapPerformanceFilters.Add(selectedTrackIdForActions);
                        if (chip.IsChecked == true) selectedClasses.Add(performanceClass);
                        else selectedClasses.Remove(performanceClass);
                        selectedLapIds.Clear();
                        displayedLapIds.Clear();
                        RenderSelectedPage(true);
                    };
                    classFilter.Children.Add(chip);
                }
                selectorText.Children.Add(Label("性能等级", 11, FontWeights.Normal, "MutedBrush"));
                selectorText.Children.Add(classFilter);
            }

            var deleteTrackLaps = new Button
            {
                Content = "删除赛道记录",
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(58, 28, 35)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(168, 71, 85)),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 190)),
                IsEnabled = selectedTrackLaps.Length > 0,
                ToolTip = "可按筛选等级删除，并选择是否保留历史最快圈"
            };
            deleteTrackLaps.Click += (_, _) =>
            {
                if (selector.SelectedItem is not ComboBoxItem { Tag: Guid selectedTrackId }) return;
                var selectedTrack = compatibleTracks.First(item => item.Summary.Id == selectedTrackId).Summary;
                var laps = TrackLaps(selectedTrackId);
                if (laps.Length == 0)
                {
                    MessageBox.Show("该赛道没有已保存圈速。", "删除赛道圈速", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var confirmation = new LapBulkDeleteDialog(this, selectedTrack.Name, laps, selectedClasses);
                if (confirmation.ShowDialog() != true) return;

                IReadOnlySet<int>? deletionClasses = confirmation.SelectedClassesOnly
                    ? selectedClasses.ToHashSet()
                    : null;
                module.DeleteTrackLaps(selectedTrackId, confirmation.DeleteHistoricalBests, deletionClasses);
                selectedLapIds.Clear();
                displayedLapIds.Clear();
                RenderSelectedPage(true);
            };

            var selectedTrackLapIds = selectedTrackLaps.Select(lap => lap.Id).ToHashSet();
            var deleteSelectedLaps = new Button
            {
                Content = "删除所选圈速",
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(58, 28, 35)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(168, 71, 85)),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 190)),
                IsEnabled = selectedLapIds.Any(selectedTrackLapIds.Contains),
                ToolTip = "删除表格中已勾选的记录"
            };
            deleteSelectedLapsButton = deleteSelectedLaps;
            deleteSelectedLaps.Click += (_, _) =>
            {
                if (selector.SelectedItem is not ComboBoxItem { Tag: Guid selectedTrackId }) return;
                var laps = TrackLaps(selectedTrackId);
                var selectedLaps = laps
                    .Where(lap => selectedLapIds.Contains(lap.Id))
                    .OrderByDescending(lap => lap.StartedAt)
                    .ToArray();
                if (selectedLaps.Length == 0)
                {
                    MessageBox.Show("请先勾选要删除的圈速。", "删除所选圈速", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var historicalBestIds = laps
                    .Where(lap => lap.IsValid)
                    .GroupBy(lap => lap.Vehicle.CarClass)
                    .Select(group => group
                        .OrderBy(lap => lap.TotalSeconds)
                        .ThenBy(lap => lap.StartedAt)
                        .ThenBy(lap => lap.Id)
                        .First().Id)
                    .ToHashSet();
                var selectedHistoricalBestCount = selectedLaps.Count(lap => historicalBestIds.Contains(lap.Id));
                var records = string.Join("\n", selectedLaps.Select(lap =>
                    $"• {lap.StartedAt.ToLocalTime():MM-dd HH:mm:ss}  {PerformanceClassName(lap.Vehicle.CarClass)} {lap.Vehicle.PerformanceIndex}  {AnalysisTime(lap.TotalSeconds, pointToPointTimingApproximate)}" +
                    (historicalBestIds.Contains(lap.Id) ? "  · 历史最快" : string.Empty)));
                var fastestWarning = selectedHistoricalBestCount > 0
                    ? $"\n\n其中 {selectedHistoricalBestCount} 条是对应性能等级当前保留的历史最快圈；删除后，下一条最快有效圈会成为新的历史最快。"
                    : string.Empty;
                if (MessageBox.Show(
                        $"确认删除所选 {selectedLaps.Length} 条圈速？\n\n{records}{fastestWarning}\n\n此操作不可撤销。",
                        "删除所选圈速", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

                foreach (var lap in selectedLaps) module.DeleteLap(lap.Id);
                selectedLapIds.ExceptWith(selectedLaps.Select(lap => lap.Id));
                displayedLapIds.ExceptWith(selectedLaps.Select(lap => lap.Id));
                RenderSelectedPage(true);
            };

            var selectorControls = new StackPanel();
            selectorControls.Children.Add(selector);
            if (selectedTrackIdForActions != Guid.Empty)
            {
                var deleteActions = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
                deleteActions.Children.Add(deleteTrackLaps);
                deleteActions.Children.Add(deleteSelectedLaps);
                selectorControls.Children.Add(deleteActions);
            }
            Grid.SetColumn(selectorControls, 1);
            selectorGrid.Children.Add(selectorControls);
            stack.Children.Add(Card(selectorGrid));
        }

        if (pointToPointTimingApproximate) stack.Children.Add(PointToPointTimingNotice());
        var statusLabel = Label(string.Empty, 15);
        stack.Children.Add(Card(statusLabel));
        var table = new Grid { Margin = new Thickness(4) };
        var sectorRows = new List<TextBlock[]>();
        foreach (var width in new[] { 0.6, 1.1, 1.1, 1.1, 1.1, 1.0 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        AddRow(["段", "当前圈", "本场最快", "历史最快", "秒差", "状态"], 0, true);
        for (var index = 0; index < hud.Sectors.Count; index++)
        {
            var sector = hud.Sectors[index];
            sectorRows.Add(AddRow([
                (sector.Index + 1).ToString(), AnalysisTime(sector.CurrentSeconds, pointToPointTimingApproximate), AnalysisTime(sector.CurrentCompetitionBestSeconds, pointToPointTimingApproximate), AnalysisTime(sector.HistoricalBestSeconds, pointToPointTimingApproximate),
                sector.DeltaSeconds is double delta ? $"{delta:+0.000;-0.000;0.000}" : "—", SectorStateText(sector.State)
            ], index + 1, false));
        }
        stack.Children.Add(Card(table));
        var activeTrack = module.CurrentTrack;
        var activePerformanceClasses = activeTrack is not null &&
                                       selectedLapPerformanceClasses.TryGetValue(activeTrack.Id, out var savedClassFilter)
            ? savedClassFilter
            : [];
        var comparableLaps = activeTrack is null
            ? []
            : module.VisibleLaps
                .Where(lap => lap.TrackId == activeTrack.Id && lap.Direction == activeTrack.Direction &&
                              lap.SectorSchemaVersion == TrackAlgorithms.SectorSchemaVersion &&
                              activePerformanceClasses.Contains(lap.Vehicle.CarClass))
                .OrderByDescending(lap => lap.StartedAt)
                .ToArray();
        selectedLapIds.RemoveWhere(id => comparableLaps.All(lap => lap.Id != id));
        displayedLapIds.RemoveWhere(id => comparableLaps.All(lap => lap.Id != id));
        if (deleteSelectedLapsButton is not null) deleteSelectedLapsButton.IsEnabled = selectedLapIds.Count > 0;
        var comparisonHost = new StackPanel();
        if (comparableLaps.Length > 0)
        {
            var bestLapIds = comparableLaps
                .Where(lap => lap.IsValid)
                .GroupBy(lap => lap.Vehicle.CarClass)
                .Select(group => group
                    .OrderBy(lap => lap.TotalSeconds)
                    .ThenBy(lap => lap.StartedAt)
                    .ThenBy(lap => lap.Id)
                    .First().Id)
                .ToHashSet();
            var focusSessionId = module.HasCurrentCompetitionSession && comparableLaps.Any(lap => lap.SessionId == module.CurrentSessionId)
                ? module.CurrentSessionId
                : comparableLaps[0].SessionId;
            var focusSessionLabel = module.HasCurrentCompetitionSession && focusSessionId == module.CurrentSessionId
                ? "当前比赛"
                : "最近一次比赛";
            var savedTable = new Grid { Margin = new Thickness(4) };
            foreach (var width in new[] { 0.45, 0.75, 1.05, 1.15, 0.8, 0.75, 2.05, 0.6 })
                savedTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            AddSavedRow(0, null, "比赛范围", false, true);
            for (var index = 0; index < comparableLaps.Length; index++)
            {
                var savedLap = comparableLaps[index];
                var isHistoricalBest = savedLap.IsValid && bestLapIds.Contains(savedLap.Id);
                AddSavedRow(index + 1, savedLap,
                    savedLap.SessionId == focusSessionId ? focusSessionLabel : "历史比赛",
                    isHistoricalBest, false);
            }
            var savedStack = new StackPanel();
            var savedHeader = new Grid();
            savedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            savedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var savedHeaderText = new StackPanel();
            savedHeaderText.Children.Add(Label($"已保存圈速 · {comparableLaps.Length}/50", 16, FontWeights.SemiBold));
            savedHeaderText.Children.Add(Label("勾选最多 4 圈，再加载图表；每个性能等级单独标记历史最快。", 11, FontWeights.Normal, "MutedBrush"));
            savedHeader.Children.Add(savedHeaderText);
            var displaySelectedLaps = new Button
            {
                Content = "显示勾选圈数据",
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !displayedLapIds.SetEquals(selectedLapIds),
                ToolTip = "一次性加载当前勾选圈的速度与走线数据"
            };
            displaySelectedLaps.Click += (_, _) =>
            {
                displayedLapIds.Clear();
                displayedLapIds.UnionWith(selectedLapIds);
                displaySelectedLaps.IsEnabled = false;
                RenderComparisonVisuals();
            };
            displaySelectedLapsButton = displaySelectedLaps;
            Grid.SetColumn(displaySelectedLaps, 1);
            savedHeader.Children.Add(displaySelectedLaps);
            savedStack.Children.Add(savedHeader);
            savedStack.Children.Add(savedTable);
            stack.Children.Add(Card(savedStack));

            void AddSavedRow(int row, LapSummary? selectableLap, string group, bool historicalBest, bool header)
            {
                var cells = header
                    ? new[] { "选择", "等级 / PI", "来源 / 玩家", "保存时间", "圈速", "有效性", "分段", "操作" }
                    : new[]
                    {
                        string.Empty,
                        $"{PerformanceClassName(selectableLap!.Vehicle.CarClass)} {selectableLap.Vehicle.PerformanceIndex}",
                        $"{group}\n{PlayerCodeText(selectableLap.PlayerCode)}",
                        selectableLap.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                        AnalysisTime(selectableLap.TotalSeconds, pointToPointTimingApproximate),
                        selectableLap.IsValid ? "有效" : "无效",
                        string.Join("  ", selectableLap.Segments.Select(segment => $"S{segment.Index + 1} {AnalysisTime(segment.TimeSeconds, pointToPointTimingApproximate)}")),
                        "删除"
                    };
                savedTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = header ? 32 : 48 });
                for (var column = 0; column < cells.Length; column++)
                {
                    UIElement cell;
                    if (column == 0 && selectableLap is not null)
                    {
                        var check = new CheckBox
                        {
                            IsChecked = selectedLapIds.Contains(selectableLap.Id),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            ToolTip = "用于图表对比或批量删除"
                        };
                        check.Click += (_, _) =>
                        {
                            if (check.IsChecked == true)
                            {
                                if (selectedLapIds.Count >= 4)
                                {
                                    check.IsChecked = false;
                                    MessageBox.Show("一次最多比较 4 圈。", "圈选择", MessageBoxButton.OK, MessageBoxImage.Information);
                                    return;
                                }
                                selectedLapIds.Add(selectableLap.Id);
                            }
                            else
                            {
                                selectedLapIds.Remove(selectableLap.Id);
                            }
                            if (deleteSelectedLapsButton is not null)
                                deleteSelectedLapsButton.IsEnabled = selectedLapIds.Count > 0;
                            exportSelectedLaps.IsEnabled = selectedLapIds.Count > 0;
                            if (displaySelectedLapsButton is not null)
                                displaySelectedLapsButton.IsEnabled = !displayedLapIds.SetEquals(selectedLapIds);
                        };
                        cell = check;
                    }
                    else if (column == 7 && selectableLap is not null)
                    {
                        var delete = new Button
                        {
                            Content = "删除",
                            MinWidth = 54,
                            Padding = new Thickness(9, 4, 9, 4),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            ToolTip = "删除这条圈速"
                        };
                        delete.Click += (_, _) =>
                        {
                            var fastestWarning = historicalBest
                                ? "\n\n这是程序当前保留的历史最快圈。手动删除后，下一条最快有效圈会成为新的历史最快。"
                                : string.Empty;
                            if (MessageBox.Show(
                                    $"确认删除 {selectableLap.StartedAt.ToLocalTime():MM-dd HH:mm:ss} 的圈速 {AnalysisTime(selectableLap.TotalSeconds, pointToPointTimingApproximate)}？此操作不可撤销。{fastestWarning}",
                                    "删除已保存圈速", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                            module.DeleteLap(selectableLap.Id);
                            selectedLapIds.Remove(selectableLap.Id);
                            displayedLapIds.Remove(selectableLap.Id);
                            RenderSelectedPage(true);
                        };
                        cell = delete;
                    }
                    else
                    {
                        string? brush = null;
                        if (!header && column == 2) brush = group == "历史比赛" ? "MutedBrush" : "SuccessBrush";
                        if (!header && historicalBest && column == 4) brush = "PurpleBrush";
                        var textCell = Label(cells[column], header ? 12 : 11, header ? FontWeights.SemiBold : FontWeights.Normal, brush);
                        if (column == 6)
                        {
                            textCell.TextWrapping = TextWrapping.NoWrap;
                            textCell.TextTrimming = TextTrimming.CharacterEllipsis;
                            if (!header) textCell.ToolTip = cells[column];
                        }
                        if (!header && column == 2)
                        {
                            textCell.TextWrapping = TextWrapping.Wrap;
                            textCell.ToolTip = $"{group} · 玩家代号：{PlayerCodeText(selectableLap!.PlayerCode)}";
                        }
                        cell = textCell;
                    }
                    cell.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 6, 8, 6));
                    cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                    Grid.SetRow(cell, row);
                    Grid.SetColumn(cell, column);
                    savedTable.Children.Add(cell);
                }
            }
        }
        stack.Children.Add(comparisonHost);
        RenderComparisonVisuals();
        stack.Children.Add(Card(Label(SectorColorClassifier.DatasetBestExplanation, 12, FontWeights.Normal, "MutedBrush")));
        var initialTrackId = module.CurrentTrack?.Id;
        var initialTrackTimingKind = module.CurrentTrack?.TimingKind;
        var initialCompletedLaps = hud.CompletedLaps;
        refreshVisiblePage = () =>
        {
            if (initialTrackId is Guid estateTrackId &&
                initialTrackTimingKind == TrackTimingKind.EstateGeometry &&
                store.LoadLapSummaries(estateTrackId, LazyForzaStore.MaxLapsPerTrack).Count != module.VisibleLaps.Count)
            {
                module.RefreshSelectedTrackHistory();
                RenderSelectedPage(true);
                return;
            }
            if (module.Snapshot is not LapHudState current ||
                current.Sectors.Count != sectorRows.Count ||
                current.CompletedLaps != initialCompletedLaps ||
                module.CurrentTrack?.Id != initialTrackId)
            {
                RenderSelectedPage(true);
                return;
            }

            statusLabel.Text = AppLocalization.Format(
                "lap.status",
                "{0} · 已保存 {1} 圈\n{2}",
                AppLocalization.Literal(current.TrackName),
                current.CompletedLaps,
                AppLocalization.Literal(current.Status));
            for (var index = 0; index < current.Sectors.Count; index++)
            {
                var sector = current.Sectors[index];
                var cells = sectorRows[index];
                cells[0].Text = (sector.Index + 1).ToString();
                cells[1].Text = AnalysisTime(sector.CurrentSeconds, pointToPointTimingApproximate);
                cells[2].Text = AnalysisTime(sector.CurrentCompetitionBestSeconds, pointToPointTimingApproximate);
                cells[3].Text = AnalysisTime(sector.HistoricalBestSeconds, pointToPointTimingApproximate);
                cells[4].Text = sector.DeltaSeconds is double delta ? $"{delta:+0.000;-0.000;0.000}" : "—";
                cells[5].Text = SectorStateText(sector.State);
                cells[5].Foreground = Brush(SectorStateBrush(sector.State));
            }
        };
        refreshVisiblePage();
        return Scroll(stack);

        static string TrackAnalysisKind(TrackSummary summary) => summary.TimingKind == TrackTimingKind.EstateGeometry
            ? "地产环道"
            : summary.LayoutKind == TrackLayoutKind.PointToPoint
                ? "定点"
                : "环道";

        void RenderComparisonVisuals()
        {
            comparisonHost.Children.Clear();
            var selectedVisualLaps = comparableLaps
                .Where(lap => displayedLapIds.Contains(lap.Id))
                .OrderBy(lap => lap.StartedAt)
                .Take(4)
                .ToArray();
            var singleLapPlan = selectedVisualLaps.Length == 1
                ? LapComparisonPlanner.Resolve(selectedVisualLaps[0], comparableLaps)
                : null;
            var lapIdsToLoad = selectedVisualLaps
                .Select(lap => lap.Id)
                .ToList();
            if (singleLapPlan?.ReferenceLap is { } referenceSummary &&
                !lapIdsToLoad.Contains(referenceSummary.Id))
                lapIdsToLoad.Add(referenceSummary.Id);
            var loadedLaps = module.LoadLapDetails(lapIdsToLoad)
                .Where(lap => lap.Samples.Count >= 2)
                .ToArray();
            var visualLaps = singleLapPlan is null
                ? loadedLaps.OrderBy(lap => lap.StartedAt).ToArray()
                : lapIdsToLoad
                    .Select(id => loadedLaps.FirstOrDefault(lap => lap.Id == id))
                    .Where(lap => lap is not null)
                    .Cast<LapRecord>()
                    .ToArray();
            if (visualLaps.Length == 0)
            {
                comparisonHost.Children.Add(activeTrack is null
                    ? module.HasCurrentCompetitionSession
                        ? EmptyCard(hud.TrackName, AppLocalization.Literal(hud.Status))
                        : EmptyCard("未选择赛道", "从上方选择赛道，或进入比赛后自动识别。")
                    : activePerformanceClasses.Count == 0
                        ? EmptyCard("未选择性能等级", "选择至少一个性能等级。")
                        : comparableLaps.Length > 0 && displayedLapIds.Count == 0
                            ? selectedLapIds.Count == 0
                                ? EmptyCard("未选择对比圈", "勾选最多 4 圈，再点击“显示勾选圈数据”。")
                                : EmptyCard("圈速已勾选", "点击“显示勾选圈数据”加载速度曲线与走线。")
                            : EmptyCard("暂无圈速", "完成对应等级的比赛后显示。"));
                return;
            }

            var previewStack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var legendEntries = new List<LapSeriesLegendEntry>(visualLaps.Length);
            for (var index = 0; index < visualLaps.Length; index++)
            {
                var lap = visualLaps[index];
                var pi = lap.Vehicle.PerformanceIndex >= 0 ? lap.Vehicle.PerformanceIndex.ToString() : "—";
                var role = singleLapPlan is null
                    ? null
                    : lap.Id == singleLapPlan.SelectedLap.Id
                        ? singleLapPlan.Mode == SingleLapAnalysisMode.AnalyzePersonalBest
                            ? "所选圈 · 同等级个人最快"
                            : "所选圈"
                        : "个人参考圈 · 同等级最快";
                legendEntries.Add(new LapSeriesLegendEntry(
                    $"{AnalysisTime(lap.TotalSeconds, pointToPointTimingApproximate)}" +
                    $"{(role is null ? string.Empty : $" · {role}")}",
                    $"{PerformanceClassName(lap.Vehicle.CarClass)} {pi} · " +
                    $"{lap.StartedAt.ToLocalTime():MM-dd HH:mm:ss}" +
                    $"{(lap.IsValid ? string.Empty : " · 无效")}"));
            }

            IReadOnlyList<CornerMapAnnotation> cornerAnnotations = [];
            if (singleLapPlan is not null)
            {
                var selectedDetail = visualLaps.FirstOrDefault(lap => lap.Id == singleLapPlan.SelectedLap.Id);
                var referenceDetail = singleLapPlan.ReferenceLap is null
                    ? null
                    : visualLaps.FirstOrDefault(lap => lap.Id == singleLapPlan.ReferenceLap.Id);
                if (selectedDetail is not null)
                    cornerAnnotations = BuildCornerMapAnnotations(
                        singleLapPlan,
                        selectedDetail,
                        referenceDetail,
                        pointToPointTimingApproximate);
            }

            var visuals = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var selectedDynamicsLayer = DrivingDynamicsLayer.Default;
            var dynamicsLapId = singleLapPlan?.SelectedLap.Id ?? visualLaps[0].Id;
            var linkedCursor = new LapAnalysisCursor();
            var exportRow = new Grid();
            exportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            exportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var exportHint = Label(
                visualLaps.Length == 1 ? "导出单圈分析、速度曲线和走线。" : "导出当前多圈对比、速度曲线和走线。",
                11,
                FontWeights.Normal,
                "MutedBrush");
            exportHint.VerticalAlignment = VerticalAlignment.Center;
            exportRow.Children.Add(exportHint);
            var exportPng = new Button
            {
                Content = "导出 PNG",
                Padding = new Thickness(13, 7, 13, 7),
                Margin = new Thickness(12, 0, 0, 8),
                ToolTip = "导出固定尺寸的圈速分析图片"
            };
            exportPng.Click += (_, _) => ExportLapComparisonPng(
                activeTrack?.Name ?? hud.TrackName,
                activeTrack,
                visualLaps,
                legendEntries,
                cornerAnnotations,
                pointToPointTimingApproximate,
                selectedDynamicsLayer,
                dynamicsLapId);
            Grid.SetColumn(exportPng, 1);
            exportRow.Children.Add(exportPng);
            visuals.Children.Add(exportRow);
            var chartPanel = new Grid { Height = 320 };
            chartPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chartPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var chartTitle = Label("速度曲线 · 与驾驶输入和走线联动", 13, FontWeights.SemiBold);
            chartTitle.Margin = new Thickness(0, 0, 0, 8);
            chartPanel.Children.Add(chartTitle);
            var chart = new LapTelemetryChart(
                visualLaps,
                activeTrack?.LengthMeters,
                legendEntries,
                linkedCursor);
            Grid.SetRow(chart, 1);
            chartPanel.Children.Add(chart);
            visuals.Children.Add(Card(chartPanel));

            var inputLap = visualLaps.First(lap => lap.Id == dynamicsLapId);
            var inputPanel = new Grid { Height = 250 };
            inputPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inputPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var inputTitle = Label(
                "驾驶输入曲线 · 油门 / 制动 / 方向 · 与速度和走线联动",
                13,
                FontWeights.SemiBold);
            inputTitle.Margin = new Thickness(0, 0, 0, 8);
            inputPanel.Children.Add(inputTitle);
            var inputChart = new LapInputChart(
                inputLap,
                activeTrack?.LengthMeters,
                linkedCursor);
            Grid.SetRow(inputChart, 1);
            inputPanel.Children.Add(inputChart);
            visuals.Children.Add(Card(inputPanel));

            var mapPanel = new Grid
            {
                Height = LapAnalysisVisualLayout.AdaptiveMapHeight(
                    ActualHeight > 0 ? ActualHeight : Height)
            };
            mapPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mapPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var mapHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var mapTitle = Label(
                cornerAnnotations.Count == 0
                    ? "走线预览 · 滚轮缩放，拖动平移"
                    : $"走线预览 · {cornerAnnotations.Count} 个弯角标记 · 悬停查看分析",
                13,
                FontWeights.SemiBold);
            mapHeader.Children.Add(mapTitle);
            mapPanel.Children.Add(mapHeader);
            var mapSurface = new Grid();
            var mapView = new TrackMapView(
                visualLaps,
                activeTrack,
                legendEntries,
                cornerAnnotations,
                dynamicsLapId,
                linkedCursor);
            if (singleLapPlan is not null)
            {
                var layerControls = DynamicsLayerControls(
                    mapView,
                    layer => selectedDynamicsLayer = layer);
                layerControls.Margin = new Thickness(0, 7, 0, 0);
                mapHeader.Children.Add(layerControls);
            }
            mapSurface.Children.Add(mapView);
            mapSurface.Children.Add(MapDisplayControls(mapView));
            Grid.SetRow(mapSurface, 1);
            mapPanel.Children.Add(mapSurface);
            void ResizeMap() =>
                mapPanel.Height = LapAnalysisVisualLayout.AdaptiveMapHeight(ActualHeight);
            SizeChangedEventHandler resizeMap = (_, _) =>
                ResizeMap();
            mapPanel.Loaded += (_, _) =>
            {
                SizeChanged += resizeMap;
                ResizeMap();
            };
            mapPanel.Unloaded += (_, _) => SizeChanged -= resizeMap;
            visuals.Children.Add(Card(mapPanel));
            previewStack.Children.Add(visuals);
            comparisonHost.Children.Add(previewStack);
        }

        TextBlock[] AddRow(string[] cells, int row, bool header)
        {
            var controls = new TextBlock[cells.Length];
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < cells.Length; column++)
            {
                var text = Label(cells[column], header ? 13 : 12, header ? FontWeights.SemiBold : FontWeights.Normal);
                text.Margin = new Thickness(8, 7, 8, 7);
                Grid.SetRow(text, row); Grid.SetColumn(text, column); table.Children.Add(text);
                controls[column] = text;
            }
            return controls;
        }
    }

    private static Border MapDisplayControls(TrackMapView mapView)
    {
        static ToggleButton IconToggle(
            string iconData,
            string toolTip,
            bool isChecked,
            bool isEnabled = true)
        {
            var icon = new VectorPath
            {
                Data = Geometry.Parse(iconData),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                Stroke = new SolidColorBrush(Color.FromRgb(238, 244, 248)),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            return new ToggleButton
            {
                Content = icon,
                Width = 36,
                Height = 36,
                Padding = new Thickness(8),
                Margin = new Thickness(2),
                IsChecked = isChecked,
                IsEnabled = isEnabled,
                ToolTip = toolTip
            };
        }

        var endpoints = IconToggle(
            "M 5 21 L 5 4 M 6 5 C 9 3.5 11 6.5 14 5 C 16.5 3.8 18.5 4.5 20 5.5 L 20 13 C 18 12 16.5 11.8 14.5 13 C 11.5 14.5 9 11.5 6 13 Z M 10 4.8 L 10 12.7 M 15 4.8 L 15 12.8",
            "显示或隐藏起点、终点与方向",
            isChecked: true);
        endpoints.Click += (_, _) => mapView.ShowEndpoints = endpoints.IsChecked == true;

        var corners = IconToggle(
            "M 3 18 C 6 18 6 7 11 7 C 16 7 15 18 21 18 M 11 7 L 11 3 M 8 3 L 14 3",
            mapView.CornerAnnotationCount == 0
                ? "当前圈没有检测到明显减速弯"
                : $"显示或隐藏 {mapView.CornerAnnotationCount} 个弯角分析标记",
            isChecked: mapView.CornerAnnotationCount > 0,
            isEnabled: mapView.CornerAnnotationCount > 0);
        mapView.ShowCornerAnnotations = corners.IsChecked == true;
        corners.Click += (_, _) => mapView.ShowCornerAnnotations = corners.IsChecked == true;

        var legend = IconToggle(
            "M 4 6 L 8 6 M 11 6 L 20 6 M 4 12 L 8 12 M 11 12 L 20 12 M 4 18 L 8 18 M 11 18 L 20 18",
            "显示或隐藏曲线颜色说明",
            isChecked: true);
        legend.Click += (_, _) => mapView.ShowLegend = legend.IsChecked == true;

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        controls.Children.Add(endpoints);
        controls.Children.Add(corners);
        controls.Children.Add(legend);

        var chrome = new Border
        {
            Child = controls,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Padding = new Thickness(3),
            Background = new SolidColorBrush(Color.FromArgb(210, 14, 21, 29)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(210, 67, 84, 101)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };
        Panel.SetZIndex(chrome, 10);
        return chrome;
    }

    private static WrapPanel DynamicsLayerControls(
        TrackMapView mapView,
        Action<DrivingDynamicsLayer> onChanged)
    {
        var panel = new WrapPanel();
        var layers = new[]
        {
            DrivingDynamicsLayer.Default,
            DrivingDynamicsLayer.Throttle,
            DrivingDynamicsLayer.Brake,
            DrivingDynamicsLayer.Steering,
            DrivingDynamicsLayer.TireSlip,
            DrivingDynamicsLayer.HandlingBalance,
            DrivingDynamicsLayer.ExitWheelspin,
            DrivingDynamicsLayer.BrakingInstability
        };
        var buttons = new List<ToggleButton>(layers.Length);
        foreach (var layer in layers)
        {
            var needsExtended = DrivingDynamicsAnalyzer.RequiresExtendedTelemetry(layer);
            var button = new ToggleButton
            {
                Content = DrivingDynamicsAnalyzer.LayerName(layer),
                IsChecked = layer == DrivingDynamicsLayer.Default,
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 6, 5),
                FontSize = 11,
                ToolTip = needsExtended && !mapView.HasExtendedDynamics
                    ? "旧版本保存的圈速未记录此图层所需的动态遥测；切换后会显示不可用提示。"
                    : "切换走线着色图层；悬停可查看当前位置数据。"
            };
            button.Click += (_, _) =>
            {
                foreach (var candidate in buttons) candidate.IsChecked = false;
                button.IsChecked = true;
                mapView.DynamicsLayer = layer;
                onChanged(layer);
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        return panel;
    }

    private IReadOnlyList<CornerMapAnnotation> BuildCornerMapAnnotations(
        SingleLapAnalysisPlan plan,
        LapRecord selectedLap,
        LapRecord? referenceLap,
        bool pointToPointTimingApproximate)
    {
        if (plan.Mode == SingleLapAnalysisMode.CompareWithClassFastest && referenceLap is not null)
        {
            var comparisons = CornerDrivingAnalyzer.Compare(selectedLap, referenceLap);
            var context =
                $"对比同等级个人最快 · 所选 {AnalysisTime(selectedLap.TotalSeconds, pointToPointTimingApproximate)} / " +
                $"参考 {AnalysisTime(referenceLap.TotalSeconds, pointToPointTimingApproximate)}";
            var footer =
                $"参考情况：{ReferenceMatchText(selectedLap.Vehicle, referenceLap.Vehicle)}。" +
                "LazyForza 只能提供轻度范围内的分析，仅供参考。";
            return comparisons
                .OrderByDescending(corner => corner.TimeLossSeconds)
                .Take(8)
                .Select(corner => ComparisonCornerAnnotation(
                    selectedLap.Id,
                    corner,
                    context,
                    footer))
                .ToArray();
        }

        var contextText = plan.Mode == SingleLapAnalysisMode.AnalyzePersonalBest
            ? "同等级个人最快 · 轻度跑法分析"
            : "暂无同等级参考圈 · 仅分析明显驾驶节奏";
        const string footerText =
            "依据已保存的速度、输入、位置与动态遥测；旧圈可能不含轮胎滑移。" +
            "LazyForza 只能提供轻度范围内的分析，仅供参考。";
        return CornerDrivingAnalyzer.AnalyzePersonalBest(selectedLap)
            .OrderByDescending(corner => corner.OpportunityScore)
            .Take(8)
            .Select(corner => OptimizationCornerAnnotation(
                selectedLap.Id,
                corner,
                contextText,
                footerText))
            .ToArray();
    }

    private static CornerMapAnnotation ComparisonCornerAnnotation(
        Guid lapId,
        CornerComparisonMetrics corner,
        string context,
        string footer)
    {
        var brake = corner.BrakePointDeltaMeters is double brakeDelta
            ? brakeDelta >= 0
                ? $"刹车晚 {brakeDelta:0} m"
                : $"刹车早 {Math.Abs(brakeDelta):0} m"
            : "没有找到稳定刹车点";
        var throttle = corner.ThrottleRecoveryDeltaSeconds is double throttleDelta
            ? throttleDelta >= 0
                ? $"补油晚 {throttleDelta:0.00} s"
                : $"补油早 {Math.Abs(throttleDelta):0.00} s"
            : "没有找到稳定补油点";
        var speeds =
            $"入弯 {corner.SelectedEntrySpeedKph:0}/{corner.ReferenceEntrySpeedKph:0}，" +
            $"最低 {corner.SelectedMinimumSpeedKph:0}/{corner.ReferenceMinimumSpeedKph:0}，" +
            $"出弯 {corner.SelectedExitSpeedKph:0}/{corner.ReferenceExitSpeedKph:0} km/h";
        var gears =
            $"弯心 {GearText(corner.SelectedApexGear)}/{GearText(corner.ReferenceApexGear)}，" +
            $"弯中换挡 {corner.SelectedGearChanges}/{corner.ReferenceGearChanges} 次";
        var accent = corner.TimeLossSeconds > 0.05
            ? Color.FromRgb(242, 184, 39)
            : corner.TimeLossSeconds < -0.05
                ? Color.FromRgb(57, 217, 138)
                : Color.FromRgb(32, 184, 207);
        return new CornerMapAnnotation(
            lapId,
            corner.Window,
            context,
            $"弯道 {corner.Window.Number} · {ProgressRange(corner.Window)} · {CornerTimeText(corner.TimeLossSeconds)}",
            $"{brake} · {throttle}\n{speeds}\n走线平均偏离 {corner.MeanLineDeviationMeters:0.0} m · {gears}",
            ComparisonHint(corner),
            footer,
            accent);
    }

    private static CornerMapAnnotation OptimizationCornerAnnotation(
        Guid lapId,
        CornerOptimizationMetrics corner,
        string context,
        string footer)
    {
        var throttle = corner.ThrottleRecoverySeconds is double recovery
            ? $"过弯心后 {recovery:0.00} s 恢复 70% 油门"
            : "这段没有恢复到 70% 油门";
        var details =
            $"入弯/最低/出弯 {corner.EntrySpeedKph:0}/{corner.MinimumSpeedKph:0}/{corner.ExitSpeedKph:0} km/h · " +
            $"{throttle}\n无油无刹 {corner.CoastingSeconds:0.00} s · 油刹重叠 {corner.BrakeThrottleOverlapSeconds:0.00} s · " +
            $"弯心 {GearText(corner.ApexGear)} · 弯中换挡 {corner.GearChanges} 次";
        var accent = corner.OpportunityScore >= 1.2
            ? Color.FromRgb(242, 184, 39)
            : Color.FromRgb(32, 184, 207);
        return new CornerMapAnnotation(
            lapId,
            corner.Window,
            context,
            $"弯道 {corner.Window.Number} · {ProgressRange(corner.Window)}",
            details,
            OptimizationHint(corner),
            footer,
            accent);
    }

    private static string ComparisonHint(CornerComparisonMetrics corner)
    {
        if (corner.TimeLossSeconds < -0.05) return "该区间快于参考圈，可保留当前处理方式。";
        var hints = new List<string>();
        if (corner.BrakePointDeltaMeters is < -4) hints.Add("刹车偏早");
        if (corner.SelectedMinimumSpeedKph < corner.ReferenceMinimumSpeedKph - 3) hints.Add("弯心速度偏低");
        if (corner.ThrottleRecoveryDeltaSeconds is > 0.15) hints.Add("补油偏晚");
        if (corner.SelectedExitSpeedKph < corner.ReferenceExitSpeedKph - 3) hints.Add("出弯速度偏低");
        if (corner.MeanLineDeviationMeters > 2) hints.Add("走线差异较大");
        if (corner.SelectedGearChanges > corner.ReferenceGearChanges)
            hints.Add("弯中换挡比参考圈多");
        if (corner.SelectedApexGear != corner.ReferenceApexGear)
            hints.Add("弯心挡位不同，需结合车辆确认");
        return hints.Count == 0
            ? "单项差异不大，这段更像是整体节奏损失。"
            : $"建议优先复查：{string.Join("、", hints)}。";
    }

    private static string OptimizationHint(CornerOptimizationMetrics corner)
    {
        var hints = new List<string>();
        if (corner.CoastingSeconds > 0.35) hints.Add("可复查是否存在过长的无油无刹滑行");
        if (corner.ThrottleRecoverySeconds is > 0.90) hints.Add("可尝试略早且更平缓地恢复油门");
        if (corner.BrakeThrottleOverlapSeconds > 0.25) hints.Add("油刹重叠偏多");
        if (corner.GearChanges > 1) hints.Add("复核入弯挡位，尽量少在弯中换挡");
        return hints.Count == 0
            ? "当前数据未显示明显问题，无需仅为配合分析结论而改变跑法。"
            : $"{string.Join("；", hints)}。";
    }

    private static string ReferenceMatchText(
        VehicleProfileFingerprint selected,
        VehicleProfileFingerprint reference)
    {
        if (selected == reference) return "车辆和调校指纹一致";
        if (selected.CarOrdinal == reference.CarOrdinal &&
            selected.PerformanceIndex == reference.PerformanceIndex)
            return "车型和 PI 一致，但调校指纹不同或尚未识别";
        if (selected.CarOrdinal == reference.CarOrdinal)
            return "车型相同，但 PI 或调校不同";
        return "同等级不同车辆，速度和挡位差异要谨慎看";
    }

    private static string CornerTimeText(double deltaSeconds) =>
        deltaSeconds >= 0
            ? $"损失 {deltaSeconds:0.000} s"
            : $"快 {Math.Abs(deltaSeconds):0.000} s";

    private static string ProgressRange(CornerWindow window) =>
        $"{window.StartS / 1_000:0.00}–{window.EndS / 1_000:0.00} km";

    private static string GearText(int gear) => gear > 0 ? $"{gear} 挡" : "—";

    private UIElement TracksPage()
    {
        var stack = PageStack("赛道", "管理自定义赛道，浏览内置官方赛事。");
        var lapModule = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
        var estateModule = moduleManager.Modules.OfType<EstateCircuitModule>().Single();
        if (lapModule.IncompatibleTrackName is { } incompatibleTrack)
        {
            stack.Children.Add(Card(Label(
                $"“{incompatibleTrack}”使用旧版起点逻辑，已停止参与匹配。进入比赛后可重新学习。",
                13, FontWeights.SemiBold, "AccentBrush")));
        }

        var allTracks = store.ListTracks();
        var customTracks = allTracks
            .Where(track => track.CatalogKind == TrackCatalogKind.UserCustom &&
                            track.TimingKind == TrackTimingKind.GameEvent &&
                            string.Equals(track.Source, CurrentTrackSource, StringComparison.Ordinal))
            .ToArray();
        var estateTracks = allTracks
            .Where(track => track.CatalogKind == TrackCatalogKind.UserCustom &&
                            track.TimingKind == TrackTimingKind.EstateGeometry)
            .ToArray();
        var officialTracks = allTracks
            .Where(track => track.CatalogKind == TrackCatalogKind.PlaygroundOfficial)
            .ToArray();

        var estateSection = EstateTrackSection(estateTracks, estateModule);
        stack.Children.Add(estateSection.View);
        stack.Children.Add(CustomTrackSection(customTracks, lapModule));
        stack.Children.Add(OfficialTrackSection(officialTracks, lapModule));
        refreshVisiblePage = estateSection.Refresh;
        return Scroll(stack);
    }

    private Border CustomTrackSection(IReadOnlyList<TrackSummary> tracks, LapAnalysisModule lapModule)
    {
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Label("用户自定义赛道", 20, FontWeights.SemiBold));
        heading.Children.Add(Label("你学习的赛道，可重命名或删除。", 12, FontWeights.Normal, "MutedBrush"));
        header.Children.Add(heading);

        var add = new Button
        {
            Content = lapModule.IsTrackLearningRequested ? "等待下一场比赛" : "添加赛道",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16, 8, 16, 8),
            IsEnabled = !lapModule.IsTrackLearningRequested
        };
        add.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    "下场比赛将用于学习赛道。请完整跑完路线，期间不要倒带、传送或退出。开始学习吗？",
                    "添加自定义赛道",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            lapModule.ResetTrackLearning();
            RenderSelectedPage();
        };
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        content.Children.Add(header);

        if (lapModule.IsTrackLearningRequested)
        {
            var waiting = new Grid();
            waiting.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            waiting.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var waitingCopy = new StackPanel();
            waitingCopy.Children.Add(Label("已准备录入下一场比赛", 15, FontWeights.SemiBold, "AccentBrush"));
            waitingCopy.Children.Add(Label(
                "进入目标赛事并正常起步。环道完整跑一圈后保存；定点赛道在完赛后保存。",
                12,
                FontWeights.Normal,
                "MutedBrush"));
            waiting.Children.Add(waitingCopy);
            var cancelLearning = new Button
            {
                Content = "取消录入",
                MinWidth = 92,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            cancelLearning.Click += (_, _) =>
            {
                lapModule.CancelTrackLearning();
                RenderSelectedPage();
            };
            Grid.SetColumn(cancelLearning, 1);
            waiting.Children.Add(cancelLearning);
            var waitingCard = Card(waiting);
            waitingCard.Margin = new Thickness(0, 14, 0, 12);
            content.Children.Add(waitingCard);
        }

        var guidance = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, lapModule.IsTrackLearningRequested ? 0 : 14, 0, 16),
            Child = Label(
                "进入比赛并完整跑完路线。环道再次过线后保存，定点赛道在完赛后保存。",
                12,
                FontWeights.Normal,
                "MutedBrush")
        };
        content.Children.Add(guidance);

        if (tracks.Count == 0)
        {
            content.Children.Add(Label(
                "还没有自定义赛道。点击“添加赛道”开始学习。",
                13,
                FontWeights.Normal,
                "MutedBrush"));
        }
        else
        {
            var grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var track in tracks)
            {
                var template = LoadTrackPreview(track.Id);
                if (template is not null) grid.Children.Add(TrackCard(track, template, lapModule, false));
            }
            content.Children.Add(grid);
        }

        return TrackSectionContainer(content);
    }

    private Border OfficialTrackSection(
        IReadOnlyList<TrackSummary> tracks,
        LapAnalysisModule lapModule)
    {
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Label(PlaygroundOfficialTrackCatalog.DisplayName, 20, FontWeights.SemiBold));
        heading.Children.Add(Label(
            "内置官方赛事模板，只读。缩略图来自实际行驶轨迹。",
            12,
            FontWeights.Normal,
            "MutedBrush"));
        header.Children.Add(heading);
        var count = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Label(AppLocalization.Format("tracks.count", "{0} 条", tracks.Count), 12, FontWeights.SemiBold, "AccentBrush")
        };
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        content.Children.Add(header);

        foreach (var category in tracks
                     .GroupBy(track => track.Category ?? "其他")
                     .OrderBy(group => OfficialCategoryOrder(group.Key))
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var categoryHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 20, 0, 10)
            };
            categoryHeader.Children.Add(new Border
            {
                Width = 4,
                Height = 18,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(TrackMapPreview.CategoryColor(category.Key)),
                Margin = new Thickness(0, 1, 9, 0)
            });
            categoryHeader.Children.Add(Label(AppLocalization.Format(
                "tracks.categoryCount",
                "{0}  ·  {1} 条",
                AppLocalization.Literal(category.Key),
                category.Count()), 15, FontWeights.SemiBold));
            content.Children.Add(categoryHeader);

            var grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var track in category.OrderBy(track => track.Name, StringComparer.Ordinal))
            {
                var template = LoadTrackPreview(track.Id);
                if (template is not null) grid.Children.Add(TrackCard(track, template, lapModule, true));
            }
            content.Children.Add(grid);
        }

        return TrackSectionContainer(content);
    }

    private Border TrackCard(
        TrackSummary summary,
        TrackTemplate template,
        LapAnalysisModule lapModule,
        bool official)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(138) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var preview = new Grid();
        preview.Children.Add(new TrackMapPreview(template));
        var layoutBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(205, 20, 29, 39)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 210, 225, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = Label(AppLocalization.Literal(summary.LayoutKind == TrackLayoutKind.PointToPoint ? "定点" : "环道"), 10, FontWeights.SemiBold)
        };
        preview.Children.Add(layoutBadge);
        ApplyTopRoundedClip(preview, 9);
        root.Children.Add(preview);

        var divider = new Border { Background = Brush("BorderBrush") };
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);

        var details = new StackPanel { Margin = new Thickness(13, 11, 13, 12) };
        var name = Label(summary.Name, 15, FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        details.Children.Add(name);
        details.Children.Add(Label(
            AppLocalization.Format("tracks.lengthLaps", "{0:0.00} km  ·  {1} 圈", summary.Length / 1000, summary.Laps),
            11,
            FontWeights.Normal,
            "MutedBrush"));

        if (official)
        {
            var readOnly = Label("Playground 官方 · 只读", 10, FontWeights.SemiBold, "AccentBrush");
            readOnly.Margin = new Thickness(0, 7, 0, 0);
            details.Children.Add(readOnly);
        }
        else
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 9, 0, 0)
            };
            var rename = new Button { Content = "重命名", Padding = new Thickness(10, 4, 10, 4) };
            rename.Click += (_, _) =>
            {
                var nextName = TrackNameDialog.Ask(this, summary.Name);
                if (string.IsNullOrWhiteSpace(nextName)) return;
                store.RenameTrack(summary.Id, nextName);
                lapModule.RenameCurrentTrack(summary.Id, nextName);
                RenderSelectedPage();
            };
            actions.Children.Add(rename);
            var delete = new Button
            {
                Content = "删除",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(7, 0, 0, 0)
            };
            delete.Click += (_, _) =>
            {
                if (MessageBox.Show(
                        $"删除“{summary.Name}”及其 {summary.Laps} 圈记录？此操作不可撤销。",
                        "确认删除自定义赛道",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                store.DeleteTrack(summary.Id);
                trackPreviewCache.Remove(summary.Id);
                if (lapModule.CurrentTrack?.Id == summary.Id) lapModule.ResetTrackLearning();
                RenderSelectedPage();
            };
            actions.Children.Add(delete);
            details.Children.Add(actions);
        }

        Grid.SetRow(details, 2);
        root.Children.Add(details);
        var categoryColor = TrackMapPreview.CategoryColor(summary.Category);
        var hoverOutline = new Border
        {
            BorderBrush = new SolidColorBrush(categoryColor),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(9),
            Opacity = 0,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(hoverOutline, 3);
        Panel.SetZIndex(hoverOutline, 10);
        root.Children.Add(hoverOutline);
        var card = new Border
        {
            Width = 266,
            Height = official ? 232 : 268,
            Background = new SolidColorBrush(Color.FromRgb(17, 24, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 54, 69)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 12, 12),
            Cursor = System.Windows.Input.Cursors.Hand,
            RenderTransform = new TranslateTransform(),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = root
        };
        card.MouseEnter += (_, _) =>
        {
            AnimateCardOffset(card, -5, 140);
            AnimateOpacity(hoverOutline, 1, 120);
            card.Background = new SolidColorBrush(Color.FromRgb(27, 38, 50));
            card.BorderBrush = new SolidColorBrush(categoryColor);
            Panel.SetZIndex(card, 1);
        };
        card.MouseLeave += (_, _) =>
        {
            AnimateCardOffset(card, 0, 180);
            AnimateOpacity(hoverOutline, 0, 150);
            card.Background = new SolidColorBrush(Color.FromRgb(17, 24, 33));
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 54, 69));
            Panel.SetZIndex(card, 0);
        };
        card.MouseLeftButtonUp += (_, eventArgs) =>
        {
            if (IsInsideButton(eventArgs.OriginalSource as DependencyObject, card)) return;
            OpenTrackAnalysis(summary, lapModule);
            eventArgs.Handled = true;
        };
        return card;
    }

    private void OpenTrackAnalysis(TrackSummary summary, LapAnalysisModule lapModule)
    {
        if (lapModule.CurrentTrack?.Id != summary.Id)
        {
            if (lapModule.HasCurrentCompetitionSession && MessageBox.Show(
                    "切换赛道会结束当前比赛的分析。仍要继续吗？",
                    "切换分析赛道",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                lapModule.SelectTrack(summary.Id);
                selectedLapIds.Clear();
                displayedLapIds.Clear();
            }
            catch (InvalidOperationException exception)
            {
                MessageBox.Show(exception.Message, "无法选择赛道", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        navigation.SelectedIndex = 4;
        if (navigation.SelectedIndex == 4 && content.Content is not ScrollViewer)
            RenderSelectedPage();
    }

    private static void AnimateCardOffset(Border card, double target, int durationMilliseconds)
    {
        if (card.RenderTransform is not TranslateTransform transform) return;
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private static void AnimateOpacity(UIElement element, double target, int durationMilliseconds) =>
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

    private static bool IsInsideButton(DependencyObject? source, DependencyObject stopAt)
    {
        for (var current = source; current is not null && current != stopAt; current = ParentOf(current))
        {
            if (current is ButtonBase) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject current)
    {
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    private static void ApplyTopRoundedClip(FrameworkElement element, double radius)
    {
        void UpdateClip()
        {
            var width = element.ActualWidth;
            var height = element.ActualHeight;
            if (width <= 0 || height <= 0) return;
            var cornerRadius = Math.Min(radius, Math.Min(width, height) / 2);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(0, height), true, true);
                context.LineTo(new Point(0, cornerRadius), true, false);
                context.ArcTo(
                    new Point(cornerRadius, 0),
                    new Size(cornerRadius, cornerRadius),
                    0,
                    false,
                    SweepDirection.Clockwise,
                    true,
                    false);
                context.LineTo(new Point(width - cornerRadius, 0), true, false);
                context.ArcTo(
                    new Point(width, cornerRadius),
                    new Size(cornerRadius, cornerRadius),
                    0,
                    false,
                    SweepDirection.Clockwise,
                    true,
                    false);
                context.LineTo(new Point(width, height), true, false);
            }
            geometry.Freeze();
            element.Clip = geometry;
        }

        element.SizeChanged += (_, _) => UpdateClip();
    }

    private TrackTemplate? LoadTrackPreview(Guid trackId)
    {
        if (trackPreviewCache.TryGetValue(trackId, out var cached)) return cached;
        var loaded = store.LoadTrack(trackId);
        if (loaded is null) return null;
        trackPreviewCache[trackId] = loaded.Value.Track;
        return loaded.Value.Track;
    }

    private static Border TrackSectionContainer(UIElement content) => new()
    {
        Background = Brush("CardBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(18),
        Margin = new Thickness(0, 8, 0, 16),
        Child = content
    };

    private static int OfficialCategoryOrder(string category) => category switch
    {
        "公路" => 0,
        "街头" => 1,
        "泥地" => 2,
        "越野" => 3,
        "山道" => 4,
        "直线" => 5,
        _ => 99
    };

    private UIElement ShiftPage()
    {
        var stack = PageStack("车辆与换挡", "管理车辆配置，并根据有效加速数据学习升降挡提示。");
        var module = moduleManager.Modules.OfType<DashboardModule>().Single();
        var summaryLabel = Label(string.Empty, 15);
        var guidanceLabel = Label(string.Empty, 14, FontWeights.Normal, "AccentBrush");
        var fingerprintLabel = Label(string.Empty, 13);
        stack.Children.Add(Card(summaryLabel));
        stack.Children.Add(Card(guidanceLabel));
        stack.Children.Add(Card(fingerprintLabel));
        var targets = new StackPanel();
        stack.Children.Add(Card(targets));
        var rejectedLabel = Label(string.Empty, 12, FontWeights.Normal, "MutedBrush");
        stack.Children.Add(Card(rejectedLabel));

        var profilesSection = new StackPanel();
        profilesSection.Children.Add(Label("已存车辆配置", 18, FontWeights.SemiBold));
        var profilesNote = Label(
            "车辆按车型折叠整理；展开后可管理各调校。不同 PI 必定分开，同 PI 只有在多个稳定特征持续不一致时才建立新调校。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        profilesNote.Margin = new Thickness(0, 4, 0, 12);
        profilesSection.Children.Add(profilesNote);
        var profilesPanel = new StackPanel();
        profilesSection.Children.Add(profilesPanel);
        stack.Children.Add(Card(profilesSection));

        string? targetSignature = null;
        string? profileListSignature = null;
        string? renderedActiveProfileId = null;
        var nextProfileRefreshAt = DateTimeOffset.MinValue;

        void RefreshProfiles(bool force = false)
        {
            var activeProfileId = module.ActiveVehicleProfileId;
            var now = DateTimeOffset.UtcNow;
            if (!force &&
                now < nextProfileRefreshAt &&
                string.Equals(renderedActiveProfileId, activeProfileId, StringComparison.Ordinal))
                return;
            nextProfileRefreshAt = now + TimeSpan.FromSeconds(2);
            renderedActiveProfileId = activeProfileId;
            var profiles = store.ListVehicleProfiles();
            var nextSignature = string.Join(
                "|",
                profiles.Select(profile =>
                    $"{profile.Id}:{profile.CustomName}:{profile.ShiftRecommendationsEnabled}"));
            nextSignature = $"{activeProfileId ?? "none"}|{nextSignature}";
            if (!force && string.Equals(profileListSignature, nextSignature, StringComparison.Ordinal)) return;
            profileListSignature = nextSignature;
            profilesPanel.Children.Clear();
            if (profiles.Count == 0)
            {
                var empty = Label("还没有已保存配置。完成有效加速学习后会自动出现在这里。", 13, FontWeights.Normal, "MutedBrush");
                empty.Margin = new Thickness(0, 8, 0, 4);
                profilesPanel.Children.Add(empty);
                return;
            }

            foreach (var vehicleGroup in profiles
                         .GroupBy(profile => profile.Fingerprint.CarOrdinal)
                         .OrderByDescending(group => group.Max(profile => profile.UpdatedAt)))
            {
                var orderedTunes = vehicleGroup
                    .OrderBy(profile => profile.UpdatedAt)
                    .ToArray();
                var tunePanel = new StackPanel();
                for (var index = 0; index < orderedTunes.Length; index++)
                {
                    var profile = orderedTunes[index];
                    var displayName = profile.CustomName ??
                                      (orderedTunes.Length > 1
                                          ? $"调校 {index + 1}"
                                          : "车辆配置");
                    tunePanel.Children.Add(VehicleProfileCard(
                        profile,
                        displayName,
                        module,
                        () =>
                        {
                            profileListSignature = null;
                            RefreshProfiles(true);
                        }));
                }

                profilesPanel.Children.Add(new Expander
                {
                    Header = VehicleProfileGroupHeader(
                        VehicleDisplayName(vehicleGroup.Key),
                        vehicleGroup.Key,
                        orderedTunes,
                        activeProfileId),
                    Content = tunePanel,
                    IsExpanded = false,
                    Style = (Style)FindResource("VehicleGroupExpander")
                });
            }
        }

        refreshVisiblePage = () =>
        {
            var learning = module.Learning;
            var recommendationsEnabled =
                (module.Snapshot as DashboardHudState)?.ShiftRecommendationsEnabled ?? true;
            var eta = learning.State == LearningState.Ready
                ? AppLocalization.Literal("已就绪")
                : learning.EstimatedSecondsRemaining is double seconds
                    ? AppLocalization.Format("shift.etaSeconds", "约 {0:0} 秒", Math.Ceiling(seconds))
                    : AppLocalization.Literal("等待有效加速");
            summaryLabel.Text = AppLocalization.Format(
                "shift.learningSummary",
                "{0} · 完成度 {1:P0} · 置信度 {2:P0}\n有效样本 {3} · 转速区间 {4}/{5} · 挡位 {6}\n预计：{7} · 推荐挡位{8}\n{9}",
                LearningStateText(learning.State),
                learning.Progress,
                learning.Confidence,
                learning.AcceptedSamples,
                learning.ReadyBins,
                learning.RequiredBins,
                learning.ReadyGears,
                eta,
                AppLocalization.Literal(recommendationsEnabled ? "已启用" : "已关闭"),
                AppLocalization.Literal(learning.StatusMessage));
            guidanceLabel.Text = AppLocalization.Format(
                "shift.guidance",
                "学习方法\n{0}",
                AppLocalization.Literal(learning.Guidance));
            fingerprintLabel.Text = learning.Fingerprint is { } fingerprint
                ? AppLocalization.Format(
                    "shift.currentConfiguration",
                    "当前配置：{0} · {1} {2} · 车型编号 {3} · {4} · {5} 缸 · 最高 {6:N0} RPM",
                    VehicleDisplayName(fingerprint.CarOrdinal),
                    PerformanceClassName(fingerprint.CarClass),
                    fingerprint.PerformanceIndex,
                    fingerprint.CarOrdinal,
                    DrivetrainText(fingerprint.DrivetrainType),
                    fingerprint.NumCylinders,
                    fingerprint.RoundedMaxRpm)
                : AppLocalization.Literal("等待车辆数据。");
            var nextSignature = string.Join("|", learning.Targets.Select(target => $"{target.FromGear}:{target.ToGear}:{target.TargetRpm:0}:{target.CueRpm:0}:{target.Confidence:0.000}"));
            if (!string.Equals(targetSignature, nextSignature, StringComparison.Ordinal))
            {
                targetSignature = nextSignature;
                targets.Children.Clear();
                targets.Children.Add(Label("各挡目标", 16, FontWeights.SemiBold));
                foreach (var target in learning.Targets)
                {
                    targets.Children.Add(Label(AppLocalization.Format(
                        "shift.target",
                        "{0} → {1}    目标 {2:0} RPM    提示 {3:0} RPM    换挡后 {4:0} RPM    置信度 {5:P0}{6}",
                        target.FromGear,
                        target.ToGear,
                        target.TargetRpm,
                        target.CueRpm,
                        target.AfterShiftRpm,
                        target.Confidence,
                        target.UsedLimiterFallback ? AppLocalization.Literal(" · 转速限制推算") : string.Empty), 13));
                }
                if (learning.Targets.Count == 0) targets.Children.Add(Label("数据不足：不会伪造最佳换挡点。", 13, FontWeights.Normal, "MutedBrush"));
            }
            var rejected = string.Join(" · ", learning.RejectedSamples.OrderByDescending(item => item.Value).Take(8).Select(item => $"{item.Key} {item.Value}"));
            rejectedLabel.Text = AppLocalization.Format(
                "shift.rejected",
                "未计入：{0}",
                string.IsNullOrEmpty(rejected) ? AppLocalization.Literal("暂无") : rejected);
            RefreshProfiles();
        };
        RefreshProfiles(true);
        refreshVisiblePage();
        return Scroll(stack);
    }

    private UIElement VehicleProfileGroupHeader(
        string vehicleName,
        int carOrdinal,
        IReadOnlyList<VehicleProfileSummary> profiles,
        string? activeProfileId)
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(Label(vehicleName, 16, FontWeights.SemiBold));
        var classes = string.Join(
            " · ",
            profiles
                .Select(profile =>
                    $"{PerformanceClassName(profile.Fingerprint.CarClass)} {profile.Fingerprint.PerformanceIndex}")
                .Distinct(StringComparer.Ordinal));
        var subtitle = Label(
            $"车型编号 {carOrdinal} · {classes}",
            12,
            FontWeights.Normal,
            "MutedBrush");
        subtitle.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(subtitle);
        root.Children.Add(text);

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (profiles.Any(profile =>
                string.Equals(profile.Id, activeProfileId, StringComparison.Ordinal)))
        {
            badges.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, 49, 214, 231)),
                BorderBrush = Brush("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Child = Label("当前", 10, FontWeights.SemiBold, "AccentBrush")
            });
        }
        badges.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 55)),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 3),
            Child = Label(
                profiles.Count > 1 ? $"{profiles.Count} 个调校" : "1 个配置",
                11,
                FontWeights.SemiBold,
                "MutedBrush")
        });
        Grid.SetColumn(badges, 1);
        root.Children.Add(badges);
        return root;
    }

    private Border VehicleProfileCard(
        VehicleProfileSummary profile,
        string displayName,
        DashboardModule module,
        Action refreshProfiles)
    {
        var fingerprint = profile.Fingerprint;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var details = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Label(displayName, 16, FontWeights.SemiBold));
        if (string.Equals(module.ActiveVehicleProfileId, profile.Id, StringComparison.Ordinal))
        {
            var active = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, 49, 214, 231)),
                BorderBrush = Brush("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(9, 1, 0, 0),
                Child = Label("当前", 10, FontWeights.SemiBold, "AccentBrush")
            };
            titleRow.Children.Add(active);
        }
        details.Children.Add(titleRow);

        var identity = Label(
            $"{PerformanceClassName(fingerprint.CarClass)} {fingerprint.PerformanceIndex} · 车型编号 {fingerprint.CarOrdinal} · " +
            $"{DrivetrainText(fingerprint.DrivetrainType)} · {fingerprint.NumCylinders} 缸 · 最高 {fingerprint.RoundedMaxRpm:N0} RPM",
            13,
            FontWeights.Normal,
            "TextBrush");
        identity.Margin = new Thickness(0, 5, 0, 0);
        details.Children.Add(identity);

        var legacy = !VehicleProfileIdentity.IsResolved(fingerprint);
        var learned = Label(
            legacy
                ? "旧版配置 · 需要重新学习后才能自动匹配调校"
                : $"{LearningStateText(profile.State)} · 置信度 {profile.Confidence:P0} · " +
                  $"{profile.CurveBins} 个转速区间 · {profile.Gears} 个挡位 · {profile.ShiftTargets} 个换挡目标",
            12,
            FontWeights.Normal,
            legacy ? "WarningBrush" : "MutedBrush");
        learned.Margin = new Thickness(0, 4, 0, 0);
        details.Children.Add(learned);

        var updated = Label(
            $"更新于 {profile.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            11,
            FontWeights.Normal,
            "MutedBrush");
        updated.Margin = new Thickness(0, 3, 0, 0);
        details.Children.Add(updated);
        root.Children.Add(details);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0)
        };
        var recommendation = new ToggleButton
        {
            IsChecked = profile.ShiftRecommendationsEnabled,
            Content = profile.ShiftRecommendationsEnabled ? "推荐挡位：开" : "推荐挡位：关",
            MinWidth = 112,
            Padding = new Thickness(10, 6, 10, 6)
        };
        recommendation.Click += (_, _) =>
        {
            var enabled = recommendation.IsChecked == true;
            store.SetShiftRecommendationsEnabled(profile.Id, enabled);
            module.SetShiftRecommendationsEnabled(profile.Id, enabled);
            recommendation.Content = enabled ? "推荐挡位：开" : "推荐挡位：关";
            refreshProfiles();
        };
        actions.Children.Add(recommendation);

        var rename = new Button
        {
            Content = "重命名",
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(8, 0, 0, 0)
        };
        rename.Click += (_, _) =>
        {
            var nextName = VehicleProfileNameDialog.Ask(this, displayName);
            if (string.IsNullOrWhiteSpace(nextName)) return;
            store.RenameVehicleProfile(profile.Id, nextName);
            refreshProfiles();
        };
        actions.Children.Add(rename);

        var delete = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(8, 0, 0, 0)
        };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    $"删除车辆配置“{displayName}”及其学习数据？此操作不可撤销。",
                    "确认删除车辆配置",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            store.DeleteVehicleProfile(profile.Id);
            module.ForgetVehicleProfile(profile.Id);
            refreshProfiles();
        };
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 1);
        root.Children.Add(actions);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(16, 25, 35)),
            BorderBrush = string.Equals(module.ActiveVehicleProfileId, profile.Id, StringComparison.Ordinal)
                ? Brush("AccentBrush")
                : Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = root
        };
    }

    private static string DrivetrainText(int drivetrain) => AppLocalization.Literal(drivetrain switch
    {
        0 => "前驱",
        1 => "后驱",
        2 => "四驱",
        _ => "驱动未知"
    });

    private static string VehicleDisplayName(int carOrdinal)
    {
        var displayName = VehicleNameCatalog.DisplayName(carOrdinal);
        return string.Equals(displayName, $"车辆 {carOrdinal}", StringComparison.Ordinal)
            ? AppLocalization.Format("vehicle.unknown", "车辆 {0}", carOrdinal)
            : displayName;
    }

    private async Task OfferUpdateAsync(UpdateReleaseInfo release, TextBlock? status = null)
    {
        status?.SetCurrentValue(
            TextBlock.TextProperty,
            $"发现 {release.Tag}，等待你的确认。");

        if (!updateManager.CanInstallAutomatically)
        {
            new UpdateOfferWindow(this, release, false).ShowDialog();
            status?.SetCurrentValue(
                TextBlock.TextProperty,
                $"已查看 {release.Tag}；开发构建不会自动安装。");
            return;
        }

        if (new UpdateOfferWindow(this, release, true).ShowDialog() != true)
        {
            status?.SetCurrentValue(TextBlock.TextProperty, $"已跳过 {release.Tag}。");
            return;
        }

        var progressWindow = new UpdateProgressWindow(this, release.Version.ToString(3));
        progressWindow.Show();
        try
        {
            var prepared = await updateManager.DownloadAsync(
                release,
                progressWindow.Progress,
                progressWindow.CancellationToken);
            progressWindow.Finish();
            status?.SetCurrentValue(TextBlock.TextProperty, "更新已校验，正在重启安装…");
            updateManager.InstallAndRestart(prepared);
        }
        catch (OperationCanceledException)
        {
            progressWindow.Finish();
            status?.SetCurrentValue(TextBlock.TextProperty, "已取消下载。");
        }
        catch (Exception exception)
        {
            progressWindow.Finish();
            updateManager.ReportFailure("Update download or install failed", exception);
            status?.SetCurrentValue(TextBlock.TextProperty, "更新失败，可稍后重试。");
            MessageBox.Show(
                $"更新未安装，当前版本没有被更改。\n\n{exception.Message}",
                "更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private StackPanel BuildDataProtectionSettings()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        stack.Children.Add(Label("数据备份与迁移", 22, FontWeights.SemiBold));
        var dataDescription = Label(
            "备份、迁移并恢复 LazyForza 的个人数据。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        dataDescription.Margin = new Thickness(0, 3, 0, 14);
        stack.Children.Add(dataDescription);
        var backupService = new DataBackupService(store, CurrentApplicationVersion());
        var automaticBackups = Directory.Exists(directories.BackupsPath)
            ? Directory.EnumerateFiles(directories.BackupsPath, "auto-*.lfzbackup").Count()
            : 0;

        var protectionPanel = new StackPanel();
        var protectionHeader = new Grid();
        protectionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        protectionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        protectionHeader.Children.Add(Label("自动保护", 17, FontWeights.SemiBold));
        var protectionState = Label("● 已启用", 13, FontWeights.SemiBold, "SuccessBrush");
        protectionState.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(protectionState, 1);
        protectionHeader.Children.Add(protectionState);
        protectionPanel.Children.Add(protectionHeader);
        var protectionDescription = Label(
            "数据库升级和自动更新前会先创建可恢复副本。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        protectionDescription.Margin = new Thickness(0, 4, 0, 14);
        protectionPanel.Children.Add(protectionDescription);

        var protectionSummary = new Grid();
        protectionSummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        protectionSummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        protectionSummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
        protectionSummary.Children.Add(DataSummaryItem(
            "现有备份",
            AppLocalization.Format("settings.backup.count", "{0} 份", automaticBackups),
            "SuccessBrush"));
        var retention = DataSummaryItem(
            "轮换上限",
            AppLocalization.Format(
                "settings.backup.count",
                "{0} 份",
                DataBackupService.AutomaticBackupRetention));
        Grid.SetColumn(retention, 1);
        protectionSummary.Children.Add(retention);
        var location = DataSummaryItem("保存位置", directories.BackupsPath);
        Grid.SetColumn(location, 2);
        protectionSummary.Children.Add(location);
        protectionPanel.Children.Add(protectionSummary);
        stack.Children.Add(Card(protectionPanel));

        var selectionPanel = new StackPanel();
        selectionPanel.Children.Add(DataSectionHeader(
            "导出备份",
            "选择要迁移的数据。导出完成前会校验备份内容。"));
        var selections = new UniformGrid { Columns = 2, Margin = new Thickness(-4, 10, -4, 10) };
        var settings = new CheckBox { IsChecked = true };
        var vehicles = new CheckBox { IsChecked = true };
        var laps = new CheckBox { IsChecked = true };
        var tracks = new CheckBox { IsChecked = true };
        selections.Children.Add(DataSelectionOption(settings, "配置", "应用与模块设置"));
        selections.Children.Add(DataSelectionOption(vehicles, "车辆与换挡模型", "车辆档案与学习结果"));
        selections.Children.Add(DataSelectionOption(laps, "个人圈速", "圈速、分段和遥测样本"));
        selections.Children.Add(DataSelectionOption(tracks, "自定义赛道", "个人学习和编辑的路线"));
        selectionPanel.Children.Add(selections);
        var exportStatus = Label("圈速会自动包含所需的赛道定义。", 12, FontWeights.Normal, "MutedBrush");
        exportStatus.VerticalAlignment = VerticalAlignment.Center;
        var exportButton = new Button
        {
            Content = "导出备份",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 120
        };
        exportButton.Click += async (_, _) =>
        {
            var selection = new BackupSelection(
                settings.IsChecked == true,
                vehicles.IsChecked == true,
                laps.IsChecked == true,
                tracks.IsChecked == true);
            if (!selection.HasAny)
            {
                MessageBox.Show(
                    "请至少选择一类数据。",
                    "没有可导出的内容",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出 LazyForza 数据备份",
                Filter = "LazyForza 备份 (*.lfzbackup)|*.lfzbackup",
                DefaultExt = ".lfzbackup",
                AddExtension = true,
                InitialDirectory = directories.BackupsPath,
                FileName = $"LazyForza-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.lfzbackup"
            };
            if (dialog.ShowDialog(this) != true) return;

            exportButton.IsEnabled = false;
            exportStatus.Text = "正在整理并校验备份…";
            try
            {
                var manifest = await Task.Run(
                    () => backupService.Create(
                        dialog.FileName,
                        selection,
                        lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
                exportStatus.Text =
                    $"已导出 · Schema {manifest.SchemaVersion} · 应用 {manifest.ApplicationVersion}";
                MessageBox.Show(
                    $"备份已生成并完成 SHA-256 校验。\n\n{dialog.FileName}",
                    "导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                exportStatus.Text = "导出已取消。";
            }
            catch (Exception exception)
            {
                exportStatus.Text = "导出失败。";
                MessageBox.Show(
                    exception.Message,
                    "无法导出备份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                exportButton.IsEnabled = true;
            }
        };
        var exportFooter = new Grid { Margin = new Thickness(-4, 2, 0, 0) };
        exportFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        exportFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        exportFooter.Children.Add(exportButton);
        exportStatus.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(exportStatus, 1);
        exportFooter.Children.Add(exportStatus);
        selectionPanel.Children.Add(exportFooter);
        var exportCard = Card(selectionPanel);

        var importPanel = new StackPanel();
        importPanel.Children.Add(DataSectionHeader(
            "导入备份",
            "先校验文件并预览冲突，再决定是否导入。"));
        var importModeLabel = Label("导入方式", 13, FontWeights.SemiBold);
        importModeLabel.Margin = new Thickness(0, 12, 0, 6);
        importPanel.Children.Add(importModeLabel);
        var modePanel = new UniformGrid { Columns = 2, Margin = new Thickness(-4, 0, -4, 10) };
        var merge = new RadioButton
        {
            IsChecked = true,
            GroupName = "BackupImportMode"
        };
        var overwrite = new RadioButton
        {
            GroupName = "BackupImportMode"
        };
        modePanel.Children.Add(DataSelectionOption(merge, "合并", "保留本机同名记录"));
        modePanel.Children.Add(DataSelectionOption(overwrite, "覆盖冲突", "只替换发生冲突的数据"));
        importPanel.Children.Add(modePanel);
        var previewTitle = Label("备份预览", 13, FontWeights.SemiBold);
        previewTitle.Margin = new Thickness(0, 0, 0, 6);
        importPanel.Children.Add(previewTitle);
        var previewLabel = Label(
            "尚未选择备份文件。选择文件后会显示版本、内容数量和冲突。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        previewLabel.VerticalAlignment = VerticalAlignment.Center;
        var previewContainer = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            MinHeight = 78,
            Padding = new Thickness(12, 10, 12, 10),
            Child = previewLabel
        };
        importPanel.Children.Add(previewContainer);
        var importControls = new WrapPanel { Margin = new Thickness(-4, 8, 0, 0) };
        var inspectButton = new Button { Content = "选择备份文件", MinWidth = 140 };
        var importButton = new Button
        {
            Content = "开始导入",
            MinWidth = 110,
            IsEnabled = false
        };
        string? selectedBackup = null;
        BackupPreview? preview = null;
        inspectButton.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 LazyForza 数据备份",
                Filter = "LazyForza 备份 (*.lfzbackup)|*.lfzbackup",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = directories.BackupsPath
            };
            if (dialog.ShowDialog(this) != true) return;

            inspectButton.IsEnabled = false;
            importButton.IsEnabled = false;
            previewLabel.Text = "正在校验清单和文件哈希…";
            try
            {
                preview = await Task.Run(
                    () => backupService.Preview(
                        dialog.FileName,
                        lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
                selectedBackup = dialog.FileName;
                var conflictLines = preview.Conflicts.Count == 0
                    ? "没有发现冲突。"
                    : string.Join(
                        "\n",
                        preview.Conflicts.Take(8).Select(conflict =>
                            $"• {conflict.Category}：{conflict.SourceSummary}"));
                if (preview.Conflicts.Count > 8)
                    conflictLines += $"\n• 另有 {preview.Conflicts.Count - 8} 项冲突";
                var warnings = preview.Warnings.Count == 0
                    ? string.Empty
                    : "\n\n" + string.Join("\n", preview.Warnings);
                previewLabel.Text =
                    $"创建时间：{preview.Manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                    $"来源版本：{preview.Manifest.ApplicationVersion} · Schema {preview.Manifest.SchemaVersion}\n" +
                    $"配置 {preview.Settings} · 车辆 {preview.Vehicles} · 圈速 {preview.Laps} · 自定义赛道 {preview.CustomTracks}\n" +
                    $"冲突 {preview.Conflicts.Count} 项\n\n{conflictLines}{warnings}";
                importButton.IsEnabled = true;
            }
            catch (Exception exception)
            {
                selectedBackup = null;
                preview = null;
                previewLabel.Text = "备份校验失败。";
                MessageBox.Show(
                    exception.Message,
                    "无法读取备份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                inspectButton.IsEnabled = true;
            }
        };
        importButton.Click += async (_, _) =>
        {
            if (selectedBackup is null || preview is null) return;
            var mode = overwrite.IsChecked == true
                ? BackupImportMode.Overwrite
                : BackupImportMode.Merge;
            if (mode == BackupImportMode.Overwrite &&
                preview.Conflicts.Count > 0 &&
                MessageBox.Show(
                    $"将覆盖 {preview.Conflicts.Count} 项冲突数据。继续前会保留现有自动备份，是否继续？",
                    "确认覆盖冲突",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            importButton.IsEnabled = false;
            inspectButton.IsEnabled = false;
            previewLabel.Text += "\n\n正在导入…";
            try
            {
                var result = await Task.Run(
                    () =>
                    {
                        backupService.CreateAutomaticBackup(
                            directories.BackupsPath,
                            "import",
                            lifetimeCancellation.Token);
                        return backupService.Import(
                            selectedBackup,
                            mode,
                            lifetimeCancellation.Token);
                    },
                    lifetimeCancellation.Token);
                previewLabel.Text +=
                    $"\n完成：配置 {result.ImportedSettings} · 车辆 {result.ImportedVehicles} · " +
                    $"圈速 {result.ImportedLaps} · 自定义赛道 {result.ImportedCustomTracks}。";
                MessageBox.Show(
                    "数据导入完成。为使后台模块重新载入赛道与车辆缓存，请重启 LazyForza。",
                    "导入完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                previewLabel.Text += "\n导入失败，数据库事务已回滚。";
                MessageBox.Show(
                    exception.Message,
                    "无法导入备份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                importButton.IsEnabled = true;
                inspectButton.IsEnabled = true;
            }
        };
        importControls.Children.Add(inspectButton);
        importControls.Children.Add(importButton);
        importPanel.Children.Add(importControls);
        var importCard = Card(importPanel);

        var operations = new Grid();
        operations.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        operations.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        operations.Children.Add(exportCard);
        Grid.SetColumn(importCard, 1);
        operations.Children.Add(importCard);
        stack.Children.Add(operations);
        return stack;
    }

    private UIElement SettingsPage()
    {
        var stack = PageStack(
            "设置",
            SettingsCategoryDescription(selectedSettingsCategory));
        stack.Children.Add(BuildSettingsCategoryNavigation());
        var generalSettings = new StackPanel();
        var telemetrySettings = new StackPanel();
        var hudSettings = new StackPanel();
        var maintenanceSettings = new StackPanel();
        var identity = new Grid();
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identityText = new StackPanel();
        identityText.Children.Add(Label("玩家代号", 17, FontWeights.SemiBold));
        identityText.Children.Add(Label(
            "用于新录制和圈速分享；首次加入地产赛事时作为代表名默认值。可留空，不覆盖上次参赛代表名。",
            11,
            FontWeights.Normal,
            "MutedBrush"));
        identity.Children.Add(identityText);
        var playerCode = new TextBox
        {
            Text = PlayerIdentitySettings.Normalize(
                store.GetAppSetting(PlayerIdentitySettings.PlayerCodeSettingKey)),
            MaxLength = PlayerIdentitySettings.MaximumLength,
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(18, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = $"最多 {PlayerIdentitySettings.MaximumLength} 个字符；留空表示不标注"
        };
        Grid.SetColumn(playerCode, 1);
        identity.Children.Add(playerCode);
        var saveIdentity = new Button
        {
            Content = "保存",
            Padding = new Thickness(16, 7, 16, 7),
            VerticalAlignment = VerticalAlignment.Center
        };
        saveIdentity.Click += (_, _) =>
        {
            var normalized = PlayerIdentitySettings.Normalize(playerCode.Text);
            playerCode.Text = normalized;
            store.SetAppSetting(PlayerIdentitySettings.PlayerCodeSettingKey, normalized);
            saveIdentity.Content = "已保存";
        };
        playerCode.TextChanged += (_, _) => saveIdentity.Content = "保存";
        Grid.SetColumn(saveIdentity, 2);
        identity.Children.Add(saveIdentity);
        generalSettings.Children.Add(Card(identity));

        generalSettings.Children.Add(BuildStartupSettingsCard());

        var network = new Grid();
        network.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        network.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        var address = new TextBox { Text = store.GetAppSetting("telemetry.listenAddress") ?? LazyForzaDefaults.TelemetryListenAddress, Padding = new Thickness(8) };
        var port = new TextBox { Text = store.GetAppSetting("telemetry.port") ?? LazyForzaDefaults.TelemetryPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Padding = new Thickness(8) };
        AddSettingRow("监听地址", address, 0);
        AddSettingRow("UDP 端口", port, 1);
        network.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var networkNote = Label("默认仅监听本机；端口范围 1–65535，请避开 5200–5300。胎温显示为 °C。", 12, FontWeights.Normal, "MutedBrush");
        networkNote.Margin = new Thickness(0, 8, 0, 8);
        Grid.SetRow(networkNote, 2);
        Grid.SetColumnSpan(networkNote, 2);
        network.Children.Add(networkNote);
        var saveNetwork = new Button { Content = "保存监听设置", HorizontalAlignment = HorizontalAlignment.Left };
        saveNetwork.Click += (_, _) =>
        {
            if (!IPAddress.TryParse(address.Text.Trim(), out _) ||
                !int.TryParse(port.Text.Trim(), out var parsedPort) || parsedPort is < 1 or > 65535 || parsedPort is >= 5200 and <= 5300)
            {
                MessageBox.Show("请输入有效 IP 地址和 1–65535 端口，并避开 5200–5300。", "监听设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            store.SetAppSetting("telemetry.listenAddress", address.Text.Trim());
            store.SetAppSetting("telemetry.port", parsedPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            MessageBox.Show("监听设置已保存，重启后生效。", "设置已保存", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Grid.SetRow(saveNetwork, 3);
        Grid.SetColumnSpan(saveNetwork, 2);
        network.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        network.Children.Add(saveNetwork);
        telemetrySettings.Children.Add(Card(network));

        var current = overlay.CurrentLayout;
        var currentDashboardBounds = OverlayLayoutGeometry.Bounds(
            current,
            OverlayHudKind.Dashboard);
        var currentLapBounds = OverlayLayoutGeometry.Bounds(
            current,
            OverlayHudKind.Lap);
        var currentDriftBounds = OverlayLayoutGeometry.Bounds(
            current,
            OverlayHudKind.Drift);
        var currentDashboardWidgets = DashboardWidgetLayoutSettings.Normalize(
            current.DashboardWidgets);
        var currentEstateRaceWidgets = EstateRaceHudLayoutSettings.Normalize(
            current.EstateRaceWidgets);
        var dashboardWidgetKinds = Enum.GetValues<DashboardWidgetKind>();
        var estateRaceWidgetKinds = Enum.GetValues<EstateRaceHudWidgetKind>();
        var visibleDashboardWidgetCount = dashboardWidgetKinds
            .Count(kind => currentDashboardWidgets.Get(kind).IsVisible);
        var visibleEstateRaceWidgetCount = estateRaceWidgetKinds
            .Count(kind => currentEstateRaceWidgets.Get(kind).IsVisible);
        var controls = new StackPanel();
        var hudAppearance = new StackPanel();
        var hudComponents = new StackPanel();
        var hudOpacity = new StackPanel();
        var hudTiming = new StackPanel();

        var overlayHeader = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        overlayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new StackPanel();
        headerText.Children.Add(Label("Overlay 设置", 17, FontWeights.SemiBold));
        var overlaySummary = Label(
            AppLocalization.Format(
                "settings.overlay.summary",
                "仪表盘 {0:0} × {1:0} · 圈速 {2:0} × {3:0} · 漂移 {4:0} × {5:0} · 仪表盘部件 {6}/{7} · 赛事部件 {8}/{9} · {10} · 不透明度 {11:P0} · {12}",
                currentDashboardBounds.Width,
                currentDashboardBounds.Height,
                currentLapBounds.Width,
                currentLapBounds.Height,
                currentDriftBounds.Width,
                currentDriftBounds.Height,
                visibleDashboardWidgetCount,
                dashboardWidgetKinds.Length,
                visibleEstateRaceWidgetCount,
                estateRaceWidgetKinds.Length,
                AppLocalization.Literal(current.LapHudAttachedToDashboard ? "已吸附" : "独立布局"),
                current.Opacity,
                current.MonitorId),
            11, FontWeights.Normal, "MutedBrush");
        overlaySummary.Margin = new Thickness(0, 3, 0, 0);
        headerText.Children.Add(overlaySummary);
        var interactionNote = Label(
            "运行时始终点击穿透且不可移动；位置与大小只在布局编辑器中调整。",
            10,
            FontWeights.Normal,
            "MutedBrush");
        interactionNote.Margin = new Thickness(0, 3, 0, 0);
        headerText.Children.Add(interactionNote);
        overlayHeader.Children.Add(headerText);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        var editLayout = new Button
        {
            Content = "设置 Overlay 布局",
            Padding = new Thickness(13, 7, 13, 7),
            ToolTip = "以 Forza Horizon 6 当前窗口为背景，拖动并缩放 Overlay"
        };
        editLayout.Click += async (_, _) =>
            await ConfigureOverlayLayoutAsync(editLayout);
        headerActions.Children.Add(editLayout);
        var resetDefaults = new Button
        {
            Content = "重置 Overlay",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(12, 7, 12, 7),
            ToolTip = "恢复 Overlay、仪表盘部件与地产赛事部件的默认布局；不修改监听 IP 和 UDP 端口"
        };
        resetDefaults.Click += async (_, _) =>
        {
            if (MessageBox.Show(
                    "确定重置 Overlay 设置吗？\n\n位置、尺寸、仪表盘部件、地产赛事部件、透明度、动态和时间参数将恢复默认值。监听 IP、UDP 端口与本地数据不受影响。",
                    "重置 Overlay",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var defaultLayout = LazyForzaDefaults.CreateOverlayLayout();
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(defaultLayout));
            await overlay.SetLayoutAsync(defaultLayout, CancellationToken.None);
            RenderSelectedPage();
        };
        headerActions.Children.Add(resetDefaults);
        Grid.SetColumn(headerActions, 1);
        overlayHeader.Children.Add(headerActions);
        hudAppearance.Children.Add(overlayHeader);

        var primarySettings = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        primarySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        primarySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        primarySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var appearance = new StackPanel();
        var dashboardScale = AddValueSlider(
            appearance, "仪表盘 HUD 精确缩放", "按 1% 调整，并保持仪表盘中心点不变", current.Scale,
            OverlayScaleSettings.Minimum,
            OverlayScaleSettings.Maximum,
            OverlayScaleSettings.Step,
            value => value.ToString("P0"));
        dashboardScale.SmallChange = OverlayScaleSettings.Step;
        dashboardScale.LargeChange = 0.05;
        var lapScale = AddValueSlider(
            appearance, "圈速 HUD 精确缩放", "按 1% 调整，并保持圈速 HUD 中心点不变",
            current.LapHudScale ?? current.Scale,
            OverlayScaleSettings.Minimum,
            OverlayScaleSettings.Maximum,
            OverlayScaleSettings.Step,
            value => value.ToString("P0"));
        lapScale.SmallChange = OverlayScaleSettings.Step;
        lapScale.LargeChange = 0.05;
        var driftScale = AddValueSlider(
            appearance,
            "漂移 HUD 精确缩放 · 实验性",
            "按 1% 调整，并保持漂移 HUD 中心点不变",
            current.DriftHudScale ?? current.Scale,
            OverlayScaleSettings.Minimum,
            OverlayScaleSettings.Maximum,
            OverlayScaleSettings.Step,
            value => value.ToString("P0"));
        driftScale.SmallChange = OverlayScaleSettings.Step;
        driftScale.LargeChange = 0.05;
        var opacity = AddValueSlider(
            appearance, "不透明度", "HUD 内容的最高可见度", current.Opacity,
            0.25, 1, 0.05, value => value.ToString("P0"));
        var monitor = Label(
            AppLocalization.Format("settings.overlay.monitor", "当前显示器：{0}", current.MonitorId),
            10,
            FontWeights.Normal,
            "MutedBrush");
        monitor.Margin = new Thickness(0, 0, 0, 4);
        appearance.Children.Add(monitor);
        primarySettings.Children.Add(SettingGroup(
            "外观",
            "分别调整三个主 HUD；地产赛事部件在可视化布局编辑器中独立缩放。",
            appearance));

        var interaction = new StackPanel();
        var toggleRow = new WrapPanel { Margin = new Thickness(-4, -2, 0, 8) };
        var reduceMotion = new ToggleButton
        {
            Content = current.ReduceMotion ? "减少动态：开" : "减少动态：关",
            IsChecked = current.ReduceMotion
        };
        var dashboardMotion = new ToggleButton
        {
            Content = current.DashboardMotionEnabled ? "加速度跟随：开" : "加速度跟随：关",
            IsChecked = current.DashboardMotionEnabled,
            ToolTip = "让仪表盘随车辆加速度轻微移动"
        };
        toggleRow.Children.Add(reduceMotion);
        toggleRow.Children.Add(dashboardMotion);
        interaction.Children.Add(toggleRow);
        var motionIntensity = AddValueSlider(
            interaction, "动态强度", "控制加速度跟随的位移幅度",
            current.DashboardMotionIntensity, 0, 1, 0.05, value => value.ToString("P0"));

        void RefreshInteractionControls()
        {
            reduceMotion.Content = reduceMotion.IsChecked == true ? "减少动态：开" : "减少动态：关";
            dashboardMotion.Content = dashboardMotion.IsChecked == true ? "加速度跟随：开" : "加速度跟随：关";
            motionIntensity.IsEnabled = dashboardMotion.IsChecked == true && reduceMotion.IsChecked != true;
        }

        reduceMotion.Click += (_, _) => RefreshInteractionControls();
        dashboardMotion.Click += (_, _) =>
        {
            RefreshInteractionControls();
        };
        RefreshInteractionControls();
        var interactionGroup = SettingGroup(
            "动态效果",
            "控制动画和仪表盘加速度跟随。",
            interaction);
        Grid.SetColumn(interactionGroup, 2);
        primarySettings.Children.Add(interactionGroup);
        hudAppearance.Children.Add(primarySettings);

        var dashboardComponentItems = new[]
        {
            (DashboardWidgetKind.RpmArc, "转速灯带"),
            (DashboardWidgetKind.SpeedGear, "速度 / 挡位"),
            (DashboardWidgetKind.EngineOutput, "转速 / 动力"),
            (DashboardWidgetKind.Tires, "轮胎状态"),
            (DashboardWidgetKind.Pedals, "油门 / 制动"),
            (DashboardWidgetKind.Steering, "方向指示"),
            (DashboardWidgetKind.ClassBadge, "等级 / PI")
        };
        var componentToggles = new Dictionary<DashboardWidgetKind, ToggleButton>();
        var componentPanel = new WrapPanel { Margin = new Thickness(-4, -2, 0, 0) };
        foreach (var (kind, name) in dashboardComponentItems)
        {
            var toggle = new ToggleButton
            {
                IsChecked = currentDashboardWidgets.Get(kind).IsVisible,
                Margin = new Thickness(4, 2, 4, 6),
                Padding = new Thickness(10, 6, 10, 6),
                MinWidth = 106
            };
            void RefreshComponentToggle() => toggle.Content =
                $"{name}：{(toggle.IsChecked == true ? "开" : "关")}";
            toggle.Click += (_, _) => RefreshComponentToggle();
            RefreshComponentToggle();
            componentToggles[kind] = toggle;
            componentPanel.Children.Add(toggle);
        }
        hudComponents.Children.Add(SettingGroup(
            "主仪表盘部件",
            "各部件可独立开关；位置请在 Overlay 布局编辑器中拖动，并可一键恢复当前默认布局。",
            componentPanel));

        var estateRaceComponentItems = new[]
        {
            (EstateRaceHudWidgetKind.Leaderboard, "比赛排行榜"),
            (EstateRaceHudWidgetKind.TrackMap, "赛道一览"),
            (EstateRaceHudWidgetKind.GripStatus, "抓地提示"),
            (EstateRaceHudWidgetKind.Banner, "赛事横幅"),
            (EstateRaceHudWidgetKind.StartLights, "五盏红灯"),
            (EstateRaceHudWidgetKind.PitStopInfo, "维修站信息"),
            (EstateRaceHudWidgetKind.PitLimiter, "维修区限速"),
            (EstateRaceHudWidgetKind.PenaltyStatus, "罚时指示器"),
            (EstateRaceHudWidgetKind.PracticeProgram, "练习项目提示"),
            (EstateRaceHudWidgetKind.PitWindowSuggestion, "进站窗口建议"),
            (EstateRaceHudWidgetKind.FullRaceStrategy, "整场进站策略")
        };
        var estateRaceToggles = new Dictionary<EstateRaceHudWidgetKind, ToggleButton>();
        var estateRaceComponentPanel = new WrapPanel { Margin = new Thickness(-4, -2, 0, 0) };
        foreach (var (kind, name) in estateRaceComponentItems)
        {
            var toggle = new ToggleButton
            {
                IsChecked = currentEstateRaceWidgets.Get(kind).IsVisible,
                Margin = new Thickness(4, 2, 4, 6),
                Padding = new Thickness(10, 6, 10, 6),
                MinWidth = 116
            };
            void RefreshEstateRaceToggle() => toggle.Content =
                $"{name}：{(toggle.IsChecked == true ? "开" : "关")}";
            toggle.Click += (_, _) => RefreshEstateRaceToggle();
            RefreshEstateRaceToggle();
            estateRaceToggles[kind] = toggle;
            estateRaceComponentPanel.Children.Add(toggle);
        }
        hudComponents.Children.Add(SettingGroup(
            "地产赛事 HUD 部件",
            "十一个部件可独立开关；进入 Overlay 布局编辑器后可直接选择、拖动、缩放或恢复赛事默认布局。",
            estateRaceComponentPanel));
        var estateRaceOpacitySliders = new Dictionary<EstateRaceHudWidgetKind, Slider>();
        var estateRaceOpacityPanel = new UniformGrid { Columns = 2 };
        foreach (var (kind, name) in estateRaceComponentItems)
        {
            var opacityItem = new StackPanel { Margin = new Thickness(6, 3, 14, 4) };
            estateRaceOpacitySliders[kind] = AddValueSlider(
                opacityItem,
                name,
                "只调整这个部件，不影响其他 HUD。",
                currentEstateRaceWidgets.Get(kind).Opacity,
                0.15,
                1,
                0.05,
                value => $"{value:P0}");
            estateRaceOpacityPanel.Children.Add(opacityItem);
        }
        hudOpacity.Children.Add(SettingGroup(
            "地产赛事 HUD 透明度",
            "十一个赛事部件分别保存透明度，互不影响。",
            estateRaceOpacityPanel));

        var timingItems = new UniformGrid { Columns = 2 };
        var dashboardIdleWait = AddTimeSlider(timingItems, "仪表盘静止等待", current.DashboardIdleWaitSeconds, 0, 15, 0.5);
        var dashboardFade = AddTimeSlider(timingItems, "仪表盘淡入 / 淡出", current.DashboardVisibilityFadeSeconds, 0.1, 3, 0.1);
        var completedLapHold = AddTimeSlider(timingItems, "完成圈分段保留", current.LapCompletedHoldSeconds, 0, 10, 0.5);
        var noMatchConfirmation = AddTimeSlider(timingItems, "无匹配赛道确认", current.LapNoMatchConfirmationSeconds, 1, 30, 0.5);
        var noMatchFade = AddTimeSlider(timingItems, "无匹配圈速 HUD 淡出", current.LapNoMatchFadeSeconds, 0.1, 3, 0.1);
        var liveHudStale = AddTimeSlider(timingItems, "Live HUD 断流隐藏", current.LiveHudStaleSeconds, 0.1, 3, 0.1);
        hudTiming.Children.Add(SettingGroup(
            "HUD 时间",
            "调整 HUD 的等待、保留和淡入淡出时间。",
            timingItems));

        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.appearance", "外观与动态"),
            AppLocalization.Text(
                "settings.hud.appearanceDetail",
                "布局、缩放、不透明度和动态效果。"),
            hudAppearance,
            hudAppearanceExpanded,
            expanded => hudAppearanceExpanded = expanded));
        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.components", "部件显示"),
            AppLocalization.Text(
                "settings.hud.componentsDetail",
                "管理仪表盘与地产赛事 HUD 的显示内容。"),
            hudComponents,
            hudComponentsExpanded,
            expanded => hudComponentsExpanded = expanded));
        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.opacity", "赛事部件透明度"),
            AppLocalization.Text(
                "settings.hud.opacityDetail",
                "分别调整每个地产赛事部件的可见度。"),
            hudOpacity,
            hudOpacityExpanded,
            expanded => hudOpacityExpanded = expanded));
        controls.Children.Add(SettingsSectionExpander(
            AppLocalization.Text("settings.hud.timing", "显示时序"),
            AppLocalization.Text(
                "settings.hud.timingDetail",
                "调整 HUD 的等待、保留和淡入淡出时间。"),
            hudTiming,
            hudTimingExpanded,
            expanded => hudTimingExpanded = expanded));

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var saveHint = Label("点击应用后立即生效。", 11, FontWeights.Normal, "MutedBrush");
        saveHint.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(saveHint);
        var saveOverlay = new Button
        {
            Content = "应用 HUD 设置",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 7, 16, 7)
        };
        saveOverlay.Click += async (_, _) =>
        {
            var dashboardWidgets = DashboardWidgetLayoutSettings.Normalize(
                overlay.CurrentLayout.DashboardWidgets);
            foreach (var (kind, toggle) in componentToggles)
            {
                var placement = dashboardWidgets.Get(kind);
                dashboardWidgets = dashboardWidgets.Set(
                    kind,
                    placement with { IsVisible = toggle.IsChecked == true });
            }
            var estateRaceWidgets = EstateRaceHudLayoutSettings.Normalize(
                overlay.CurrentLayout.EstateRaceWidgets);
            foreach (var (kind, toggle) in estateRaceToggles)
            {
                var placement = estateRaceWidgets.Get(kind);
                estateRaceWidgets = estateRaceWidgets.Set(
                    kind,
                    placement with
                    {
                        IsVisible = toggle.IsChecked == true,
                        Opacity = estateRaceOpacitySliders[kind].Value
                    });
            }
            var next = overlay.CurrentLayout with
            {
                Opacity = opacity.Value,
                ClickThrough = true,
                IsLocked = true,
                ReduceMotion = reduceMotion.IsChecked == true,
                DashboardMotionEnabled = dashboardMotion.IsChecked == true,
                DashboardMotionIntensity = motionIntensity.Value,
                DashboardIdleWaitSeconds = dashboardIdleWait.Value,
                DashboardVisibilityFadeSeconds = dashboardFade.Value,
                LapCompletedHoldSeconds = completedLapHold.Value,
                LapNoMatchConfirmationSeconds = noMatchConfirmation.Value,
                LapNoMatchFadeSeconds = noMatchFade.Value,
                LiveHudStaleSeconds = liveHudStale.Value,
                DashboardWidgets = dashboardWidgets,
                EstateRaceWidgets = estateRaceWidgets
            };
            next = OverlayLayoutGeometry.ScaleAroundCenter(
                next,
                OverlayHudKind.Dashboard,
                dashboardScale.Value);
            next = OverlayLayoutGeometry.ScaleAroundCenter(
                next,
                OverlayHudKind.Lap,
                lapScale.Value);
            next = OverlayLayoutGeometry.ScaleAroundCenter(
                next,
                OverlayHudKind.Drift,
                driftScale.Value);
            if (current.LapHudAttachedToDashboard &&
                Math.Abs(dashboardScale.Value - lapScale.Value) <
                OverlayScaleSettings.Step / 2)
                next = OverlayLayoutGeometry.AttachLapToDashboard(next);
            store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(next));
            await overlay.SetLayoutAsync(next, CancellationToken.None);
            RenderSelectedPage();
        };
        Grid.SetColumn(saveOverlay, 1);
        footer.Children.Add(saveOverlay);
        controls.Children.Add(new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 10, 4),
            Child = footer
        });
        hudSettings.Children.Add(controls);

        var interfacePanel = new StackPanel();
        interfacePanel.Children.Add(Label("界面与诊断", 17, FontWeights.SemiBold));
        var diagnosticsNavigation = new ToggleButton
        {
            IsChecked = showDiagnosticsNavigation,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 7, 12, 7),
            ToolTip = "诊断页用于排查 UDP、赛道识别、模块与本地文件问题；日常使用可以保持隐藏。"
        };
        void RefreshDiagnosticsNavigationText() => diagnosticsNavigation.Content = AppLocalization.Format(
            "settings.diagnostics.navigation",
            "在侧边栏显示诊断：{0}",
            AppLocalization.Literal(diagnosticsNavigation.IsChecked == true ? "开" : "关"));
        diagnosticsNavigation.Click += (_, _) =>
        {
            showDiagnosticsNavigation = diagnosticsNavigation.IsChecked == true;
            store.SetAppSetting(
                ShowDiagnosticsNavigationSetting,
                showDiagnosticsNavigation.ToString());
            PopulateNavigation();
            navigation.SelectedIndex = 8;
            RefreshDiagnosticsNavigationText();
        };
        RefreshDiagnosticsNavigationText();
        interfacePanel.Children.Add(diagnosticsNavigation);
        var diagnosticsHelp = Label(
            "默认隐藏诊断入口。开启后，侧边栏会增加“诊断”；关闭不会删除诊断记录或影响一键诊断包。",
            11,
            FontWeights.Normal,
            "MutedBrush");
        diagnosticsHelp.Margin = new Thickness(0, 8, 0, 0);
        interfacePanel.Children.Add(diagnosticsHelp);
        maintenanceSettings.Children.Add(Card(interfacePanel));

        telemetrySettings.Children.Add(BuildRecordingSettingsCard());
        maintenanceSettings.Children.Add(BuildUpdateSettingsCard());
        maintenanceSettings.Children.Add(BuildDataProtectionSettings());

        stack.Children.Add(selectedSettingsCategory switch
        {
            SettingsCategory.Telemetry => telemetrySettings,
            SettingsCategory.Hud => hudSettings,
            SettingsCategory.Maintenance => maintenanceSettings,
            _ => generalSettings
        });

        return Scroll(stack);

        Slider AddValueSlider(
            Panel parent,
            string title,
            string description,
            double value,
            double minimum,
            double maximum,
            double tick,
            Func<double, string> format)
        {
            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(Label(title, 12, FontWeights.SemiBold));
            var valueLabel = Label(format(Math.Clamp(value, minimum, maximum)), 12, FontWeights.SemiBold, "AccentBrush");
            valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(valueLabel, 1);
            heading.Children.Add(valueLabel);
            container.Children.Add(heading);
            if (!string.IsNullOrWhiteSpace(description))
            {
                var help = Label(description, 10, FontWeights.Normal, "MutedBrush");
                help.Margin = new Thickness(0, 2, 0, 5);
                container.Children.Add(help);
            }
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = tick,
                IsSnapToTickEnabled = true,
                Value = Math.Clamp(value, minimum, maximum),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            slider.ValueChanged += (_, _) => valueLabel.Text = format(slider.Value);
            container.Children.Add(slider);
            parent.Children.Add(container);
            return slider;
        }

        Slider AddTimeSlider(
            Panel parent,
            string title,
            double value,
            double minimum,
            double maximum,
            double tick)
        {
            var item = new StackPanel { Margin = new Thickness(6, 3, 14, 12) };
            var slider = AddValueSlider(
                item, title, "", value, minimum, maximum, tick,
                currentValue => AppLocalization.Format("settings.seconds", "{0:0.0} 秒", currentValue));
            parent.Children.Add(item);
            return slider;
        }

        Border SettingGroup(string title, string description, UIElement body)
        {
            var group = new StackPanel();
            group.Children.Add(Label(title, 14, FontWeights.SemiBold));
            var help = Label(description, 10, FontWeights.Normal, "MutedBrush");
            help.Margin = new Thickness(0, 3, 0, 12);
            group.Children.Add(help);
            group.Children.Add(body);
            return new Border
            {
                Background = Brush("PanelBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Child = group
            };
        }

        void AddSettingRow(string title, Control editor, int row)
        {
            network.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Label(title, 13, FontWeights.SemiBold);
            label.Margin = new Thickness(0, 7, 12, 7);
            editor.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(label, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            network.Children.Add(label);
            network.Children.Add(editor);
        }
    }

    private string SettingsCategoryDescription(SettingsCategory category) => category switch
    {
        SettingsCategory.Telemetry => AppLocalization.Text(
            "settings.category.telemetry.pageDetail",
            "配置 FH6 遥测监听、比赛自动录制与录制文件管理。"),
        SettingsCategory.Hud => AppLocalization.Text(
            "settings.category.hud.pageDetail",
            "调整 Overlay 布局、显示部件、透明度、动态与显示时序。"),
        SettingsCategory.Maintenance => AppLocalization.Text(
            "settings.category.maintenance.pageDetail",
            "管理应用更新、数据备份迁移与诊断入口。"),
        _ => AppLocalization.Text(
            "settings.category.general.pageDetail",
            "管理玩家代号、界面语言、数据目录和窗口关闭行为。")
    };

    private Border BuildSettingsCategoryNavigation()
    {
        var categories = new[]
        {
            (SettingsCategory.General,
                AppLocalization.Text("settings.category.general", "常规")),
            (SettingsCategory.Telemetry,
                AppLocalization.Text("settings.category.telemetryTab", "遥测与录制")),
            (SettingsCategory.Hud,
                "HUD"),
            (SettingsCategory.Maintenance,
                AppLocalization.Text("settings.category.maintenance", "维护"))
        };
        var tabs = new UniformGrid { Columns = categories.Length };
        foreach (var (category, title) in categories)
        {
            var tab = new ToggleButton
            {
                Content = title,
                IsChecked = category == selectedSettingsCategory,
                Style = (Style)FindResource("SettingsCategoryTab"),
                Tag = category
            };
            tab.Click += (_, _) =>
            {
                selectedSettingsCategory = (SettingsCategory)tab.Tag;
                RenderSelectedPage();
            };
            tabs.Children.Add(tab);
        }

        return new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 7, 10, 11),
            Child = tabs
        };
    }

    private Expander SettingsSectionExpander(
        string title,
        string description,
        UIElement content,
        bool isExpanded,
        Action<bool> updateState)
    {
        var header = new StackPanel();
        header.Children.Add(Label(title, 15, FontWeights.SemiBold));
        var detail = Label(description, 10, FontWeights.Normal, "MutedBrush");
        detail.Margin = new Thickness(0, 3, 20, 0);
        header.Children.Add(detail);
        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = isExpanded,
            Style = (Style)FindResource("SettingsSectionExpander")
        };
        expander.Expanded += (_, _) => updateState(true);
        expander.Collapsed += (_, _) => updateState(false);
        return expander;
    }

    private static string CurrentApplicationVersion()
        => ApplicationVersionInfo.Display;

    private UIElement DiagnosticsPage()
    {
        var stack = PageStack("诊断", "查看 UDP、原始帧、模块状态和本地文件。");
        var diagnosticsLabel = Label(string.Empty, 14);
        var rawLabel = Label(string.Empty, 13);
        stack.Children.Add(Card(diagnosticsLabel));
        stack.Children.Add(Card(rawLabel));
        var packagePanel = new StackPanel();
        packagePanel.Children.Add(Label("一键诊断包", 17, FontWeights.SemiBold));
        var packageStatus = Label(
            "短时内存缓冲只保留必要遥测字段；导出时会移除用户名和本地目录。",
            12,
            FontWeights.Normal,
            "MutedBrush");
        packagePanel.Children.Add(packageStatus);
        var exportPackage = new Button
        {
            Content = "导出诊断包",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 130,
            Margin = new Thickness(0, 10, 0, 0)
        };
        exportPackage.Click += async (_, _) =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出 LazyForza 诊断包",
                Filter = "LazyForza 诊断包 (*.lfzdiag)|*.lfzdiag",
                DefaultExt = ".lfzdiag",
                AddExtension = true,
                InitialDirectory = directories.DiagnosticsPath,
                FileName = $"LazyForza-diagnostic-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.lfzdiag"
            };
            if (dialog.ShowDialog(this) != true) return;
            exportPackage.IsEnabled = false;
            packageStatus.Text = "正在整理遥测、状态事件和赛道候选评分…";
            try
            {
                var path = await Task.Run(
                    () => diagnosticCapture.Export(
                        dialog.FileName,
                        store.SchemaVersion,
                        CurrentApplicationVersion(),
                        lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
                packageStatus.Text =
                    $"已导出：{Path.GetFileName(path)}\n" +
                    $"当前缓冲 {diagnosticCapture.BufferedSamples} 帧 · " +
                    $"事件 {diagnosticCapture.EventCount} 条 · 异常快照 {diagnosticCapture.AnomalyCount} 个";
                MessageBox.Show(
                    $"诊断包已生成。\n\n{path}",
                    "导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                packageStatus.Text = "诊断包导出失败。";
                MessageBox.Show(
                    exception.Message,
                    "无法导出诊断包",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                exportPackage.IsEnabled = true;
            }
        };
        packagePanel.Children.Add(exportPackage);
        stack.Children.Add(Card(packagePanel));
        var lapAnalysisModule = moduleManager.Modules.OfType<LapAnalysisModule>().FirstOrDefault();
        var trackMatchLabel = Label(string.Empty, 13);
        Button? trackCorrection = null;
        if (lapAnalysisModule is not null)
        {
            var trackMatchPanel = new StackPanel();
            trackMatchPanel.Children.Add(trackMatchLabel);
            trackCorrection = new Button
            {
                Content = "打开赛道识别纠错助手",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(14, 8, 14, 8)
            };
            trackCorrection.Click += (_, _) =>
            {
                var dialog = new TrackCorrectionWindow(this, lapAnalysisModule.CorrectionCandidates);
                if (dialog.ShowDialog() != true || dialog.SelectedTrackId is not Guid trackId) return;
                try
                {
                    var result = lapAnalysisModule.CorrectTrackMatch(trackId);
                    MessageBox.Show(
                        result.Message,
                        "赛道已纠正",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    refreshVisiblePage?.Invoke();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        exception.Message,
                        "无法应用纠正",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            };
            trackMatchPanel.Children.Add(trackCorrection);
            stack.Children.Add(Card(trackMatchPanel));
        }
        var recordingControls = new StackPanel();
        var recordingLabel = Label(string.Empty, 13);
        recordingControls.Children.Add(recordingLabel);
        var recordButton = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        recordButton.Click += async (_, _) =>
        {
            recordButton.IsEnabled = false;
            if (recorder.IsManualRecording) await recorder.StopAsync(CancellationToken.None);
            else await recorder.StartAsync(CancellationToken.None);
            recordButton.IsEnabled = true;
            refreshVisiblePage?.Invoke();
        };
        recordingControls.Children.Add(recordButton);
        stack.Children.Add(Card(recordingControls));
        var moduleLabels = moduleManager.Modules.Select(module => (Module: module, Label: Label(string.Empty, 13))).ToArray();
        foreach (var (_, label) in moduleLabels) stack.Children.Add(Card(label));
        stack.Children.Add(Card(Label($"数据目录：{directories.Root}\n录制目录：{directories.RecordingsPath}\n日志目录：{directories.LogsPath}\nSchemaVersion：{store.SchemaVersion}\n车辆配置档案：{store.CountVehicleProfiles()}", 13)));
        refreshVisiblePage = () =>
        {
            var diagnostics = telemetry.Diagnostics;
            diagnosticsLabel.Text = $"来源/监听：{diagnostics.ListenAddress}\n状态：{diagnostics.State}\n包率：{diagnostics.PacketsPerSecond:0.0} Hz\n有效/无效：{diagnostics.ValidPackets:N0} / {diagnostics.InvalidPackets:N0}\n估计丢包：{diagnostics.EstimatedDroppedPackets:N0}\n同时间戳/乱序/回绕：{diagnostics.DuplicatePackets:N0} / {diagnostics.OutOfOrderPackets:N0} / {diagnostics.TimestampWraps:N0}\n最后包：{diagnostics.LastPacketAt?.ToLocalTime():O}\n最近错误：{diagnostics.LastError ?? "无"}";
            if (telemetry.Latest is { } latest)
            {
                var raw = latest.Raw;
                var displayGear = raw.IsRaceOn == 1 ? ForzaGear.Display(raw.Gear) : "—（非驾驶帧）";
                rawLabel.Text = $"当前原始帧（用于真实 FH6 核验）\nIsRaceOn={raw.IsRaceOn} · Competition={TelemetryContextClassifier.IsCompetition(raw)} · RacePosition={raw.RacePosition} · LapNumber={raw.LapNumber}\nCarOrdinal={raw.CarOrdinal} · Class/PI={raw.CarClass}/{raw.CarPerformanceIndex} · Cylinders={raw.NumCylinders} · Drivetrain={raw.DrivetrainType}\nRace/Current/Last={raw.CurrentRaceTime:0.000}/{raw.CurrentLap:0.000}/{raw.LastLap:0.000} s\nGear(raw)={raw.Gear} · Display={displayGear} · Speed={raw.Speed:0.000} m/s → {latest.Normalized.SpeedKph:0.0} km/h · Distance={raw.DistanceTraveled:0.0} m\nPosition={raw.Position.X:0.000}/{raw.Position.Y:0.000}/{raw.Position.Z:0.000} · Yaw/Pitch/Roll={raw.Yaw:0.000}/{raw.Pitch:0.000}/{raw.Roll:0.000}\nVelocity={raw.Velocity.X:0.000}/{raw.Velocity.Y:0.000}/{raw.Velocity.Z:0.000} · WheelRad/s={raw.WheelRotationSpeed.FrontLeft:0.000}/{raw.WheelRotationSpeed.FrontRight:0.000}/{raw.WheelRotationSpeed.RearLeft:0.000}/{raw.WheelRotationSpeed.RearRight:0.000}\nRPM={raw.CurrentEngineRpm:0}/{raw.EngineMaxRpm:0} · Power={raw.Power:0} W → {latest.Normalized.PowerKw:0.0} kW · Torque={raw.Torque:0.0} N·m\nAccel={raw.Accel} · Brake={raw.Brake} · Clutch={raw.Clutch} · HandBrake={raw.HandBrake} · Steer={raw.Steer}\nTireTemp raw={raw.TireTemperature.FrontLeft:0.0}/{raw.TireTemperature.FrontRight:0.0}/{raw.TireTemperature.RearLeft:0.0}/{raw.TireTemperature.RearRight:0.0} °F（实机同帧对照；仪表盘换算为 °C）";
            }
            else rawLabel.Text = "尚未收到原始帧。";
            if (lapAnalysisModule is not null)
            {
                var match = lapAnalysisModule.MatchDiagnostics;
                diagnosticCapture.UpdateTrackMatch(match);
                var candidates = match.TopCandidates.Count == 0
                    ? "  暂无候选"
                    : string.Join(
                        "\n",
                        match.TopCandidates.Select((candidate, index) =>
                        {
                            var layout = candidate.LayoutKind == TrackLayoutKind.Circuit ? "环道" : "定点";
                            var startDistance = candidate.StartDistanceMeters is double start
                                ? $"{start:0.0} m"
                                : "—";
                            var meanDistance = candidate.MeanDistanceMeters is double mean
                                ? $"{mean:0.0} m"
                                : "—";
                            var reason = string.IsNullOrWhiteSpace(candidate.EliminationReason)
                                ? string.Empty
                                : $" · 淘汰：{candidate.EliminationReason}";
                            return $"  {index + 1}. {candidate.TrackName} [{candidate.Stage} · {layout}/{candidate.Category ?? "未分类"} · {candidate.LengthMeters:0} m]" +
                                   $"\n     起点 {startDistance} · 平均距离 {meanDistance} · 进度 {candidate.ProgressMeters:0} m · 有效率 {candidate.ValidRatio:P0}{reason}";
                        }));
                var eliminations = match.EliminatedCandidates.Count == 0
                    ? "  暂无淘汰记录"
                    : string.Join(
                        "\n",
                        match.EliminatedCandidates.Select(candidate =>
                            $"  {candidate.TrackName}：{candidate.EliminationReason ?? "未进入精匹配集合"}"));
                trackMatchLabel.Text =
                    $"赛道识别 2.0\n状态：{match.State}\n路线总数/粗筛通过/精匹配：{match.TotalRoutes} / {match.CoarseEligibleRoutes} / {match.FineCandidateRoutes}\n前三名候选：\n{candidates}\n最近淘汰：\n{eliminations}";
                if (trackCorrection is not null)
                {
                    trackCorrection.IsEnabled = lapAnalysisModule.HasCurrentCompetitionSession;
                    trackCorrection.ToolTip = trackCorrection.IsEnabled
                        ? "根据当前候选证据或官方赛事名称纠正本场识别"
                        : "进入一场比赛后可使用纠错助手";
                }
            }
            if (exportPackage.IsEnabled)
            {
                packageStatus.Text =
                    $"短时缓冲 {diagnosticCapture.BufferedSamples} 帧 · " +
                    $"事件 {diagnosticCapture.EventCount} 条 · 异常快照 {diagnosticCapture.AnomalyCount} 个\n" +
                    "导出内容会移除用户名和本地目录，不包含原始 UDP 字节。";
            }
            recordingLabel.Text = recorder.IsManualRecording
                ? $"正在手动录制：{recorder.FramesWritten:N0} 帧\n{recorder.CurrentPath}\n{recorder.AutomaticStatus}"
                : $"手动原始数据录制已停止。\n{recorder.AutomaticStatus}";
            recordButton.Content = recorder.IsManualRecording ? "停止并写入文件" : "开始手动原始 324 字节录制";
            foreach (var (module, label) in moduleLabels) label.Text = $"{module.Descriptor.Id} · {module.Status.State} · Enabled={module.Status.IsEnabled} · Error={module.Status.LastError ?? "none"}";
        };
        refreshVisiblePage();
        return Scroll(stack);
    }

    private static StackPanel PageStack(string title, string description)
    {
        var stack = new StackPanel();
        var titleLabel = Label(title, 28, FontWeights.SemiBold);
        titleLabel.Margin = new Thickness(0, 0, 0, 5);
        stack.Children.Add(titleLabel);

        var descriptionLabel = Label(description, 14, FontWeights.Normal, "MutedBrush");
        descriptionLabel.Margin = new Thickness(0, 0, 0, 18);
        stack.Children.Add(descriptionLabel);
        return stack;
    }

    private static Grid NavigationEntry(string iconData, string title)
    {
        var entry = new Grid { MinHeight = 28, UseLayoutRounding = true };
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new VectorPath
        {
            Data = Geometry.Parse(iconData),
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        icon.SetBinding(VectorShape.StrokeProperty, NavigationForegroundBinding());
        entry.Children.Add(icon);

        var label = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            FontFamily = UiFont,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetBinding(TextBlock.ForegroundProperty, NavigationForegroundBinding());
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 2);
        entry.Children.Add(label);
        return entry;
    }

    private static Binding NavigationForegroundBinding() => new("Foreground")
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
    };

    private static StackPanel DataSectionHeader(string title, string description)
    {
        var header = new StackPanel();
        header.Children.Add(Label(title, 17, FontWeights.SemiBold));
        var detail = Label(description, 12, FontWeights.Normal, "MutedBrush");
        detail.Margin = new Thickness(0, 4, 0, 0);
        header.Children.Add(detail);
        return header;
    }

    private static Border DataSelectionOption(ToggleButton input, string title, string description)
    {
        var content = new StackPanel();
        content.Children.Add(Label(title, 14, FontWeights.SemiBold));
        var detail = Label(description, 12, FontWeights.Normal, "MutedBrush");
        detail.Margin = new Thickness(0, 2, 0, 0);
        content.Children.Add(detail);
        input.Content = content;
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.VerticalAlignment = VerticalAlignment.Stretch;
        input.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        input.Padding = new Thickness(0);
        input.Margin = new Thickness(12, 10, 12, 10);

        return new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(4),
            Child = input
        };
    }

    private static StackPanel DataSummaryItem(string title, string value, string? valueBrush = null)
    {
        var item = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        item.Children.Add(Label(title, 12, FontWeights.Normal, "MutedBrush"));
        var valueLabel = Label(value, 14, FontWeights.SemiBold, valueBrush);
        valueLabel.Margin = new Thickness(0, 3, 0, 0);
        item.Children.Add(valueLabel);
        return item;
    }

    private static Border MetricCard(string label, string value, string detail)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(label, 12, FontWeights.Normal, "MutedBrush"));
        stack.Children.Add(Label(value, 24, FontWeights.SemiBold));
        stack.Children.Add(Label(detail, 12, FontWeights.Normal, "MutedBrush"));
        return Card(stack);
    }

    private static Border MetricCard(string label, TextBlock value, TextBlock detail)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(label, 12, FontWeights.Normal, "MutedBrush"));
        stack.Children.Add(value);
        stack.Children.Add(detail);
        return Card(stack);
    }

    private static Border EmptyCard(string title, string message)
    {
        var stack = new StackPanel { Margin = new Thickness(10) };
        stack.Children.Add(Label(title, 17, FontWeights.SemiBold));
        stack.Children.Add(Label(message, 13, FontWeights.Normal, "MutedBrush"));
        return Card(stack);
    }

    private static Border PointToPointTimingNotice()
    {
        var stack = new StackPanel { Margin = new Thickness(4) };
        stack.Children.Add(Label("定点赛道计时可能有轻微误差", 15, FontWeights.SemiBold, "WarningBrush"));
        stack.Children.Add(Label(
            "FH6 UDP 不提供结算画面的最终插值时间，因此“≈”成绩可能相差数毫秒。",
            11, FontWeights.Normal, "MutedBrush"));
        return Card(stack);
    }

    private static Border Card(UIElement child) => new()
    {
        Background = Brush("CardBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 8, 10, 4),
        Child = child
    };

    private static TextBlock Label(string text, double size, FontWeight? weight = null, string? brushKey = null) => new()
    {
        Text = AppLocalization.Literal(text),
        FontSize = ReadableFontSize(size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brushKey is null ? Brush("TextBrush") : Brush(brushKey),
        TextWrapping = TextWrapping.Wrap,
        FontFamily = UiFont
    };

    private static double ReadableFontSize(double size) => size switch
    {
        <= 10 => 12,
        <= 11 => 13,
        <= 13 => 14,
        _ => size
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        PanningMode = PanningMode.VerticalOnly,
        IsDeferredScrollingEnabled = false
    };
    private static string Time(double? seconds) => seconds is double value ? TimeSpan.FromSeconds(value).ToString(@"m\:ss\.fff") : "—";
    private static string AnalysisTime(double? seconds, bool approximate) =>
        seconds is double value ? $"{(approximate ? "≈ " : string.Empty)}{Time(value)}" : "—";
    private static string PerformanceClassName(int value) => PerformanceClassCatalog.Name(value);
    private static string ModuleStateText(ModuleRuntimeState state) => AppLocalization.Literal(state switch
    {
        ModuleRuntimeState.Disabled => "已停用",
        ModuleRuntimeState.Initialized => "已就绪",
        ModuleRuntimeState.Starting => "正在启动",
        ModuleRuntimeState.Running => "运行中",
        ModuleRuntimeState.Stopping => "正在停止",
        ModuleRuntimeState.Faulted => "发生错误",
        _ => state.ToString()
    });

    private static string TelemetryStateText(TelemetryStreamState state) => AppLocalization.Literal(state switch
    {
        TelemetryStreamState.Disconnected => "未连接",
        TelemetryStreamState.Connecting => "正在连接",
        TelemetryStreamState.Live => "Live",
        TelemetryStreamState.Replay => "回放",
        TelemetryStreamState.Stale => "数据中断",
        TelemetryStreamState.Faulted => "发生错误",
        _ => state.ToString()
    });

    private static string LearningStateText(LearningState state) => AppLocalization.Literal(state switch
    {
        LearningState.NotStarted => "等待数据",
        LearningState.Collecting => "正在学习",
        LearningState.Insufficient => "样本不足",
        LearningState.Ready => "已就绪",
        LearningState.Stale => "需要重新学习",
        LearningState.Error => "发生错误",
        _ => state.ToString()
    });
    private static Color PerformanceClassColor(int value) => value switch
    {
        0 => Color.FromRgb(0x62, 0xB8, 0xE8),
        1 => Color.FromRgb(0xF2, 0xB8, 0x27),
        2 => Color.FromRgb(0xED, 0x7A, 0x1A),
        3 => Color.FromRgb(0xE3, 0x31, 0x4F),
        4 => Color.FromRgb(0xB4, 0x3B, 0xDD),
        5 => Color.FromRgb(0x24, 0x72, 0xD4),
        6 => Color.FromRgb(0xE6, 0x2A, 0x83),
        7 => Color.FromRgb(0x00, 0xB8, 0x5A),
        _ => Color.FromRgb(0x68, 0x76, 0x86)
    };
    private static Color DimmedPerformanceClassColor(Color color) => Color.FromRgb(
        (byte)(25 + color.R * 0.20),
        (byte)(30 + color.G * 0.20),
        (byte)(37 + color.B * 0.20));
    private static string SectorStateText(SectorColorState state) => AppLocalization.Literal(state switch
    {
        SectorColorState.Gray => "未跑",
        SectorColorState.Yellow => "偏慢",
        SectorColorState.Green => "本场最快",
        SectorColorState.Purple => "历史最快",
        _ => "未知"
    });
    private static string SectorStateBrush(SectorColorState state) => state switch
    {
        SectorColorState.Gray => "MutedBrush",
        SectorColorState.Yellow => "WarningBrush",
        SectorColorState.Green => "SuccessBrush",
        SectorColorState.Purple => "PurpleBrush",
        _ => "TextBrush"
    };
    private string ConfiguredLiveEndpoint()
    {
        var address = store.GetAppSetting("telemetry.listenAddress") ?? LazyForzaDefaults.TelemetryListenAddress;
        var port = store.GetAppSetting("telemetry.port") ?? LazyForzaDefaults.TelemetryPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{address}:{port}";
    }

    private void ApplyDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

internal sealed class TrackNameDialog : Window
{
    private readonly TextBox textBox;
    private TrackNameDialog(Window owner, string current)
    {
        Owner = owner;
        Title = "重命名赛道";
        Width = 420;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock { Text = "赛道名称", Margin = new Thickness(0, 0, 0, 6) });
        textBox = new TextBox { Text = current, Padding = new Thickness(8), MaxLength = 80 };
        stack.Children.Add(textBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(textBox.Text)) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); stack.Children.Add(buttons);
        Content = stack;
        Loaded += (_, _) => { textBox.SelectAll(); textBox.Focus(); };
    }

    public static string? Ask(Window owner, string current)
    {
        var dialog = new TrackNameDialog(owner, current);
        return dialog.ShowDialog() == true ? dialog.textBox.Text.Trim() : null;
    }
}

internal sealed class VehicleProfileNameDialog : Window
{
    private readonly TextBox textBox;

    private VehicleProfileNameDialog(Window owner, string current)
    {
        Owner = owner;
        Title = "重命名车辆配置";
        Width = 420;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock { Text = "配置名称", Margin = new Thickness(0, 0, 0, 6) });
        textBox = new TextBox { Text = current, Padding = new Thickness(8), MaxLength = 80 };
        stack.Children.Add(textBox);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(textBox.Text)) DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);
        Content = stack;
        Loaded += (_, _) =>
        {
            textBox.SelectAll();
            textBox.Focus();
        };
    }

    public static string? Ask(Window owner, string current)
    {
        var dialog = new VehicleProfileNameDialog(owner, current);
        return dialog.ShowDialog() == true ? dialog.textBox.Text.Trim() : null;
    }
}
