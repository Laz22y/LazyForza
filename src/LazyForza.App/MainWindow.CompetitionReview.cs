using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private Border BuildCompetitionReviewCard(
        string trackName,
        int? performanceClass,
        int? performanceIndex,
        IReadOnlyList<LapSummary> laps,
        bool isCompleted,
        bool approximateTiming)
    {
        var review = RaceReviewAnalyzer.Analyze(laps);
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Label(
            isCompleted ? "赛后复盘与稳定性" : "本场复盘与稳定性",
            16,
            FontWeights.SemiBold));
        heading.Children.Add(Label(
            isCompleted
                ? "根据上一场已保存圈速生成。"
                : "随本场有效圈更新，比赛结束后保留 5 分钟。",
            11,
            FontWeights.Normal,
            "MutedBrush"));
        header.Children.Add(heading);
        var export = new Button
        {
            Content = "导出 PNG",
            Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = review.ValidLaps > 0,
            ToolTip = "导出固定尺寸的赛后复盘图片"
        };
        export.Click += (_, _) =>
        {
            try
            {
                var report = BuildCompetitionReviewReport(
                    trackName,
                    performanceClass,
                    performanceIndex,
                    laps,
                    isCompleted,
                    approximateTiming);
                var path = PngReportExporter.Export(
                    this,
                    report,
                    $"LazyForza-{AppLocalization.Text("png.competitionReview.fileStem", "赛后复盘")}-{trackName}-{DateTime.Now:yyyyMMdd-HHmm}.png");
                if (path is not null)
                    AppDialog.Show(
                        AppLocalization.Format("common.exportedPath", "已导出：\n{0}", path),
                        AppLocalization.Text("common.exportComplete", "导出完成"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                AppDialog.Show(
                    AppLocalization.Format("png.export.failedMessage", "无法导出 PNG：{0}", exception.Message),
                    AppLocalization.Text("common.exportFailed", "导出失败"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
        Grid.SetColumn(export, 1);
        header.Children.Add(export);
        content.Children.Add(header);
        content.Children.Add(BuildReviewMetrics(review, approximateTiming, compact: true));
        content.Children.Add(BuildReviewFindings(review));
        if (review.Sectors.Count > 0)
            content.Children.Add(BuildSectorStabilityTable(review.Sectors, approximateTiming));
        return Card(content);
    }

    private FrameworkElement BuildCompetitionReviewReport(
        string trackName,
        int? performanceClass,
        int? performanceIndex,
        IReadOnlyList<LapSummary> laps,
        bool isCompleted,
        bool approximateTiming)
    {
        var review = RaceReviewAnalyzer.Analyze(laps);
        var stack = new StackPanel();
        stack.Children.Add(Label(AppLocalization.Literal("LazyForza · 赛后复盘与稳定性"), 28, FontWeights.Bold));
        var classText = performanceClass is int classCode
            ? $"{PerformanceClassName(classCode)} {performanceIndex?.ToString() ?? "—"}"
            : AppLocalization.Literal("性能等级未知");
        var subtitle = Label(
            AppLocalization.Format(
                "png.competitionReview.subtitle",
                "{0} · {1} · {2} · 导出于 {3:yyyy-MM-dd HH:mm}",
                trackName,
                classText,
                AppLocalization.Literal(isCompleted ? "比赛已结束" : "比赛进行中"),
                DateTime.Now),
            13,
            FontWeights.Normal,
            "MutedBrush");
        subtitle.Margin = new Thickness(0, 6, 0, 20);
        stack.Children.Add(subtitle);
        stack.Children.Add(BuildReviewMetrics(review, approximateTiming, compact: false));

        var sectionHeading = Label("结论", 17, FontWeights.SemiBold);
        sectionHeading.Margin = new Thickness(0, 20, 0, 8);
        stack.Children.Add(sectionHeading);
        stack.Children.Add(BuildReviewFindings(review));
        if (review.Sectors.Count > 0)
        {
            var sectorHeading = Label("分段稳定性", 17, FontWeights.SemiBold);
            sectorHeading.Margin = new Thickness(0, 20, 0, 8);
            stack.Children.Add(sectorHeading);
            stack.Children.Add(BuildSectorStabilityTable(review.Sectors, approximateTiming));
        }

        var lapsHeading = Label("有效圈摘要", 17, FontWeights.SemiBold);
        lapsHeading.Margin = new Thickness(0, 20, 0, 8);
        stack.Children.Add(lapsHeading);
        stack.Children.Add(BuildReviewLapTable(laps, approximateTiming));
        var note = Label(
            "稳定性只根据本机已记录的有效圈与分段计算，不代表线上排名或官方成绩。",
            11,
            FontWeights.Normal,
            "MutedBrush");
        note.Margin = new Thickness(0, 18, 0, 0);
        stack.Children.Add(note);
        return ReportChrome(stack);
    }

    private static UniformGrid BuildReviewMetrics(
        RaceReview review,
        bool approximateTiming,
        bool compact)
    {
        var grid = new UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, compact ? 14 : 0, 0, compact ? 12 : 0)
        };
        grid.Children.Add(ReviewMetric(
            AppLocalization.Literal("有效圈"),
            $"{review.ValidLaps} / {review.TotalLaps}",
            AppLocalization.Literal(review.ValidLaps < 3 ? "样本较少" : "用于统计")));
        grid.Children.Add(ReviewMetric(
            AppLocalization.Literal("本场最快"),
            AnalysisTime(review.BestLapSeconds, approximateTiming),
            AppLocalization.Literal("有效圈")));
        grid.Children.Add(ReviewMetric(
            AppLocalization.Literal("稳定性"),
            review.ConsistencyPercent is double consistency ? $"{consistency:0}%" : "—",
            review.StandardDeviationSeconds is double deviation
                ? AppLocalization.Format("png.review.standardDeviation", "标准差 {0:0.000} 秒", deviation)
                : AppLocalization.Literal("等待有效圈")));
        grid.Children.Add(ReviewMetric(
            AppLocalization.Literal("组合最佳"),
            AnalysisTime(review.TheoreticalBestSeconds, approximateTiming),
            review.TheoreticalGainSeconds is double gain
                ? AppLocalization.Format("png.review.gain", "相对最快 -{0:0.000} 秒", gain)
                : AppLocalization.Literal("等待完整分段")));
        return grid;
    }

    private static Border ReviewMetric(string title, string value, string detail)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(title, 11, FontWeights.Normal, "MutedBrush"));
        var valueLabel = Label(value, 20, FontWeights.SemiBold);
        valueLabel.Margin = new Thickness(0, 4, 0, 2);
        panel.Children.Add(valueLabel);
        panel.Children.Add(Label(detail, 10, FontWeights.Normal, "MutedBrush"));
        return new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(4),
            Child = panel
        };
    }

    private static Border BuildReviewFindings(RaceReview review)
    {
        var panel = new StackPanel();
        foreach (var finding in review.Findings)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = Label("•", 13, FontWeights.Bold, "AccentBrush");
            row.Children.Add(marker);
            var text = Label(AppLocalization.Literal(finding), 12);
            text.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = panel
        };
    }

    private static Grid BuildSectorStabilityTable(
        IReadOnlyList<RaceSectorStability> sectors,
        bool approximateTiming)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var width in new[] { 0.7, 1.2, 1.2, 1.2, 1.2 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        AddRow(new[] { "分段", "最快", "中位数", "标准差", "可组合空间" }
            .Select(AppLocalization.Literal).ToArray(), true);
        foreach (var sector in sectors)
        {
            AddRow([
                $"S{sector.Index + 1}",
                AnalysisTime(sector.BestSeconds, approximateTiming),
                AnalysisTime(sector.MedianSeconds, approximateTiming),
                AppLocalization.Format("common.seconds3", "{0:0.000} 秒", sector.StandardDeviationSeconds),
                AppLocalization.Format("common.seconds3", "{0:0.000} 秒", sector.OpportunitySeconds)
            ], false);
        }
        return grid;

        void AddRow(string[] cells, bool header)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < cells.Length; column++)
            {
                var text = Label(cells[column], header ? 11 : 12, header ? FontWeights.SemiBold : FontWeights.Normal);
                text.Margin = new Thickness(7, 6, 7, 6);
                Grid.SetRow(text, row);
                Grid.SetColumn(text, column);
                grid.Children.Add(text);
            }
        }
    }

    private static Grid BuildReviewLapTable(
        IReadOnlyList<LapSummary> laps,
        bool approximateTiming)
    {
        var valid = laps
            .Where(lap => lap.IsValid)
            .OrderBy(lap => lap.TotalSeconds)
            .Take(8)
            .ToArray();
        var grid = new Grid();
        foreach (var width in new[] { 0.6, 1.2, 1.2, 3.0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        AddRow(new[] { "排名", "圈速", "时间", "分段" }
            .Select(AppLocalization.Literal).ToArray(), true);
        for (var index = 0; index < valid.Length; index++)
        {
            var lap = valid[index];
            AddRow([
                (index + 1).ToString(),
                AnalysisTime(lap.TotalSeconds, approximateTiming),
                lap.StartedAt.ToLocalTime().ToString("HH:mm:ss"),
                string.Join("  ", lap.Segments.Select(segment =>
                    $"S{segment.Index + 1} {AnalysisTime(segment.TimeSeconds, approximateTiming)}"))
            ], false);
        }
        return grid;

        void AddRow(string[] cells, bool header)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < cells.Length; column++)
            {
                var text = Label(cells[column], header ? 11 : 12, header ? FontWeights.SemiBold : FontWeights.Normal);
                text.Margin = new Thickness(7, 6, 7, 6);
                Grid.SetRow(text, row);
                Grid.SetColumn(text, column);
                grid.Children.Add(text);
            }
        }
    }

    private static Border ReportChrome(UIElement content) => new()
    {
        Width = 1180,
        Background = Brush("WindowBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(32),
        Child = content
    };
}
