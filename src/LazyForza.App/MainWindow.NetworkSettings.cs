using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement BuildTelemetryListenerSettings()
    {
        var network = new StackPanel();
        var address = new TextBox { Text = store.GetAppSetting("telemetry.listenAddress") ?? LazyForzaDefaults.TelemetryListenAddress };
        var port = new TextBox { Text = store.GetAppSetting("telemetry.port") ?? LazyForzaDefaults.TelemetryPort.ToString(CultureInfo.InvariantCulture) };
        network.Children.Add(NetworkSettingRow(AppLocalization.Literal("监听地址"), address));
        network.Children.Add(NetworkSettingRow(AppLocalization.Literal("UDP 端口"), port));
        var note = Label("端口范围 1–65535，5200–5300 为保留端口。", 12, FontWeights.Normal, "MutedBrush");
        note.Margin = new Thickness(0, 8, 0, 0); network.Children.Add(note);
        var status = AutoApplyStatus(); network.Children.Add(status);
        var appliedAddress = address.Text;
        var appliedPort = port.Text;
        var automatic = AutoApplySettings(network, async () =>
        {
            if (!IPAddress.TryParse(address.Text.Trim(), out var parsedAddress) ||
                !int.TryParse(port.Text.Trim(), out var parsedPort) || parsedPort is < 1 or > 65535 or >= 5200 and <= 5300)
            {
                status.Text = AppLocalization.Literal("请输入有效 IP 地址和端口。保留端口：5200–5300。");
                status.Visibility = Visibility.Visible;
                return;
            }
            var nextAddress = parsedAddress.ToString();
            var nextPort = parsedPort.ToString(CultureInfo.InvariantCulture);
            if (nextAddress == appliedAddress && nextPort == appliedPort && telemetry.Diagnostics.State != TelemetryStreamState.Faulted)
            { status.Visibility = Visibility.Collapsed; return; }
            if (sourceKind == TelemetrySourceKind.Live && telemetry is ILiveTelemetryConfiguration configurable)
                await configurable.ChangeListenerAsync(nextAddress, parsedPort, lifetimeCancellation.Token);
            store.SetAppSetting("telemetry.listenAddress", nextAddress);
            store.SetAppSetting("telemetry.port", nextPort);
            appliedAddress = nextAddress; appliedPort = nextPort;
            status.Text = AppLocalization.Literal("已保存，使用 Live 模式时生效。");
            status.Visibility = sourceKind == TelemetrySourceKind.Live ? Visibility.Collapsed : Visibility.Visible;
            RefreshSidebar();
            if (sourceChip is not null && sourceKind == TelemetrySourceKind.Live) sourceChip.ToolTip = ConfiguredLiveEndpoint();
        }, status, 650);
        address.TextChanged += (_, _) => automatic.Request();
        port.TextChanged += (_, _) => automatic.Request();
        return Card(network);
    }

    internal static Grid NetworkSettingRow(string title, TextBox editor)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 300 });
        var label = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 12, 0);
        editor.FontSize = 13;
        editor.MinHeight = 38;
        editor.Padding = new Thickness(10, 8, 10, 8);
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.VerticalContentAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(editor, title);
        Grid.SetColumn(editor, 1);
        row.Children.Add(label); row.Children.Add(editor);
        return row;
    }
}
