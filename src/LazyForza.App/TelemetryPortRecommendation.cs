using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LazyForza.Telemetry;

namespace LazyForza.App;

/// <summary>A dismissible recommendation; never participates in endpoint validation.</summary>
internal sealed class TelemetryPortRecommendation : Grid
{
    private int? dismissedPort;
    private int? currentPort;
    private readonly Action<string> saveDismissal;

    internal TelemetryPortRecommendation(string? savedDismissal = null, Action<string>? saveDismissal = null)
    {
        dismissedPort = int.TryParse(savedDismissal, out var saved) ? saved : null;
        this.saveDismissal = saveDismissal ?? (_ => { });
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 6, 0, 0);
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = AppLocalization.Literal("5200–5300 可能与游戏使用的端口冲突，建议更换；仍可使用。"),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        var close = new Button { Content = "×", Padding = new Thickness(8, 2, 8, 2), MinWidth = 28,
            ToolTip = AppLocalization.Literal("忽略此建议"), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(close, AppLocalization.Literal("忽略此建议"));
        close.Click += (_, _) =>
        {
            dismissedPort = currentPort;
            this.saveDismissal(dismissedPort?.ToString(CultureInfo.InvariantCulture) ?? "");
            Visibility = Visibility.Collapsed;
        };
        SetColumn(close, 1); Children.Add(text); Children.Add(close);
    }

    internal void Update(string port)
    {
        currentPort = int.TryParse(port, out var value) && value is >= 1 and <= 65535 ? value : null;
        if (currentPort is not null && dismissedPort is not null && currentPort != dismissedPort)
        {
            dismissedPort = null;
            saveDismissal("");
        }
        Visibility = currentPort is int number && number != dismissedPort && TelemetryNetworkPolicy.IsDiscouragedPort(number)
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
