using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Domain;
using LazyForza.Telemetry;
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
        var note = Label("端口范围 1–65535，建议使用 2299。", 12, FontWeights.Normal, "MutedBrush");
        note.Margin = new Thickness(0, 8, 0, 0); network.Children.Add(note);
        var warning = new TelemetryPortRecommendation(store.GetAppSetting("telemetry.portRecommendationDismissed"),
            value => store.SetAppSetting("telemetry.portRecommendationDismissed", value));
        void RefreshWarning() => warning.Update(port.Text);
        RefreshWarning(); network.Children.Add(warning);
        var status = AutoApplyStatus(); network.Children.Add(status);
        var appliedAddress = address.Text;
        var appliedPort = port.Text;
        var automatic = AutoApplySettings(network, async () =>
        {
            if (!TelemetryNetworkPolicy.TryParseListener(address.Text, port.Text, out var parsedAddress, out var parsedPort))
            {
                status.Text = AppLocalization.Literal("请输入有效 IP 地址和 1–65535 端口。");
                status.Visibility = Visibility.Visible;
                return;
            }
            var nextAddress = parsedAddress!.ToString();
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
        port.TextChanged += (_, _) => { RefreshWarning(); automatic.Request(); };
        return Card(network);
    }

    private UIElement BuildTelemetryForwardingSettings()
    {
        var hub = telemetry as TelemetryHub;
        var panel = new TelemetryForwardingPanel(TelemetryForwardingSettings.Read(store.GetAppSetting(TelemetryForwardingSettings.Key)), sourceKind,
            async settings =>
            {
                if (hub is null) throw new InvalidOperationException("当前遥测源不支持转发。");
                await hub.ChangeForwardingAsync(settings, lifetimeCancellation.Token);
                store.SetAppSetting(TelemetryForwardingSettings.Key, TelemetryForwardingSettings.Write(settings));
            }, () => hub?.ForwardingStatus ?? new(false, 0, 0, 0, null),
            store.GetAppSetting("telemetry.forwardingRecommendationDismissed"),
            value => store.SetAppSetting("telemetry.forwardingRecommendationDismissed", value));
        settingApplications.Add(panel.Application);
        refreshVisiblePage = panel.Refresh;
        return Card(panel);
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
