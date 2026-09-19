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
        if (laps.Count == 0)
            return EmptyCard("手动弯道分析", "选择带有遥测样本的圈记录，查看弯道指标。");
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
        var referenceHint = Label("", 12, FontWeights.Normal, "MutedBrush");
        referenceHint.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(referenceHint);
        var markers = new ComboBox { MinWidth = 220 };
        panel.Children.Add(AnalysisField("弯道区间", markers));
        var editor = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
        var mapHint = Label("在走线上依次点击起点、终点。", 13, FontWeights.SemiBold);
        mapHint.Margin = new Thickness(0, 6, 0, 10);
        mapHint.TextWrapping = TextWrapping.Wrap;
        editor.Children.Add(mapHint);
        var mapHost = new Border { Margin = new Thickness(0, 0, 0, 16), CornerRadius = new CornerRadius(8), ClipToBounds = true };
        editor.Children.Add(mapHost);
        TrackMapView? intervalMap = null;
        var pickingStep = 0;
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
        var repick = AnalysisButton("重选起终点");
        var add = AnalysisButton("新增区间");
        foreach (var button in new[] { save, repick, add, remove }) actions.Children.Add(button);
        editor.Children.Add(actions);
        editor.Children.Add(Label("滚轮缩放 · 拖动平移 · 双击复位", 12, FontWeights.Normal, "MutedBrush"));
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
        IReadOnlyList<LapSummary> candidates = track is null ? [] : store.LoadLapHistory(track.Id);
        var currentRevision = track is null ? null : LapTrackRevision.Create(track);
        double distanceLength = 0;
        var corners = new List<ManualCorner>();
        repick.Click += (_, _) => BeginPicking();
        add.Click += (_, _) =>
        {
            markers.SelectedIndex = -1;
            var number = 1;
            while (corners.Any(corner => string.Equals(corner.Name, $"T{number}", StringComparison.OrdinalIgnoreCase))) number++;
            name.Text = $"T{number}";
            remove.IsEnabled = false;
            BeginPicking();
        };
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
            reference.Items.Add(new ComboBoxItem { Content = AppLocalization.Literal("仅分析本圈") });
            var summary = LapSummary.FromRecord(selected);
            var eligibility = ManualCornerAnalyzer.ComparisonEligibility(track, summary);
            var excludedReasons = new HashSet<string>();
            if (eligibility is null && track is not null)
                foreach (var lap in candidates.Where(lap => lap.Id != selected.Id).OrderByDescending(lap => lap.Annotation.IsReference).ThenByDescending(lap => lap.StartedAt))
                {
                    var reason = ManualCornerAnalyzer.Compatibility(track, summary, lap);
                    if (reason is null) reference.Items.Add(new ComboBoxItem { Content = LapCaption(lap), Tag = lap.Id });
                    else excludedReasons.Add(reason);
                }
            referenceHint.Text = eligibility is not null
                ? string.Join(" ", AppLocalization.Literal(eligibility), AppLocalization.Literal("可继续查看本圈弯道指标。"))
                : reference.Items.Count > 1
                    ? AppLocalization.Literal("不选参考圈时显示本圈指标；选择后比较差异。")
                    : string.Join(" ", new[] { AppLocalization.Literal("暂无可比较的参考圈，仍可查看本圈指标。") }
                        .Concat(excludedReasons.Select(AppLocalization.Literal)));
            reference.ToolTip = string.Join("\n", new[] { AppLocalization.Literal("仅列出路线修订、方向、分段版本和车辆条件兼容的真实圈；未记录的调校与天气仍可能影响结果。") }
                .Concat(excludedReasons.Select(AppLocalization.Literal)));
            var currentRoute = track is not null && selected.TrackId == track.Id && selected.Direction == track.Direction &&
                selected.TrackRevision == currentRevision;
            distanceLength = currentRoute ? track!.LengthMeters : selected.Samples.Where(sample => double.IsFinite(sample.S))
                .Select(sample => sample.S).DefaultIfEmpty(0).Max();
            corners.Clear();
            // Unknown or retired geometry uses this lap's distance axis; never attach its markers to today's route.
            key = currentRoute ? $"cornerAnalysis.v1.{track!.Id:N}.{track.Direction}.{selected.SectorSchemaVersion}.{currentRevision}"
                : $"cornerAnalysis.lap.v1.{selected.Id:N}";
            status.Text = string.Empty;
            try
            {
                if (store.GetAppSetting(key) is { } json && JsonSerializer.Deserialize<ManualCorner[]>(json) is { } saved)
                    corners.AddRange(saved.Where(corner => corner is not null && ManualCornerAnalyzer.IsValid(corner, distanceLength)).Take(32));
            }
            catch (JsonException) { status.Text = AppLocalization.Literal("保存的弯道标记无法读取，请重新标记；保存后将替换这些标记。"); }
            RefreshMarkers();
            editSection.IsExpanded = corners.Count == 0;
            intervalMap = new TrackMapView([selected], currentRoute ? track : null)
            {
                Height = 300, ShowLegend = false, ShowEndpoints = false, ShowCornerAnnotations = false,
                Cursor = System.Windows.Input.Cursors.Cross
            };
            System.Windows.Automation.AutomationProperties.SetName(intervalMap, AppLocalization.Literal("弯道区间走线选点"));
            intervalMap.ProgressPicked += PickProgress;
            mapHost.Child = intervalMap;
            if (corners.Count == 0) BeginPicking();
            else UpdateMapInterval();
            reference.SelectedItem = reference.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is Guid id &&
                candidates.Any(lap => lap.Id == id && lap.Annotation.IsReference)) ?? reference.Items[0];
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
            pickingStep = 0;
            mapHint.Text = AppLocalization.Literal("已标记区间，可重选起终点或微调距离。");
            remove.IsEnabled = true;
            UpdateMapInterval();
            Analyze();
        };
        start.TextChanged += (_, _) => UpdateMapInterval();
        end.TextChanged += (_, _) => UpdateMapInterval();
        save.Click += (_, _) =>
        {
            if (!double.TryParse(start.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var from) ||
                !double.TryParse(end.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var to) ||
                !ManualCornerAnalyzer.IsValid(new(name.Text.Trim(), from, to), distanceLength))
            { status.Text = AppLocalization.Literal("请输入名称和本圈距离范围内的有效区间；终点至少比起点大 30 米。"); return; }
            var index = corners.FindIndex(corner => string.Equals(corner.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0 && corners.Count >= 32) { status.Text = AppLocalization.Literal("最多保存 32 个弯道区间。"); return; }
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

        void BeginPicking()
        {
            pickingStep = 1;
            start.Clear();
            end.Clear();
            mapHint.Text = AppLocalization.Literal("点击走线，选择起点 1。");
            editSection.IsExpanded = true;
        }
        void PickProgress(double progress)
        {
            if (pickingStep == 0) return;
            if (pickingStep == 1)
            {
                start.Text = progress.ToString("0.0", CultureInfo.InvariantCulture);
                pickingStep = 2;
                mapHint.Text = AppLocalization.Literal("起点已选，点击走线选择终点 2。");
            }
            else
            {
                if (!TryDistance(start, out var from) || progress - from < 30)
                {
                    mapHint.Text = AppLocalization.Literal("请沿行驶方向选择至少 30 米后的终点；跨终点弯请分开标记。");
                    return;
                }
                end.Text = progress.ToString("0.0", CultureInfo.InvariantCulture);
                pickingStep = 0;
                mapHint.Text = AppLocalization.Literal("区间已选，可微调距离后保存。");
            }
            if (selected is not null) cursor.Set(mapHost, selected.Id, progress);
        }
        void UpdateMapInterval()
        {
            intervalMap?.SetSelectedInterval(TryDistance(start, out var from) ? from : null,
                TryDistance(end, out var to) ? to : null);
        }
        bool TryDistance(TextBox field, out double value) =>
            double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && value >= 0 && value <= distanceLength;

        void Persist()
        {
            store.SetAppSetting(key, JsonSerializer.Serialize(corners));
            status.Text = AppLocalization.Literal("区间已保存。");
            pickingStep = 0;
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
            speedChart = new LapTelemetryChart(series, distanceLength,
                series.Select((lap, index) => new LapSeriesLegendEntry(AppLocalization.Literal(index == 0 ? "分析圈" : "参考圈"), LapCaption(LapSummary.FromRecord(lap)))).ToArray(), cursor) { Height = 300 };
            var pages = new List<(string, Func<UIElement>)> { ("速度", () => speedChart) };
            pages.Add(("分析圈输入", () => new LapInputChart(series[0], distanceLength, cursor) { Height = 220 }));
            if (series.Length > 1) pages.Add(("参考圈输入", () => new LapInputChart(series[1], distanceLength, cursor) { Height = 220 }));
            curveTabs = AnalysisTabs(pages.ToArray());
            curves.Children.Add(curveTabs);
        }
        void Analyze()
        {
            results.Children.Clear();
            if (selected is null) return;
            if (corners.Count == 0) { results.Children.Add(Label("请先标记至少一个弯道区间。", 12)); return; }
            var comparisons = track is not null && referenceLap is not null
                ? corners.OrderBy(corner => corner.StartS).Select(corner => ManualCornerAnalyzer.Compare(track, selected, referenceLap, corner)).ToArray()
                : [];
            var comparison = comparisons.FirstOrDefault(item => (markers.SelectedItem as ComboBoxItem)?.Tag is ManualCorner corner && item.Corner.Name == corner.Name);
            var current = comparison?.Selected is not null ? comparison : (markers.SelectedItem as ComboBoxItem)?.Tag is ManualCorner activeCorner
                ? ManualCornerAnalyzer.Analyze(selected, activeCorner) : null;
            if (current is not null)
            {
                if (current.Selected is { } a)
                {
                    var b = current.Reference;
                    var metrics = new WrapPanel();
                    Metric("区间耗时", b is null ? $"{a.Seconds:0.000} s" : $"{a.Seconds - b.Seconds:+0.000;-0.000;0.000} s",
                        b is null ? AppLocalization.Literal("本圈") : $"{a.Seconds:0.000} / {b.Seconds:0.000} s");
                    Metric("最低速度", $"{a.MinimumSpeedKph:0.0} km/h", b is null ? "" : $"{AppLocalization.Literal("参考")} {b.MinimumSpeedKph:0.0} km/h");
                    Metric("制动起点", Meters(a.BrakeStartS), b is null ? "" : $"{AppLocalization.Literal("参考")} {Meters(b.BrakeStartS)}");
                    Metric("恢复油门", Meters(a.ThrottleRecoveryS), b is null ? "" : $"{AppLocalization.Literal("参考")} {Meters(b.ThrottleRecoveryS)}");
                    results.Children.Add(metrics);
                    void Metric(string title, string value, string detail)
                    {
                        var metric = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 14, 12) };
                        metric.Children.Add(Label(title, 12, FontWeights.Normal, "MutedBrush"));
                        metric.Children.Add(Label(value, 21, FontWeights.SemiBold));
                        if (detail.Length > 0) metric.Children.Add(Label(detail, 12, FontWeights.Normal, "MutedBrush"));
                        metrics.Children.Add(metric);
                    }
                }
                else Note(current.Message);
                if (comparison is not null && comparison.Selected is null && current.Selected is not null)
                    Note(comparison.Evidence == CornerEvidence.Insufficient
                        ? "参考圈在此区间缺少可用样本，已显示本圈指标。" : comparison.Message);
                if (!selected.IsValid) Note("本圈标记为无效；指标仅用于回看驾驶过程。");
                if (current.Evidence == CornerEvidence.Partial)
                    Note("部分输入证据不足，无法确认的位置显示为 —。");
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
            void Note(string message)
            {
                var label = Label(message, 12, FontWeights.Normal, "MutedBrush");
                label.TextWrapping = TextWrapping.Wrap;
                results.Children.Add(label);
            }
        }
        static string Meters(double? value) => value is double s ? $"{s:0.0} m" : "—";
        static string LapCaption(LapSummary lap) => $"{(lap.Annotation.IsReference ? AppLocalization.Literal("固定参考") + " · " : "")}" +
            $"{(lap.Annotation.Name is { } title ? title + " · " : "")}{AnalysisTime(lap.TotalSeconds, false)} · {lap.StartedAt.ToLocalTime():MM-dd HH:mm}";
    }
}
