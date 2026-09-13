using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;
using LazyForza.Modules.EstateRace;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement OverviewPage()
    {
        var stack = PageStack("概览", "驾驶、分析、回看。");
        var lapModule = moduleManager.Modules.OfType<LapAnalysisModule>().Single();
        var estateModule = moduleManager.Modules.OfType<EstateRaceModule>().Single();
        var stateLabel = Label(string.Empty, 12, FontWeights.Normal, "AccentBrush");
        var trackName = Label(string.Empty, 27, FontWeights.SemiBold);
        trackName.Margin = new Thickness(0, 9, 0, 5);
        var sessionDetail = Label(string.Empty, 13, FontWeights.Normal, "MutedBrush");
        stack.Children.Add(stateLabel);
        stack.Children.Add(trackName);
        stack.Children.Add(sessionDetail);

        var sessionGrid = new Grid { MinHeight = 142 };
        foreach (var width in new[] { 0.85, 1.05, 1.25 }) sessionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        var lapCaption = Label("当前比赛", 12, FontWeights.Normal, "MutedBrush");
        var best = OverviewNumber("—", 36);
        var bestDetail = Label(string.Empty, 12, FontWeights.Normal, "MutedBrush");
        var numberLine = new TextBlock { Margin = new Thickness(0, 12, 0, 5) };
        // Inline text shares one baseline even when digit and unit sizes differ.
        var numberRun = new System.Windows.Documents.Run("—") { FontFamily = new FontFamily("Bahnschrift"), FontSize = 64, FontWeight = FontWeights.SemiBold };
        var totalRun = new System.Windows.Documents.Run { FontFamily = new FontFamily("Bahnschrift"), FontSize = 24, Foreground = Brush("MutedBrush") };
        numberLine.Inlines.Add(numberRun);
        numberLine.Inlines.Add(totalRun);
        var lapColumn = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        lapColumn.Children.Add(Label("CURRENT LAP", 12, FontWeights.Normal, "MutedBrush"));
        lapColumn.Children.Add(numberLine);
        lapColumn.Children.Add(lapCaption);
        sessionGrid.Children.Add(lapColumn);
        var bestColumn = new StackPanel { Margin = new Thickness(24, 0, 20, 0) };
        bestColumn.Children.Add(Label("本场最快", 12, FontWeights.Normal, "MutedBrush"));
        best.Margin = new Thickness(0, 25, 0, 11);
        bestColumn.Children.Add(best);
        bestColumn.Children.Add(bestDetail);
        var bestFrame = new Border { Child = bestColumn, BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(bestFrame, 1);
        sessionGrid.Children.Add(bestFrame);
        var mapColumn = new Grid { Margin = new Thickness(24, 0, 0, 0) };
        mapColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mapColumn.RowDefinitions.Add(new RowDefinition());
        mapColumn.Children.Add(Label("赛道预览", 12, FontWeights.Normal, "MutedBrush"));
        var map = new OverviewTrackMap { MinHeight = 106, ClipToBounds = true };
        Grid.SetRow(map, 1);
        mapColumn.Children.Add(map);
        var mapEmpty = Label("识别赛道后显示", 12, FontWeights.Normal, "MutedBrush");
        mapEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        mapEmpty.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(mapEmpty, 1);
        mapColumn.Children.Add(mapEmpty);
        var mapFrame = new Border { Child = mapColumn, BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(mapFrame, 2);
        sessionGrid.Children.Add(mapFrame);
        var hero = Card(sessionGrid);
        hero.BorderThickness = new Thickness(0);
        hero.Padding = new Thickness(24);
        hero.Margin = new Thickness(0, 24, 10, 20);
        stack.Children.Add(hero);

        var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 6, 0, 22) };
        var rate = OverviewMetric(metrics, "遥测频率", "Hz");
        var lapCount = OverviewMetric(metrics, "本地圈记录", AppLocalization.Text("overview.unit.laps", "圈"));
        var trackCount = OverviewMetric(metrics, "已收录赛道", AppLocalization.Text("overview.unit.tracks", "条"));
        var invalid = OverviewMetric(metrics, "无效包", AppLocalization.Text("overview.unit.packets", "个"));
        stack.Children.Add(new Border { Child = metrics, BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 10, 25) });

        var lower = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var records = new StackPanel();
        records.Children.Add(Label("最近圈记录", 17, FontWeights.SemiBold));
        var recentRows = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        records.Children.Add(recentRows);
        lower.Children.Add(records);
        var actions = new StackPanel();
        actions.Children.Add(Label("继续", 17, FontWeights.SemiBold));
        foreach (var (title, detail, index) in new[]
        {
            ("圈速分析", "比较圈时、分段和弯道", 4),
            ("回放工作台", "沿时间轴回看驾驶细节", 5)
        })
        {
            var body = new StackPanel();
            body.Children.Add(Label(title, 15, FontWeights.SemiBold));
            var caption = Label(detail, 12, FontWeights.Normal, "MutedBrush");
            caption.Margin = new Thickness(0, 5, 0, 0);
            body.Children.Add(caption);
            var button = new Button { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brush("CardBrush"), Padding = new Thickness(18, 15, 18, 15), Margin = new Thickness(0, 16, 0, 0) };
            button.Click += (_, _) => navigation.SelectedIndex = index;
            actions.Children.Add(button);
        }
        Grid.SetColumn(actions, 2);
        lower.Children.Add(actions);
        stack.Children.Add(lower);
        var status = Label(string.Empty, 12, FontWeights.Normal, "MutedBrush");
        status.Margin = new Thickness(0, 24, 10, 0);
        stack.Children.Add(status);
        sessionGrid.SizeChanged += (_, _) =>
        {
            var compact = sessionGrid.ActualWidth < 750;
            numberRun.FontSize = compact ? 48 : 64;
            totalRun.FontSize = compact ? 18 : 24;
            best.FontSize = compact ? 27 : 36;
            mapColumn.Margin = new Thickness(compact ? 12 : 24, 0, 0, 0);
            bestColumn.Margin = new Thickness(compact ? 16 : 24, 0, 12, 0);
        };

        refreshVisiblePage = () =>
        {
            var diagnostics = telemetry.Diagnostics;
            var estate = estateModule.Snapshot as EstateRaceHudState;
            var race = estate is { IsConnected: true } ? estate.Session : null;
            var local = race?.Participants.FirstOrDefault(p => p.Id == estate!.LocalParticipantId);
            var lap = lapModule.CurrentCompetitionSnapshot;
            var active = race is not null || lap is { IsCompetitionActive: true };
            var track = active && race is null ? lapModule.CurrentTrack : null;
            stateLabel.Text = AppLocalization.Literal(race is not null ? "赛事已连接" : active ? "赛事进行中" : "准备驾驶");
            trackName.Text = race?.TrackName ?? (active ? track?.Name ?? lap?.TrackName : null)
                ?? AppLocalization.Literal("等待下一场比赛");
            sessionDetail.Text = race is not null
                ? AppLocalization.Literal("地产赛事") + "  /  " + RacePhaseLabel(race.Phase)
                : active ? AppLocalization.Literal(lap!.Status)
                : AppLocalization.Literal("进入赛事后，这里会自动显示赛道与本场成绩。");
            numberRun.Text = local is not null && race!.Phase is RaceSessionPhase.Race or RaceSessionPhase.Practice or RaceSessionPhase.Qualifying or RaceSessionPhase.Finished
                ? Math.Min(local.CompletedLaps + (race.Phase == RaceSessionPhase.Finished ? 0 : 1),
                    race.Phase == RaceSessionPhase.Race && race.TotalRaceLaps > 0 ? race.TotalRaceLaps : int.MaxValue).ToString("00")
                : lap is { IsCompetitionActive: true } ? (lap.CompletedLaps + 1).ToString("00") : "—";
            totalRun.Text = race is { TotalRaceLaps: > 0, Phase: RaceSessionPhase.Race or RaceSessionPhase.Finished }
                ? $"  / {race.TotalRaceLaps:00}" : string.Empty;
            lapCaption.Text = AppLocalization.Literal(race is null ? "当前比赛" : "地产赛事");
            var sessionBest = local?.BestLapSeconds ?? (race is null && lap is not null
                ? lapModule.CurrentSessionLaps.Where(l => l.IsValid && l.Vehicle.CarClass == lapModule.CurrentCompetitionPerformanceClass)
                    .Select(l => (double?)l.TotalSeconds).Min() : null);
            best.Text = AnalysisTime(sessionBest, track?.LayoutKind == TrackLayoutKind.PointToPoint);
            bestDetail.Text = sessionBest is null ? AppLocalization.Literal("完成有效圈后显示")
                : AppLocalization.Literal(race is null ? "本场有效圈 · 同性能等级" : "服务端确认成绩");
            var hasMap = estate is { IsConnected: true, TrackOutline.Count: > 1 } || track is { Points.Count: > 1 };
            mapEmpty.Visibility = hasMap ? Visibility.Collapsed : Visibility.Visible;
            if (estate is { IsConnected: true, TrackOutline.Count: > 1 })
                map.SetRoute(estate.TrackOutline, estate.TrackOutline.Select(p => new Point(p.X * 1000, p.Y * 1000)));
            else map.SetRoute(track, track?.Points.Select(p => new Point(p.X, -p.Z)) ?? []);
            rate.Text = diagnostics.PacketsPerSecond.ToString("0.0");
            invalid.Text = diagnostics.InvalidPackets.ToString("N0");
            if (pageRefresh.ShouldRefreshOverviewStorage(DateTimeOffset.UtcNow))
            {
                pageRefresh.UpdateOverviewStorage(store.CountLaps(CurrentTrackSource), store.CountTracks(CurrentTrackSource), DateTimeOffset.UtcNow);
                var tracks = store.ListTracks(CurrentTrackSource).ToDictionary(t => t.Id);
                var recent = store.LoadRecentLapSummaries(CurrentTrackSource);
                recentRows.Children.Clear();
                foreach (var record in recent)
                {
                    var row = new Grid { Margin = new Thickness(0, 11, 0, 11) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
                    var identity = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                    var title = Label(tracks.GetValueOrDefault(record.TrackId)?.Name ?? "—", 14);
                    title.TextWrapping = TextWrapping.NoWrap;
                    title.TextTrimming = TextTrimming.CharacterEllipsis;
                    identity.Children.Add(title);
                    var detail = Label($"{PerformanceClassName(record.Vehicle.CarClass)} {record.Vehicle.PerformanceIndex}  ·  {record.StartedAt.ToLocalTime():MM-dd HH:mm}", 12, FontWeights.Normal, "MutedBrush");
                    detail.Margin = new Thickness(0, 5, 0, 0);
                    identity.Children.Add(detail);
                    row.Children.Add(identity);
                    var time = OverviewNumber(AnalysisTime(record.TotalSeconds, tracks.GetValueOrDefault(record.TrackId)?.LayoutKind == TrackLayoutKind.PointToPoint), 23,
                        record.IsValid ? "AccentBrush" : "MutedBrush");
                    time.TextAlignment = TextAlignment.Right;
                    time.VerticalAlignment = VerticalAlignment.Center;
                    time.ToolTip = record.IsValid ? AppLocalization.Literal("有效圈") : AppLocalization.Literal(record.InvalidReason ?? "无效圈");
                    Grid.SetColumn(time, 1);
                    row.Children.Add(time);
                    recentRows.Children.Add(new Border { Child = row, BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
                }
                if (recent.Count == 0) recentRows.Children.Add(Label("还没有圈记录，完成比赛后可在这里查看。", 13, FontWeights.Normal, "MutedBrush"));
            }
            lapCount.Text = pageRefresh.OverviewLapCount.ToString("N0");
            trackCount.Text = pageRefresh.OverviewTrackCount.ToString("N0");
            status.Text = TelemetryStateText(diagnostics.State) + "  ·  " + string.Join("    ", moduleManager.Modules
                .Where(m => m.Status.IsEnabled).Select(m => AppLocalization.Literal(m.Descriptor.DisplayName) + " · " + ModuleStateText(m.Status.State)));
        };
        pageRefresh.InvalidateOverviewStorage();
        refreshVisiblePage();
        return Scroll(stack);
    }

    private static TextBlock OverviewNumber(string value, double size, string? brush = null)
    {
        var label = Label(value, size, FontWeights.SemiBold, brush);
        label.FontFamily = new FontFamily("Bahnschrift");
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        return label;
    }

    private static System.Windows.Documents.Run OverviewMetric(Panel panel, string title, string unit)
    {
        var metric = new StackPanel { Margin = new Thickness(0, 4, 16, 0) };
        metric.Children.Add(Label(title, 12, FontWeights.Normal, "MutedBrush"));
        var line = Label(string.Empty, 28, FontWeights.SemiBold);
        line.Margin = new Thickness(0, 9, 0, 0);
        var value = new System.Windows.Documents.Run("—") { FontFamily = new FontFamily("Bahnschrift") };
        line.Inlines.Add(value);
        line.Inlines.Add(new System.Windows.Documents.Run("   " + AppLocalization.Literal(unit))
            { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Brush("MutedBrush") });
        metric.Children.Add(line);
        panel.Children.Add(metric);
        return value;
    }

    private UIElement ModulesPage()
    {
        var stack = PageStack(
            "模块",
            "管理需要手动开关的功能。");
        foreach (var module in moduleManager.Modules.Where(module =>
                     module is not EstateCircuitModule and not EstateRaceModule))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var description = new StackPanel();
            description.Children.Add(Label(module.Descriptor.DisplayName, 17, FontWeights.SemiBold));
            description.Children.Add(Label(module.Descriptor.Description, 13, FontWeights.Normal, "MutedBrush"));
            description.Children.Add(Label(AppLocalization.Format(
                    "modules.status",
                    "状态：{0}",
                    ModuleStateText(module.Status.State)) +
                (module.Status.LastError is null ? string.Empty : $" · {AppLocalization.Literal(module.Status.LastError)}"), 12,
                FontWeights.Normal, module.Status.State == ModuleRuntimeState.Faulted ? "AccentBrush" : "MutedBrush"));
            if (module is DriftDashboardModule)
            {
                var protection = Label(
                    "开启后暂停圈速分析和圈速写入；关闭后恢复原设置。",
                    11,
                    FontWeights.Normal,
                    "MutedBrush");
                protection.Margin = new Thickness(0, 6, 0, 0);
                protection.TextWrapping = TextWrapping.Wrap;
                description.Children.Add(protection);
                var autoCloseDashboard = new ToggleButton
                {
                    Content = AutoCloseDashboardText(
                        moduleActivation.AutoCloseDashboard),
                    IsChecked = moduleActivation.AutoCloseDashboard,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0),
                    Padding = new Thickness(11, 5, 11, 5),
                    ToolTip = "关闭此选项后，漂移仪表盘可以和主仪表盘同时显示。"
                };
                autoCloseDashboard.Click += async (_, _) =>
                {
                    changingModule = true;
                    autoCloseDashboard.IsEnabled = false;
                    try
                    {
                        await moduleActivation.SetAutoCloseDashboardAsync(
                            autoCloseDashboard.IsChecked == true,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        AppDialog.Show(
                            AppLocalization.Literal(exception.Message),
                            AppLocalization.Literal("漂移仪表盘设置失败"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    finally
                    {
                        changingModule = false;
                        RenderSelectedPage();
                    }
                };
                description.Children.Add(autoCloseDashboard);
            }
            row.Children.Add(description);
            var blockedByDrift =
                module is LapAnalysisModule &&
                moduleActivation.IsDriftActive;
            var toggle = new ToggleButton
            {
                Content = blockedByDrift
                    ? "漂移模式中"
                    : module.Status.IsEnabled ? "已启用" : "已停用",
                IsChecked = module.Status.IsEnabled,
                IsEnabled = !blockedByDrift,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = blockedByDrift
                    ? "请先关闭漂移仪表盘，再启用圈速分析。"
                    : null
            };
            toggle.Click += async (_, _) =>
            {
                var acceptedIntroduction = false;
                bool? introductionAutoClose = null;
                var requestedEnabled = toggle.IsChecked == true;
                if (module is DriftDashboardModule &&
                    requestedEnabled &&
                    !moduleActivation.IntroductionSeen)
                {
                    var introduction = new DriftDashboardIntroductionWindow(
                        moduleActivation.AutoCloseDashboard)
                    {
                        Owner = this
                    };
                    if (introduction.ShowDialog() != true)
                    {
                        toggle.IsChecked = false;
                        return;
                    }
                    acceptedIntroduction = true;
                    introductionAutoClose = introduction.AutoCloseDashboard;
                }

                changingModule = true;
                toggle.IsEnabled = false;
                try
                {
                    if (introductionAutoClose is bool autoClose)
                    {
                        await moduleActivation.SetAutoCloseDashboardAsync(
                            autoClose,
                            CancellationToken.None);
                    }
                    await moduleActivation.SetEnabledAsync(
                        module.Descriptor.Id,
                        requestedEnabled,
                        CancellationToken.None);
                    if (acceptedIntroduction)
                    {
                        await moduleActivation.MarkIntroductionSeenAsync(
                            CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    AppDialog.Show(AppLocalization.Literal(exception.Message), AppLocalization.Literal("模块切换失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    changingModule = false;
                    RenderSelectedPage();
                }
            };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            stack.Children.Add(Card(row));
        }

        return Scroll(stack);

        static string AutoCloseDashboardText(bool enabled) => AppLocalization.Format(
            "modules.autoCloseDashboard",
            "打开漂移仪表盘时自动关闭主仪表盘：{0}",
            AppLocalization.Literal(enabled ? "开" : "关"));
    }
}
