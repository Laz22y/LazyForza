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
        panel.Children.Add(Label("手动弯道分析", 17, FontWeights.SemiBold));
        var guidance = Label("手动输入距起点的米数，或先点标记按钮，再点击下方曲线。区间至少 30 米，跨终点弯请分开标记。选择另一条已记录的有效圈作为参考。", 12, FontWeights.Normal, "MutedBrush");
        guidance.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(guidance);
        var selectors = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var target = new ComboBox { MinWidth = 210, MaxWidth = 320, Margin = new Thickness(0, 0, 12, 0) };
        var reference = new ComboBox { MinWidth = 210, MaxWidth = 320 };
        foreach (var lap in laps) target.Items.Add(new ComboBoxItem { Content = $"所选圈 · {LapCaption(LapSummary.FromRecord(lap))}", Tag = lap });
        selectors.Children.Add(target);
        selectors.Children.Add(reference);
        panel.Children.Add(selectors);
        var markers = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(markers);
        var editor = new WrapPanel();
        var name = new TextBox { Text = "T1", Width = 90, MaxLength = 40 };
        var start = new TextBox { Text = "0", Width = 75 };
        var end = new TextBox { Text = "100", Width = 75 };
        var save = Button("保存区间");
        var remove = Button("删除区间");
        var markStart = Button("曲线标记起点");
        var markEnd = Button("曲线标记终点");
        editor.Children.Add(Label("弯名", 12)); editor.Children.Add(name);
        editor.Children.Add(Label(" 起点 m ", 12)); editor.Children.Add(start);
        editor.Children.Add(Label(" 终点 m ", 12)); editor.Children.Add(end);
        editor.Children.Add(save); editor.Children.Add(remove); editor.Children.Add(markStart); editor.Children.Add(markEnd);
        panel.Children.Add(editor);
        var status = Label("", 12, FontWeights.Normal, "MutedBrush");
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);
        var results = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        var curves = new StackPanel();
        panel.Children.Add(results);
        panel.Children.Add(curves);
        var cursor = new LapAnalysisCursor();
        TextBox? marking = null;
        cursor.CommitRequested += (_, position) =>
        {
            if (marking is not null)
            {
                marking.Text = position.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture);
                marking = null;
                status.Text = "位置已填写，点击“保存区间”保留标记。";
            }
        };
        markStart.Click += (_, _) => { marking = start; status.Text = "请在下方曲线上点击弯道起点。"; };
        markEnd.Click += (_, _) => { marking = end; status.Text = "请在下方曲线上点击弯道终点。"; };
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
            reference.Items.Add(new ComboBoxItem { Content = "选择真实参考圈…" });
            foreach (var lap in candidates.Where(lap => ManualCornerAnalyzer.Compatibility(track, LapSummary.FromRecord(selected), lap) is null))
                reference.Items.Add(new ComboBoxItem { Content = $"参考圈 · {LapCaption(lap)}", Tag = lap.Id });
            corners.Clear();
            key = $"cornerAnalysis.v1.{track.Id:N}.{track.Direction}.{selected.SectorSchemaVersion}.{LapTrackRevision.Create(track)}";
            try
            {
                if (store.GetAppSetting(key) is { } json && JsonSerializer.Deserialize<ManualCorner[]>(json) is { } saved)
                    corners.AddRange(saved.Where(corner => corner is not null && ManualCornerAnalyzer.IsValid(corner, track.LengthMeters)).Take(32));
                status.Text = reference.Items.Count == 1
                    ? "暂无兼容参考圈：要求同路线修订、方向、分段版本和车辆条件；旧圈缺少修订或车辆信息时不作比较。"
                    : "车辆条件按型号、Class/PI、驱动及已有可观察配置限定；未记录的调校、天气等差异仍可能影响结果。";
            }
            catch (JsonException) { status.Text = "保存的弯道标记无法读取，请重新标记；保存后将替换这些标记。"; }
            RefreshMarkers();
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
        };
        save.Click += (_, _) =>
        {
            if (!double.TryParse(start.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var from) ||
                !double.TryParse(end.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var to) ||
                !ManualCornerAnalyzer.IsValid(new(name.Text.Trim(), from, to), track.LengthMeters))
            { status.Text = "请输入名称和赛道范围内的有效距离；终点至少比起点大 30 米。"; return; }
            var index = corners.FindIndex(corner => string.Equals(corner.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0 && corners.Count >= 32) { status.Text = "每个赛道版本最多保存 32 个弯道区间。"; return; }
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
        return Card(panel);

        void Persist()
        {
            store.SetAppSetting(key, JsonSerializer.Serialize(corners));
            status.Text = "区间已保存到本机，按赛道修订、方向和分段版本隔离。";
            RefreshMarkers();
            Analyze();
        }
        void RefreshMarkers()
        {
            markers.Items.Clear();
            foreach (var corner in corners.OrderBy(corner => corner.StartS))
                markers.Items.Add(new ComboBoxItem { Content = $"{corner.Name} · {corner.StartS:0.0}–{corner.EndS:0.0} m", Tag = corner });
        }
        void RenderCurves()
        {
            curves.Children.Clear();
            if (selected is null) return;
            var series = referenceLap is null ? new[] { selected } : new[] { selected, referenceLap };
            curves.Children.Add(Label("弯道对比曲线 · 同一距离轴 · 点击定位", 13, FontWeights.SemiBold));
            speedChart = new LapTelemetryChart(series, track.LengthMeters,
                series.Select((lap, index) => new LapSeriesLegendEntry(index == 0 ? "所选圈" : "参考圈", LapCaption(LapSummary.FromRecord(lap)))).ToArray(), cursor) { Height = 240 };
            curves.Children.Add(speedChart);
            for (var i = 0; i < series.Length; i++)
            {
                curves.Children.Add(Label(i == 0 ? "所选圈 · 油门 / 制动 / 方向" : "参考圈 · 油门 / 制动 / 方向", 12));
                curves.Children.Add(new LapInputChart(series[i], track.LengthMeters, cursor) { Height = 170 });
            }
        }
        void Analyze()
        {
            results.Children.Clear();
            if (selected is null || referenceLap is null) { results.Children.Add(Label("选择参考圈后生成弯道差异。", 12)); return; }
            if (corners.Count == 0) { results.Children.Add(Label("请先标记至少一个弯道区间。", 12)); return; }
            var comparisons = corners.OrderBy(corner => corner.StartS).Select(corner => ManualCornerAnalyzer.Compare(track, selected, referenceLap, corner)).ToArray();
            foreach (var comparison in comparisons)
            {
                var text = $"{comparison.Corner.Name} · {comparison.Corner.StartS:0.0}–{comparison.Corner.EndS:0.0} m\n";
                if (comparison.Selected is { } a && comparison.Reference is { } b)
                    text += $"所选 / 参考：耗时 {a.Seconds:0.000} / {b.Seconds:0.000} s；最低速度 {a.MinimumSpeedKph:0.0} / {b.MinimumSpeedKph:0.0} km/h\n" +
                        $"制动起点 {Meters(a.BrakeStartS)} / {Meters(b.BrakeStartS)}；恢复油门 {Meters(a.ThrottleRecoveryS)} / {Meters(b.ThrottleRecoveryS)}\n";
                var row = Label(text + comparison.Message, 12);
                row.TextWrapping = TextWrapping.Wrap;
                row.Margin = new Thickness(0, 4, 0, 6);
                results.Children.Add(row);
            }
            foreach (var difference in ManualCornerAnalyzer.Describe(comparisons))
            {
                var jump = Button("");
                jump.Content = new TextBlock { Text = difference.Text + "  查看曲线 →", TextWrapping = TextWrapping.Wrap };
                jump.HorizontalContentAlignment = HorizontalAlignment.Left;
                jump.Click += (_, _) =>
                {
                    cursor.Set(jump, selected.Id, difference.ProgressMeters);
                    speedChart?.BringIntoView();
                    navigate?.Invoke(selected.Id, difference.ProgressMeters);
                };
                results.Children.Add(jump);
            }
        }
        static Button Button(string text) => new() { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(5, 0, 0, 5) };
        static string Meters(double? value) => value is double s ? $"{s:0.0} m" : "—";
        static string LapCaption(LapSummary lap) => $"{lap.TotalSeconds:0.000} s · {lap.StartedAt.ToLocalTime():MM-dd HH:mm:ss} · {PlayerCodeText(lap.PlayerCode)}";
    }
}
