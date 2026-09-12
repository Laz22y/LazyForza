using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    internal static Border BuildManualCornerAnalysisCard(LazyForzaStore store, TrackTemplate? track, IReadOnlyList<LapRecord> laps,
        Action<Guid, double>? navigate = null)
    {
        if (track is null || laps.Count == 0)
            return EmptyCard("手动弯道分析", "需要带有已保存赛道信息的完整圈；无法确认路线版本的原始回放不生成弯道差异。");
        var panel = new StackPanel();
        var selectors = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        selectors.ColumnDefinitions.Add(new ColumnDefinition());
        selectors.ColumnDefinitions.Add(new ColumnDefinition());
        var target = new ComboBox { HorizontalContentAlignment = HorizontalAlignment.Left };
        var reference = new ComboBox { HorizontalContentAlignment = HorizontalAlignment.Left };
        foreach (var lap in laps) target.Items.Add(new ComboBoxItem { Content = LapCaption(LapSummary.FromRecord(lap)), Tag = lap });
        selectors.Children.Add(AnalysisField("分析圈", target));
        var referenceField = AnalysisField("参考圈", reference);
        Grid.SetColumn(referenceField, 1);
        selectors.Children.Add(referenceField);
        panel.Children.Add(selectors);
        var markers = new ComboBox { MinWidth = 220 };
        panel.Children.Add(AnalysisField("弯道区间", markers));
        var editor = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
        var fields = new WrapPanel();
        var name = new TextBox { Text = "T1", MaxLength = 40 };
        var start = new TextBox { Text = "0" };
        var end = new TextBox { Text = "100" };
        fields.Children.Add(AnalysisField("弯名", name, 140));
        fields.Children.Add(AnalysisField("起点 · m", start, 110));
        fields.Children.Add(AnalysisField("终点 · m", end, 110));
        editor.Children.Add(fields);
        var actions = new WrapPanel();
        var save = AnalysisButton("保存区间", primary: true);
        var remove = AnalysisButton("删除区间");
        var markStart = AnalysisButton("曲线标记起点");
        var markEnd = AnalysisButton("曲线标记终点");
        foreach (var button in new[] { save, markStart, markEnd, remove }) actions.Children.Add(button);
        editor.Children.Add(actions);
        editor.Children.Add(Label("区间至少 30 米；跨终点弯请分开标记。", 12, FontWeights.Normal, "MutedBrush"));
        var editSection = AnalysisDisclosure("编辑弯道区间", editor);
        panel.Children.Add(editSection);
        var status = Label("", 12, FontWeights.Normal, "MutedBrush");
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);
        var results = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        var curves = new StackPanel();
        panel.Children.Add(results);
        panel.Children.Add(curves);
        var cursor = new LapAnalysisCursor();
        TabControl? curveTabs = null;
        TextBox? marking = null;
        cursor.CommitRequested += (_, position) =>
        {
            if (marking is not null)
            {
                marking.Text = position.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture);
                marking = null;
                status.Text = AppLocalization.Literal("位置已填写，点击“保存区间”保留标记。");
            }
        };
        markStart.Click += (_, _) => { marking = start; if (curveTabs is not null) curveTabs.SelectedIndex = 0; status.Text = AppLocalization.Literal("请在下方曲线上点击弯道起点。"); };
        markEnd.Click += (_, _) => { marking = end; if (curveTabs is not null) curveTabs.SelectedIndex = 0; status.Text = AppLocalization.Literal("请在下方曲线上点击弯道终点。"); };
        var candidates = store.LoadLapSummaries(track.Id);
        var corners = new List<ManualCorner>();
        string key = "";
        LapRecord? selected = null;
        LapRecord? referenceLap = null;
        LapTelemetryChart? speedChart = null;
        target.SelectionChanged += (_, _) =>
        {
            selected = (target.SelectedItem as ComboBoxItem)?.Tag as LapRecord;
            if (selected is null) return;
            referenceLap = null;
            reference.Items.Clear();
            reference.Items.Add(new ComboBoxItem { Content = AppLocalization.Literal("选择真实参考圈…") });
            foreach (var lap in candidates.Where(lap => ManualCornerAnalyzer.Compatibility(track, LapSummary.FromRecord(selected), lap) is null))
                reference.Items.Add(new ComboBoxItem { Content = LapCaption(lap), Tag = lap.Id });
            corners.Clear();
            key = $"cornerAnalysis.v1.{track.Id:N}.{track.Direction}.{selected.SectorSchemaVersion}.{LapTrackRevision.Create(track)}";
            try
            {
                if (store.GetAppSetting(key) is { } json && JsonSerializer.Deserialize<ManualCorner[]>(json) is { } saved)
                    corners.AddRange(saved.Where(corner => corner is not null && ManualCornerAnalyzer.IsValid(corner, track.LengthMeters)).Take(32));
                status.Text = reference.Items.Count == 1
                    ? AppLocalization.Literal("暂无兼容参考圈。需要同路线版本、方向和车辆条件的有效圈。")
                    : string.Empty;
                reference.ToolTip = AppLocalization.Literal("仅列出路线修订、方向、分段版本和车辆条件兼容的真实圈；未记录的调校与天气仍可能影响结果。");
            }
            catch (JsonException) { status.Text = AppLocalization.Literal("保存的弯道标记无法读取，请重新标记；保存后将替换这些标记。"); }
            RefreshMarkers();
            editSection.IsExpanded = corners.Count == 0;
            reference.SelectedIndex = 0;
        };
        reference.SelectionChanged += (_, _) =>
        {
            referenceLap = (reference.SelectedItem as ComboBoxItem)?.Tag is Guid id ? store.LoadLap(id) : null;
            RenderCurves();
            Analyze();
        };
        markers.SelectionChanged += (_, _) =>
        {
            if ((markers.SelectedItem as ComboBoxItem)?.Tag is not ManualCorner corner) return;
            name.Text = corner.Name;
            start.Text = corner.StartS.ToString("0.0", CultureInfo.InvariantCulture);
            end.Text = corner.EndS.ToString("0.0", CultureInfo.InvariantCulture);
            Analyze();
        };
        save.Click += (_, _) =>
        {
            if (!double.TryParse(start.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var from) ||
                !double.TryParse(end.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var to) ||
                !ManualCornerAnalyzer.IsValid(new(name.Text.Trim(), from, to), track.LengthMeters))
            { status.Text = AppLocalization.Literal("请输入名称和赛道范围内的有效距离；终点至少比起点大 30 米。"); return; }
            var index = corners.FindIndex(corner => string.Equals(corner.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0 && corners.Count >= 32) { status.Text = AppLocalization.Literal("每个赛道版本最多保存 32 个弯道区间。"); return; }
            var next = new ManualCorner(name.Text.Trim(), from, to);
            if (index >= 0) corners[index] = next; else corners.Add(next);
            Persist();
        };
        remove.Click += (_, _) =>
        {
            if ((markers.SelectedItem as ComboBoxItem)?.Tag is not ManualCorner corner) return;
            corners.Remove(corner);
            Persist();
        };
        target.SelectedIndex = 0;
        var card = AnalysisCard(panel);
        ApplyAnalysisTheme(card);
        return card;

        void Persist()
        {
            store.SetAppSetting(key, JsonSerializer.Serialize(corners));
            status.Text = AppLocalization.Literal("区间已保存。");
            RefreshMarkers();
            editSection.IsExpanded = false;
            Analyze();
        }
        void RefreshMarkers()
        {
            var selectedName = (markers.SelectedItem as ComboBoxItem)?.Tag is ManualCorner current ? current.Name : name.Text;
            markers.Items.Clear();
            foreach (var corner in corners.OrderBy(corner => corner.StartS))
                markers.Items.Add(new ComboBoxItem { Content = $"{corner.Name} · {corner.StartS:0.0}–{corner.EndS:0.0} m", Tag = corner });
            markers.SelectedItem = markers.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is ManualCorner corner && corner.Name == selectedName)
                                   ?? markers.Items.OfType<ComboBoxItem>().FirstOrDefault();
            remove.IsEnabled = corners.Count > 0;
        }
        void RenderCurves()
        {
            curves.Children.Clear();
            if (selected is null) return;
            var series = referenceLap is null ? new[] { selected } : new[] { selected, referenceLap };
            speedChart = new LapTelemetryChart(series, track.LengthMeters,
                series.Select((lap, index) => new LapSeriesLegendEntry(AppLocalization.Literal(index == 0 ? "分析圈" : "参考圈"), LapCaption(LapSummary.FromRecord(lap)))).ToArray(), cursor) { Height = 300 };
            var pages = new List<(string, Func<UIElement>)> { ("速度", () => speedChart) };
            pages.Add(("分析圈输入", () => new LapInputChart(series[0], track.LengthMeters, cursor) { Height = 220 }));
            if (series.Length > 1) pages.Add(("参考圈输入", () => new LapInputChart(series[1], track.LengthMeters, cursor) { Height = 220 }));
            curveTabs = AnalysisTabs(pages.ToArray());
            curves.Children.Add(curveTabs);
        }
        void Analyze()
        {
            results.Children.Clear();
            if (selected is null || referenceLap is null) { results.Children.Add(Label("选择参考圈后生成弯道差异。", 12)); return; }
            if (corners.Count == 0) { results.Children.Add(Label("请先标记至少一个弯道区间。", 12)); return; }
            var comparisons = corners.OrderBy(corner => corner.StartS).Select(corner => ManualCornerAnalyzer.Compare(track, selected, referenceLap, corner)).ToArray();
            var comparison = comparisons.FirstOrDefault(item => (markers.SelectedItem as ComboBoxItem)?.Tag is ManualCorner corner && item.Corner.Name == corner.Name);
            if (comparison is not null)
            {
                if (comparison.Selected is { } a && comparison.Reference is { } b)
                {
                    var metrics = new WrapPanel();
                    Metric("区间耗时", $"{a.Seconds - b.Seconds:+0.000;-0.000;0.000} s", $"{a.Seconds:0.000} / {b.Seconds:0.000} s");
                    Metric("最低速度", $"{a.MinimumSpeedKph:0.0} km/h", $"{AppLocalization.Literal("参考")} {b.MinimumSpeedKph:0.0} km/h");
                    Metric("制动起点", Meters(a.BrakeStartS), $"{AppLocalization.Literal("参考")} {Meters(b.BrakeStartS)}");
                    Metric("恢复油门", Meters(a.ThrottleRecoveryS), $"{AppLocalization.Literal("参考")} {Meters(b.ThrottleRecoveryS)}");
                    results.Children.Add(metrics);
                    void Metric(string title, string value, string detail)
                    {
                        var metric = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 14, 12) };
                        metric.Children.Add(Label(title, 12, FontWeights.Normal, "MutedBrush"));
                        metric.Children.Add(Label(value, 21, FontWeights.SemiBold));
                        metric.Children.Add(Label(detail, 12, FontWeights.Normal, "MutedBrush"));
                        metrics.Children.Add(metric);
                    }
                }
                else results.Children.Add(Label(comparison.Message, 13, FontWeights.Normal, "MutedBrush"));
                if (comparison.Evidence == CornerEvidence.Partial)
                    results.Children.Add(Label("部分输入证据不足，无法确认的位置显示为 —。", 12, FontWeights.Normal, "MutedBrush"));
            }
            foreach (var difference in ManualCornerAnalyzer.Describe(comparisons))
            {
                var jump = AnalysisButton("");
                var jumpRow = new Grid();
                jumpRow.ColumnDefinitions.Add(new ColumnDefinition());
                jumpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                jumpRow.Children.Add(new TextBlock { Text = AppLocalization.Literal(difference.Text),
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
                var arrow = new TextBlock { Text = "→", FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(arrow, 1);
                jumpRow.Children.Add(arrow);
                jump.Content = jumpRow;
                jump.ToolTip = AppLocalization.Literal("查看曲线 →");
                System.Windows.Automation.AutomationProperties.SetName(jump, AppLocalization.Literal("查看曲线 →"));
                jump.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                jump.Click += (_, _) =>
                {
                    cursor.Set(jump, selected.Id, difference.ProgressMeters);
                    if (curveTabs is not null) curveTabs.SelectedIndex = 0;
                    speedChart?.BringIntoView();
                    navigate?.Invoke(selected.Id, difference.ProgressMeters);
                };
                results.Children.Add(jump);
            }
        }
        static string Meters(double? value) => value is double s ? $"{s:0.0} m" : "—";
        static string LapCaption(LapSummary lap) => $"{AnalysisTime(lap.TotalSeconds, false)} · {lap.StartedAt.ToLocalTime():MM-dd HH:mm}";
    }
}
