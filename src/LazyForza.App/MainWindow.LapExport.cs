using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private void ExportLapComparisonPng(
        string trackName,
        TrackTemplate? track,
        IReadOnlyList<LapRecord> laps,
        IReadOnlyList<LapSeriesLegendEntry> legendEntries,
        IReadOnlyList<CornerMapAnnotation> cornerAnnotations,
        bool approximateTiming,
        DrivingDynamicsLayer dynamicsLayer,
        Guid dynamicsLapId)
    {
        try
        {
            var report = BuildLapComparisonReport(
                trackName,
                track,
                laps,
                legendEntries,
                cornerAnnotations,
                approximateTiming,
                dynamicsLayer,
                dynamicsLapId);
            var path = PngReportExporter.Export(
                this,
                report,
                $"LazyForza-{AppLocalization.Text("png.lapAnalysis.fileStem", "圈速分析")}-{trackName}-{DateTime.Now:yyyyMMdd-HHmm}.png");
            if (path is not null)
                MessageBox.Show(
                    AppLocalization.Format("common.exportedPath", "已导出：\n{0}", path),
                    AppLocalization.Text("common.exportComplete", "导出完成"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                AppLocalization.Format("png.export.failedMessage", "无法导出 PNG：{0}", exception.Message),
                AppLocalization.Text("common.exportFailed", "导出失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private FrameworkElement BuildLapComparisonReport(
        string trackName,
        TrackTemplate? track,
        IReadOnlyList<LapRecord> laps,
        IReadOnlyList<LapSeriesLegendEntry> legendEntries,
        IReadOnlyList<CornerMapAnnotation> cornerAnnotations,
        bool approximateTiming,
        DrivingDynamicsLayer dynamicsLayer,
        Guid dynamicsLapId)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(
            AppLocalization.Literal(laps.Count == 1 ? "LazyForza · 单圈分析" : "LazyForza · 圈速对比"),
            28,
            FontWeights.Bold));
        var subtitle = Label(
            AppLocalization.Format(
                "png.lapAnalysis.subtitle",
                "{0} · {1} 圈 · 导出于 {2:yyyy-MM-dd HH:mm}",
                trackName,
                laps.Count,
                DateTime.Now),
            13,
            FontWeights.Normal,
            "MutedBrush");
        subtitle.Margin = new Thickness(0, 6, 0, 18);
        stack.Children.Add(subtitle);
        stack.Children.Add(BuildLapReportSummary(laps, legendEntries, approximateTiming));

        var chartTitle = Label("速度曲线", 17, FontWeights.SemiBold);
        chartTitle.Margin = new Thickness(0, 20, 0, 8);
        stack.Children.Add(chartTitle);
        stack.Children.Add(new Border
        {
            Height = 300,
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new LapTelemetryChart(laps, track?.LengthMeters, legendEntries)
        });

        var mapTitle = Label(
            dynamicsLayer == DrivingDynamicsLayer.Default
                ? AppLocalization.Literal("走线预览")
                : AppLocalization.Format(
                    "png.lapAnalysis.mapLayer",
                    "走线预览 · {0}",
                    AppLocalization.Literal(DrivingDynamicsAnalyzer.LayerName(dynamicsLayer))),
            17,
            FontWeights.SemiBold);
        mapTitle.Margin = new Thickness(0, 20, 0, 8);
        stack.Children.Add(mapTitle);
        stack.Children.Add(new Border
        {
            Height = 520,
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new TrackMapView(
                laps,
                track,
                legendEntries,
                cornerAnnotations,
                dynamicsLapId)
            {
                DynamicsLayer = dynamicsLayer
            }
        });

        if (cornerAnnotations.Count > 0)
        {
            var cornerTitle = Label("弯角摘要", 17, FontWeights.SemiBold);
            cornerTitle.Margin = new Thickness(0, 20, 0, 8);
            stack.Children.Add(cornerTitle);
            var cornerPanel = new StackPanel();
            foreach (var corner in cornerAnnotations
                         .OrderByDescending(annotation => annotation.Window.EndS - annotation.Window.StartS)
                         .Take(3))
            {
                var item = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                item.Children.Add(Label(corner.Title, 12, FontWeights.SemiBold));
                var detail = Label(
                    $"{AppLocalization.Literal(corner.Details)} · {AppLocalization.Literal(corner.Hint)}",
                    11,
                    FontWeights.Normal,
                    "MutedBrush");
                detail.TextWrapping = TextWrapping.Wrap;
                item.Children.Add(detail);
                cornerPanel.Children.Add(item);
            }
            stack.Children.Add(new Border
            {
                Background = Brush("PanelBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Child = cornerPanel
            });
            var note = Label(
                "LazyForza 只能提供轻度范围内的分析，仅供参考。",
                11,
                FontWeights.Normal,
                "MutedBrush");
            note.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(note);
        }

        return ReportChrome(stack);
    }

    private static Border BuildLapReportSummary(
        IReadOnlyList<LapRecord> laps,
        IReadOnlyList<LapSeriesLegendEntry> legendEntries,
        bool approximateTiming)
    {
        var panel = new StackPanel();
        for (var index = 0; index < laps.Count; index++)
        {
            var lap = laps[index];
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = LapSeriesPalette.BrushAt(index),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });
            var time = Label(AnalysisTime(lap.TotalSeconds, approximateTiming), 14, FontWeights.SemiBold);
            Grid.SetColumn(time, 1);
            row.Children.Add(time);
            var description = Label(
                index < legendEntries.Count
                    ? $"{legendEntries[index].PrimaryText} · {legendEntries[index].SecondaryText}"
                    : $"{PerformanceClassName(lap.Vehicle.CarClass)} {lap.Vehicle.PerformanceIndex} · {lap.StartedAt.ToLocalTime():MM-dd HH:mm:ss}",
                11,
                FontWeights.Normal,
                "MutedBrush");
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(description, 2);
            row.Children.Add(description);
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
}
