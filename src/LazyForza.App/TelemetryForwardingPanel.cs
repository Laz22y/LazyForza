using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.App;

internal sealed class TelemetryForwardingPanel : StackPanel
{
    private readonly ToggleButton enabled;
    private readonly TextBox address, port;
    private readonly TextBlock status = Copy("", 11), error = Copy("", 11);
    private readonly TelemetryPortRecommendation warning;
    private readonly StackPanel destination = new();
    private TelemetryForwardingOptions applied;
    private readonly Func<TelemetryForwardingStatus> readStatus;
    private readonly TelemetrySourceKind source;
    internal SettingsAutoApply Application { get; }

    internal TelemetryForwardingPanel(TelemetryForwardingOptions options, TelemetrySourceKind source,
        Func<TelemetryForwardingOptions, Task> apply, Func<TelemetryForwardingStatus> readStatus,
        string? dismissedPort = null, Action<string>? saveDismissal = null)
    {
        applied = options; this.readStatus = readStatus; this.source = source;
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = Copy("遥测转发", 17); title.FontWeight = FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); header.Children.Add(title);
        enabled = new ToggleButton { IsChecked = options.Enabled, MinWidth = 76, Padding = new Thickness(12, 7, 12, 7) };
        AutomationProperties.SetName(enabled, AppLocalization.Literal("遥测转发"));
        Grid.SetColumn(enabled, 1); header.Children.Add(enabled); Children.Add(header);
        Children.Add(Copy("将 Live 原始 UDP 包发送给其他软件或设备；模拟与回放不转发。", 12));
        address = new TextBox { Text = options.Address ?? "127.0.0.1" };
        port = new TextBox { Text = options.Port.ToString(CultureInfo.InvariantCulture) };
        destination.Children.Add(MainWindow.NetworkSettingRow(AppLocalization.Literal("目标 IP"), address));
        destination.Children.Add(MainWindow.NetworkSettingRow(AppLocalization.Literal("目标 UDP 端口"), port));
        destination.Children.Add(Copy("同机软件使用 127.0.0.1；外设填写其 IP。目标端口须与接收软件一致。", 11));
        warning = new TelemetryPortRecommendation(dismissedPort, saveDismissal); destination.Children.Add(warning);
        destination.Children.Add(status); Children.Add(destination);
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); error.Visibility = Visibility.Collapsed; Children.Add(error);
        Application = new SettingsAutoApply(async () =>
        {
            TelemetryForwardingOptions next;
            if (enabled.IsChecked != true) next = applied with { Enabled = false };
            else
            {
                if (!TelemetryNetworkPolicy.TryParseListener(address.Text, port.Text, out var ip, out var number))
                    throw new ArgumentException("请输入有效目标 IP 地址和 1–65535 端口。");
                next = new(true, ip!.ToString(), number);
            }
            await apply(next);
            applied = next; error.Visibility = Visibility.Collapsed; Refresh();
        }, exception =>
        {
            error.Text = AppLocalization.Format("settings.auto.failed", "未能应用：{0}", AppLocalization.Literal(exception.Message));
            error.Visibility = Visibility.Visible;
        }, TimeSpan.FromMilliseconds(650));
        enabled.Click += (_, _) => { Refresh(); Application.Request(); _ = Application.FlushAsync(); };
        address.TextChanged += (_, _) => { if (enabled.IsChecked == true) Application.Request(); };
        port.TextChanged += (_, _) => { Refresh(); if (enabled.IsChecked == true) Application.Request(); };
        Refresh();
    }

    internal void Refresh()
    {
        var active = enabled.IsChecked == true;
        enabled.Content = AppLocalization.Literal(active ? "已开启" : "已关闭");
        destination.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        warning.Update(port.Text);
        var value = readStatus();
        status.Text = AppLocalization.Literal(source != TelemetrySourceKind.Live ? "已保存，切换到 Live 后转发。" :
            !value.Enabled ? "尚未启用，请检查目标配置。" : value.LastError is not null ? "目标发送失败，将自动重试；遥测接收不受影响。" :
            value.Sent == 0 ? "已启用，等待 Live 遥测。" : "正在转发。UDP 发送成功不代表接收端已确认。");
        if (value.Enabled && source == TelemetrySourceKind.Live)
            status.Text += "\n" + AppLocalization.Format("telemetry.forwarding.counts", "已发送 {0} 包 · 队列丢弃 {1} · 发送错误 {2}", value.Sent, value.Dropped, value.Errors);
    }
    private static TextBlock Copy(string text, double size)
    {
        var label = new TextBlock { Text = AppLocalization.Literal(text), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
}
