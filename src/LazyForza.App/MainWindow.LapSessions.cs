using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private LapSessionKey? selectedAnalysisSession;
    private int lapAnalysisTabIndex;

    private UIElement BuildLapSessionHistory(TrackTemplate? track, IReadOnlyList<LapSummary> laps,
        IReadOnlySet<int> classes, bool approximate, Action<LapSummary> inspect, Action<LapSummary>? edit = null)
    {
        if (track is null) return AnalysisEmptyState("未选择赛道", "从上方选择赛道，或进入比赛后自动识别。");
        if (classes.Count == 0) return AnalysisEmptyState("未选择性能等级", "选择至少一个性能等级。");
        // Filter whole sessions, never silently compute a partial race after class filtering.
        var sessions = LapSessionAnalyzer.Group(laps)
            .Where(session => session.Laps.Any(lap => classes.Contains(lap.Vehicle.CarClass))).ToArray();
        if (sessions.Length == 0) return AnalysisEmptyState("暂无比赛记录", "完成计圈后，同场记录会自动整理在一起。");
        var panel = new StackPanel();
        var selector = new ComboBox { MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(selector, AppLocalization.Literal("选择比赛"));
        foreach (var session in sessions)
            selector.Items.Add(new ComboBoxItem
            {
                Content = AppLocalization.Format("analysis.session.option", "{0:MM-dd HH:mm} · {1} · {2} 圈",
                    session.StartedAt.ToLocalTime(), SessionKindLabel(session.Info, track), session.Laps.Count),
                Tag = session
            });
        var heading = Label(AppLocalization.Format("analysis.session.count", "已保存比赛 · {0} 场", sessions.Length),
            16, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(heading);
        panel.Children.Add(selector);
        var detail = new ContentControl { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(detail);
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not ComboBoxItem { Tag: LapSessionAnalysis selected }) return;
            selectedAnalysisSession = selected.Key;
            detail.Content = BuildLapSessionDetail(selected, approximate, inspect, edit);
            AppLocalization.ApplyTo(detail);
        };
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => ((LapSessionAnalysis)item.Tag).Key == selectedAnalysisSession) ?? selector.Items[0];
        return panel;
    }

    private static string SessionKindLabel(LapSessionInfo? info, TrackTemplate track) => AppLocalization.Literal(info?.Kind switch
    {
        LapSessionKind.GameRace => "普通比赛",
        LapSessionKind.EstateTiming => "独立计时",
        LapSessionKind.EstatePractice => "地产练习",
        LapSessionKind.EstateQualifying => "地产排位",
        LapSessionKind.EstateRace => "地产正赛",
        _ => track.TimingKind == TrackTimingKind.EstateGeometry ? "地产历史记录" : "普通比赛"
    }) + (info?.Number is > 0 ? $" · {info.Number}" : string.Empty);

    internal static UIElement BuildLapSessionDetail(LapSessionAnalysis session, bool approximate, Action<LapSummary> inspect, Action<LapSummary>? edit = null)
    {
        var panel = new StackPanel();
        var overview = new StackPanel();
        if (!string.IsNullOrWhiteSpace(session.Info?.Name))
        {
            var name = Label(session.Info.Name, 17, FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            name.ToolTip = session.Info.Name;
            overview.Children.Add(name);
        }
        overview.Children.Add(Label(AppLocalization.Format("analysis.session.period", "{0:yyyy-MM-dd HH:mm} · {1} 圈记录 · {2}",
            session.StartedAt.ToLocalTime(), session.Laps.Count, PlayerCodeText(session.Laps[0].PlayerCode)), 12,
            FontWeights.Normal, "MutedBrush"));
        var review = session.Review;
        var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(-4, 12, -4, 4) };
        metrics.Children.Add(ReviewMetric(AppLocalization.Literal("记录总用时"), SessionDuration(session.RecordedSeconds, approximate),
            AppLocalization.Literal("全部已记录圈")));
        metrics.Children.Add(ReviewMetric(AppLocalization.Literal("典型圈速"), AnalysisTime(review.MedianLapSeconds, approximate),
            AppLocalization.Literal("有效圈中位数")));
        metrics.Children.Add(ReviewMetric(AppLocalization.Literal("圈速波动"), review.StandardDeviationSeconds is double deviation
            ? AppLocalization.Format("common.seconds3", "{0:0.000} 秒", deviation) : "—", AppLocalization.Literal("有效圈标准差")));
        metrics.Children.Add(ReviewMetric(AppLocalization.Literal("本场最快"), AnalysisTime(review.BestLapSeconds, approximate),
            AppLocalization.Format("analysis.session.valid", "{0}/{1} 圈有效", review.ValidLaps, review.TotalLaps)));
        metrics.SizeChanged += (_, _) => metrics.Columns = metrics.ActualWidth < 760 ? 2 : 4;
        overview.Children.Add(metrics);
        var note = Label("统计基于本机已保存圈，包含无效圈用时；缺失圈和服务端处罚未计入总用时。", 11, FontWeights.Normal, "MutedBrush");
        note.TextWrapping = TextWrapping.Wrap;
        overview.Children.Add(note);
        panel.Children.Add(AnalysisCard(overview));

        var pace = new StackPanel();
        pace.Children.Add(Label("逐圈节奏", 16, FontWeights.SemiBold));
        pace.Children.Add(Label("按记录顺序展示 · 红色为无效圈 · 点击数据点查看单圈", 11, FontWeights.Normal, "MutedBrush"));
        pace.Children.Add(new SessionPaceChart(session.Laps, inspect) { Height = 200, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(AnalysisCard(pace));

        var rows = session.Laps.Select((lap, index) => new SessionLapRow(
            index + 1, AnalysisTime(lap.TotalSeconds, approximate),
            review.MedianLapSeconds is double median ? $"{lap.TotalSeconds - median:+0.000;-0.000;0.000}" : "—",
            $"{PerformanceClassName(lap.Vehicle.CarClass)} {lap.Vehicle.PerformanceIndex}",
            AppLocalization.Literal(!lap.IsValid ? "无效" : lap.TotalSeconds == review.BestLapSeconds ? "最快" : "有效"),
            !lap.IsValid ? Brush("DangerBrush") : lap.TotalSeconds == review.BestLapSeconds ? Brush("PurpleBrush") : Brush("MutedBrush"),
            lap)).ToArray();
        var lapList = new ListBox { ItemsSource = rows, MaxHeight = 320, MinHeight = 54,
            ItemTemplate = (DataTemplate)new ResourceDictionary
            { Source = new Uri("/LazyForza.App;component/AnalysisWorkspace.xaml", UriKind.Relative) }["SessionLapRow"] };
        VirtualizingPanel.SetIsVirtualizing(lapList, true);
        VirtualizingPanel.SetVirtualizationMode(lapList, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(lapList, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(lapList, AppLocalization.Literal("本场圈记录"));
        var records = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        var open = AnalysisButton("分析所选圈");
        open.IsEnabled = false;
        open.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(open);
        var manage = AnalysisButton("管理圈记录");
        manage.IsEnabled = false;
        manage.Visibility = edit is null ? Visibility.Collapsed : Visibility.Visible;
        DockPanel.SetDock(manage, Dock.Right); if (edit is not null) header.Children.Add(manage);
        manage.Click += (_, _) => { if (lapList.SelectedItem is SessionLapRow selected) edit?.Invoke(selected.Lap); };
        var title = Label("本场圈记录", 16, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        records.Children.Add(header);
        var columns = new Grid { Margin = new Thickness(13, 8, 13, 2) };
        var labels = new[] { "记录圈", "圈速", "相对中位数", "等级 / PI", "状态" };
        for (var i = 0; i < labels.Length; i++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(i == 0 ? 64 : i == 4 ? 64 : 120) });
            var label = Label(labels[i], 11, FontWeights.Normal, "MutedBrush");
            Grid.SetColumn(label, i);
            columns.Children.Add(label);
        }
        records.Children.Add(columns);
        records.Children.Add(lapList);
        lapList.SelectionChanged += (_, _) => open.IsEnabled = manage.IsEnabled = lapList.SelectedItem is SessionLapRow;
        void OpenSelected() { if (lapList.SelectedItem is SessionLapRow row) inspect(row.Lap); }
        open.Click += (_, _) => OpenSelected();
        lapList.MouseDoubleClick += (_, _) => OpenSelected();
        lapList.KeyDown += (_, args) => { if (args.Key == Key.Enter) { OpenSelected(); args.Handled = true; } };
        panel.Children.Add(AnalysisCard(records));
        if (review.Sectors.Count > 0)
            panel.Children.Add(AnalysisDisclosure("分段稳定性", BuildSectorStabilityTable(review.Sectors, approximate)));
        return panel;
    }

    internal static string SessionDuration(double seconds, bool approximate)
    {
        if (seconds < 3600) return AnalysisTime(seconds, approximate);
        var time = TimeSpan.FromSeconds(seconds);
        return (approximate ? "≈" : string.Empty) + $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}

internal sealed record SessionLapRow(int Number, string Time, string Delta, string Vehicle, string State, Brush StateBrush, LapSummary Lap)
{
    public string Label => string.Join(" · ", new[] { Lap.Annotation.IsFavorite ? AppLocalization.Literal("收藏") : null,
        Lap.Annotation.IsReference ? AppLocalization.Literal("固定参考") : null, Lap.Annotation.Name }.Where(value => value is not null));
    public string? Tooltip => Lap.Annotation.Notes ?? Lap.InvalidReason;
}
