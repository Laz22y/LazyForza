using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private Border BuildRecordingSettingsCard()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        var current = recorder.AutomaticOptions;
        var panel = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Label("比赛自动录制", 17, FontWeights.SemiBold));
        var summary = Label(
            "仅在 Live 模式生效；自动保留赛前 15 秒和赛后 10 秒，便于完整回放识别与比赛起止过程。",
            11,
            FontWeights.Normal,
            "MutedBrush");
        summary.Margin = new Thickness(0, 4, 20, 0);
        heading.Children.Add(summary);
        header.Children.Add(heading);

        var enabled = new ToggleButton
        {
            IsChecked = current.Enabled,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12, 7, 12, 7)
        };
        void RefreshEnabledText() =>
            enabled.Content = enabled.IsChecked == true ? "自动录制：开" : "自动录制：关";
        enabled.Click += (_, _) => RefreshEnabledText();
        RefreshEnabledText();
        Grid.SetColumn(enabled, 1);
        header.Children.Add(enabled);
        panel.Children.Add(header);

        var choices = new Grid { Margin = new Thickness(0, 16, 0, 8) };
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var capacityLabel = Label("录制容量上限", 12, FontWeights.SemiBold);
        capacityLabel.VerticalAlignment = VerticalAlignment.Center;
        choices.Children.Add(capacityLabel);
        var capacity = new ComboBox
        {
            SelectedValuePath = nameof(ComboBoxItem.Tag),
            MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var value in new[] { 1L, 2L, 5L, 10L, 20L, 50L })
            capacity.Items.Add(new ComboBoxItem { Content = $"{value} GiB", Tag = value });
        capacity.SelectedValue = Math.Max(1, current.MaximumBytes / gibibyte);
        if (capacity.SelectedIndex < 0) capacity.SelectedValue = 5L;
        Grid.SetColumn(capacity, 1);
        choices.Children.Add(capacity);

        var reserveLabel = Label("磁盘保留空间", 12, FontWeights.SemiBold);
        reserveLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(reserveLabel, 3);
        choices.Children.Add(reserveLabel);
        var reserve = new ComboBox
        {
            SelectedValuePath = nameof(ComboBoxItem.Tag),
            MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var value in new[] { 2L, 5L, 10L, 20L })
            reserve.Items.Add(new ComboBoxItem { Content = $"{value} GiB", Tag = value });
        reserve.SelectedValue = Math.Max(1, current.MinimumFreeBytes / gibibyte);
        if (reserve.SelectedIndex < 0) reserve.SelectedValue = 5L;
        Grid.SetColumn(reserve, 4);
        choices.Children.Add(reserve);
        panel.Children.Add(choices);

        var rotate = new CheckBox
        {
            IsChecked = current.RotateOldest,
            Content = "达到上限后轮换最旧的普通自动录制",
            Margin = new Thickness(0, 7, 0, 0)
        };
        panel.Children.Add(rotate);
        var rotationNote = Label(
            "默认关闭。开启后仍会保留最近 5 场，并跳过手动固定、个人最佳圈和赛道识别异常样本。",
            10,
            FontWeights.Normal,
            "MutedBrush");
        rotationNote.Margin = new Thickness(24, 2, 0, 0);
        panel.Children.Add(rotationNote);

        var status = Label(
            $"{recorder.AutomaticStatus}\n当前录制占用 {FormatBytes(recorder.RecordingBytes)} · 目录 {directories.RecordingsPath}",
            11,
            FontWeights.Normal,
            "MutedBrush");
        status.Margin = new Thickness(0, 13, 0, 8);
        panel.Children.Add(status);

        var actions = new WrapPanel();
        var save = new Button
        {
            Content = "应用录制设置",
            Padding = new Thickness(16, 8, 16, 8)
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var maximumGiB = capacity.SelectedValue is long selectedMaximum ? selectedMaximum : 5;
                var reserveGiB = reserve.SelectedValue is long selectedReserve ? selectedReserve : 5;
                await recorder.SetAutomaticOptionsAsync(
                    new AutomaticRecordingOptions(
                        enabled.IsChecked == true,
                        maximumGiB * gibibyte,
                        rotate.IsChecked == true,
                        reserveGiB * gibibyte,
                        15,
                        10),
                    CancellationToken.None);
                RenderSelectedPage();
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        actions.Children.Add(save);
        var openFolder = new Button
        {
            Content = "打开录制目录",
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(14, 8, 14, 8)
        };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(directories.RecordingsPath);
            Process.Start(new ProcessStartInfo(directories.RecordingsPath) { UseShellExecute = true });
        };
        actions.Children.Add(openFolder);
        panel.Children.Add(actions);

        var recent = recorder.Recordings.Take(8).ToArray();
        if (recent.Length > 0)
        {
            var recentTitle = Label("最近录制", 13, FontWeights.SemiBold);
            recentTitle.Margin = new Thickness(0, 18, 0, 6);
            panel.Children.Add(recentTitle);
            foreach (var entry in recent)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var protection = entry.IsPinned
                    ? " · 已固定"
                    : entry.IsProtected(DateTimeOffset.UtcNow)
                        ? $" · {entry.ProtectionReason}"
                        : string.Empty;
                row.Children.Add(Label(
                    $"{entry.CreatedAt.ToLocalTime():MM-dd HH:mm} · {entry.TrackName ?? "未识别赛道"} · " +
                    $"{FormatDuration(entry.DurationSeconds)} · {FormatFileSize(entry.RecordingPath)}{protection}",
                    11));
                var pin = new Button
                {
                    Content = entry.IsPinned ? "取消固定" : "固定",
                    Padding = new Thickness(10, 4, 10, 4),
                    Tag = entry
                };
                pin.Click += (_, _) =>
                {
                    if (pin.Tag is not RecordingCatalogEntry selected) return;
                    recorder.SetPinned(selected.RecordingPath, !selected.IsPinned);
                    RenderSelectedPage();
                };
                Grid.SetColumn(pin, 1);
                row.Children.Add(pin);
                panel.Children.Add(row);
            }
        }

        return Card(panel);

        static string FormatBytes(long bytes) =>
            bytes >= gibibyte
                ? $"{bytes / (double)gibibyte:0.00} GiB"
                : $"{bytes / (1024d * 1024):0.0} MiB";

        static string FormatDuration(double seconds) =>
            seconds <= 0 ? "时长待读取" : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

        static string FormatFileSize(string path) =>
            File.Exists(path) ? FormatBytes(new FileInfo(path).Length) : "文件缺失";
    }
}
