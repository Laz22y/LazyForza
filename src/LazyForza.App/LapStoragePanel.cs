using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed class LapStoragePanel : StackPanel
{
    private readonly LazyForzaStore store;
    private readonly TextBox capacity;
    private readonly TextBlock status, summary, details, estimate;
    private LapStorageUsage? usage;
    private readonly bool english;

    internal LapStoragePanel(LazyForzaStore store, bool english)
    {
        this.store = store; this.english = english;
        Children.Add(Text(T("圈记录存储", "Lap record storage"), 17));
        summary = Text(T("正在统计…", "Calculating…"), 12, true); Children.Add(summary);
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var label = Text(T("每赛道保留目标", "Lap target per track"), 13);
        label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(label);
        capacity = new TextBox { Text = store.LapCapacity.ToString(CultureInfo.InvariantCulture), Width = 100, MaxLength = 5,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(capacity, label.Text);
        capacity.ToolTip = T("1–10,000 圈；默认 500 圈", "1–10,000 laps; default 500");
        row.Children.Add(capacity);
        var save = new Button { Content = T("保存", "Save"), MinHeight = 36, Padding = new Thickness(16, 5, 16, 5) };
        row.Children.Add(save); Children.Add(row);
        status = Text("", 12, true); Children.Add(status);
        Children.Add(Text(T("自动清理保留收藏、固定参考与各车型及可观测车辆条件下的代表圈，并保留其整场比赛。保护记录和完整比赛可超出目标。",
            "Cleanup keeps favorites, pinned references and representative laps for each car and observed configuration, including their entire races. Protected records and whole races may exceed the target."), 12, true));
        var body = new StackPanel { Margin = new Thickness(2, 0, 2, 6) };
        details = Text("", 12, true); body.Children.Add(details);
        estimate = Text("", 12, true); body.Children.Add(estimate);
        body.Children.Add(Text(T("估算不含索引与空闲页；数据库还包含赛道、设置等数据。清理后的空闲页会被后续记录复用，文件不一定立即缩小。原始遥测录制另行管理。",
            "Estimates exclude indexes and free pages. The database also contains tracks and settings. Cleanup frees pages for reuse; the file may not shrink immediately. Raw telemetry recordings are managed separately."), 12, true));
        Children.Add(new Expander { Header = T("空间用量", "Storage usage"), Content = body, IsExpanded = false,
            Style = (Style)Application.Current.Resources["SettingsSectionExpander"], Margin = new Thickness(0, 10, 0, 0) });
        capacity.TextChanged += (_, _) => UpdateEstimate();
        save.Click += (_, _) =>
        {
            if (!int.TryParse(capacity.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is < 1 or > LazyForzaStore.MaximumLapCapacity)
            { status.Text = T("请输入 1–10,000 之间的整数。", "Enter a whole number from 1 to 10,000."); return; }
            store.SetLapCapacity(value);
            status.Text = T("已保存；从下次保存新圈起应用，不立即删除现有记录。", "Saved. Applies when the next lap is saved; existing records are not deleted now.");
        };
        Loaded += async (_, _) =>
        {
            try { SetUsage(await Task.Run(store.GetLapStorageUsage)); }
            catch (Exception) { summary.Text = T("空间统计暂不可用。", "Storage usage is unavailable."); }
        };
    }

    internal void SetUsage(LapStorageUsage value)
    {
        usage = value;
        summary.Text = T($"共 {value.Laps:N0} 圈 · 圈数据估算 {Bytes(value.EstimatedLapBytes)}", $"{value.Laps:N0} laps · estimated lap data {Bytes(value.EstimatedLapBytes)}");
        details.Text = T($"数据库占用 {Bytes(value.DatabaseBytes)} · 可复用空间 {Bytes(value.ReusableBytes)}",
            $"Database allocation {Bytes(value.DatabaseBytes)} · reusable space {Bytes(value.ReusableBytes)}");
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        estimate.Text = usage is { Laps: > 0 } && int.TryParse(capacity.Text, out var count) && count is > 0 and <= LazyForzaStore.MaximumLapCapacity
            ? T($"按当前平均单圈估算，每赛道 {count:N0} 圈约 {Bytes((long)((double)usage.EstimatedLapBytes / usage.Laps * count))}；实际随圈长和样本量变化。",
                $"At the current average, {count:N0} laps per track use about {Bytes((long)((double)usage.EstimatedLapBytes / usage.Laps * count))}. Actual usage varies with lap length and samples.")
            : T("保存圈记录后可估算目标容量。", "A capacity estimate will be available after laps are saved.");
    }

    private static string Bytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):0.00} GiB" : $"{value / (1024d * 1024):0.0} MiB";
    private string T(string zh, string en) => english ? en : zh;
    private static TextBlock Text(string value, int size, bool muted = false) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };
}
