using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private UIElement OverviewPage()
    {
        var stack = PageStack("概览", "查看数据连接、HUD 与本地记录。");
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 14) };
        var streamValue = Label(string.Empty, 24, FontWeights.SemiBold);
        var streamDetail = Label(string.Empty, 12, FontWeights.Normal, "MutedBrush");
        var rateValue = Label(string.Empty, 24, FontWeights.SemiBold);
        var rateDetail = Label(string.Empty, 12, FontWeights.Normal, "MutedBrush");
        var dataValue = Label(string.Empty, 24, FontWeights.SemiBold);
        var dataDetail = Label(string.Empty, 12, FontWeights.Normal, "MutedBrush");
        cards.Children.Add(MetricCard("数据流", streamValue, streamDetail));
        cards.Children.Add(MetricCard("包速率", rateValue, rateDetail));
        cards.Children.Add(MetricCard("本地数据", dataValue, dataDetail));
        stack.Children.Add(cards);
        stack.Children.Add(Card(Label(sourceKind == TelemetrySourceKind.Live
            ? "正在接收 Live UDP。菜单、暂停或数据中断时，HUD 会自动隐藏。"
            : "正在使用模拟数据，不会写入 Live 记录。", 15)));
        var quick = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var moduleLabels = new List<(ILazyForzaModule Module, TextBlock Label)>();
        foreach (var module in moduleManager.Modules)
        {
            var moduleLabel = Label(string.Empty, 14, FontWeights.SemiBold, "MutedBrush");
            moduleLabel.Margin = new Thickness(0, 0, 18, 0);
            moduleLabels.Add((module, moduleLabel));
            quick.Children.Add(moduleLabel);
        }
        stack.Children.Add(quick);
        refreshVisiblePage = () =>
        {
            var diagnostics = telemetry.Diagnostics;
            streamValue.Text = TelemetryStateText(diagnostics.State);
            streamDetail.Text = diagnostics.LastPacketAt?.ToLocalTime().ToString("HH:mm:ss") ?? "等待数据";
            rateValue.Text = diagnostics.PacketsPerSecond.ToString("0.0 Hz");
            rateDetail.Text = $"{diagnostics.ValidPackets:N0} 个有效包 · {diagnostics.InvalidPackets:N0} 个无效包";
            if (pageRefresh.ShouldRefreshOverviewStorage(DateTimeOffset.UtcNow))
            {
                pageRefresh.UpdateOverviewStorage(
                    store.CountLaps(CurrentTrackSource),
                    store.CountTracks(CurrentTrackSource),
                    DateTimeOffset.UtcNow);
            }
            dataValue.Text = $"{pageRefresh.OverviewLapCount} 圈";
            dataDetail.Text = $"{pageRefresh.OverviewTrackCount} 条赛道";
            foreach (var (module, label) in moduleLabels)
            {
                label.Text = $"{module.Descriptor.DisplayName} · {ModuleStateText(module.Status.State)}";
                label.Foreground = Brush(module.Status.State == ModuleRuntimeState.Running ? "AccentBrush" : "MutedBrush");
            }
        };
        pageRefresh.InvalidateOverviewStorage();
        refreshVisiblePage();
        return Scroll(stack);
    }

    private UIElement ModulesPage()
    {
        var stack = PageStack("模块", "启用或停用功能。关闭模块会同时停止订阅、后台任务和 HUD。");
        foreach (var module in moduleManager.Modules)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var description = new StackPanel();
            description.Children.Add(Label(module.Descriptor.DisplayName, 17, FontWeights.SemiBold));
            description.Children.Add(Label(module.Descriptor.Description, 13, FontWeights.Normal, "MutedBrush"));
            description.Children.Add(Label($"状态：{ModuleStateText(module.Status.State)}" + (module.Status.LastError is null ? string.Empty : $" · {module.Status.LastError}"), 12,
                FontWeights.Normal, module.Status.State == ModuleRuntimeState.Faulted ? "AccentBrush" : "MutedBrush"));
            row.Children.Add(description);
            var toggle = new ToggleButton { Content = module.Status.IsEnabled ? "已启用" : "已停用", IsChecked = module.Status.IsEnabled, MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
            toggle.Click += async (_, _) =>
            {
                changingModule = true;
                toggle.IsEnabled = false;
                try
                {
                    await moduleManager.SetEnabledAsync(module.Descriptor.Id, toggle.IsChecked == true, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message, "模块切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    changingModule = false;
                    RenderSelectedPage();
                }
            };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            stack.Children.Add(Card(row));
        }

        return Scroll(stack);
    }
}
