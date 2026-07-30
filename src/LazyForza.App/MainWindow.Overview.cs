using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.DriftDashboard;
using LazyForza.Modules.LapAnalysis;

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
            if (module is DriftDashboardModule)
            {
                var protection = Label(
                    "开启期间圈速分析保持关闭，接下来的圈速不会写入数据库；关闭后按原偏好恢复。",
                    11,
                    FontWeights.Normal,
                    "MutedBrush");
                protection.Margin = new Thickness(0, 6, 0, 0);
                protection.TextWrapping = TextWrapping.Wrap;
                description.Children.Add(protection);
                var autoCloseDashboard = new ToggleButton
                {
                    Content = AutoCloseDashboardText(
                        moduleActivation.AutoCloseDashboard),
                    IsChecked = moduleActivation.AutoCloseDashboard,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0),
                    Padding = new Thickness(11, 5, 11, 5),
                    ToolTip = "关闭此选项后，漂移仪表盘可以和主仪表盘同时显示。"
                };
                autoCloseDashboard.Click += async (_, _) =>
                {
                    changingModule = true;
                    autoCloseDashboard.IsEnabled = false;
                    try
                    {
                        await moduleActivation.SetAutoCloseDashboardAsync(
                            autoCloseDashboard.IsChecked == true,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        MessageBox.Show(
                            exception.Message,
                            "漂移仪表盘设置失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    finally
                    {
                        changingModule = false;
                        RenderSelectedPage();
                    }
                };
                description.Children.Add(autoCloseDashboard);
            }
            row.Children.Add(description);
            var blockedByDrift =
                module is LapAnalysisModule &&
                moduleActivation.IsDriftActive;
            var toggle = new ToggleButton
            {
                Content = blockedByDrift
                    ? "漂移模式中"
                    : module.Status.IsEnabled ? "已启用" : "已停用",
                IsChecked = module.Status.IsEnabled,
                IsEnabled = !blockedByDrift,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = blockedByDrift
                    ? "请先关闭漂移仪表盘，再启用圈速分析。"
                    : null
            };
            toggle.Click += async (_, _) =>
            {
                var acceptedIntroduction = false;
                bool? introductionAutoClose = null;
                var requestedEnabled = toggle.IsChecked == true;
                if (module is DriftDashboardModule &&
                    requestedEnabled &&
                    !moduleActivation.IntroductionSeen)
                {
                    var introduction = new DriftDashboardIntroductionWindow(
                        moduleActivation.AutoCloseDashboard)
                    {
                        Owner = this
                    };
                    if (introduction.ShowDialog() != true)
                    {
                        toggle.IsChecked = false;
                        return;
                    }
                    acceptedIntroduction = true;
                    introductionAutoClose = introduction.AutoCloseDashboard;
                }

                changingModule = true;
                toggle.IsEnabled = false;
                try
                {
                    if (introductionAutoClose is bool autoClose)
                    {
                        await moduleActivation.SetAutoCloseDashboardAsync(
                            autoClose,
                            CancellationToken.None);
                    }
                    await moduleActivation.SetEnabledAsync(
                        module.Descriptor.Id,
                        requestedEnabled,
                        CancellationToken.None);
                    if (acceptedIntroduction)
                    {
                        await moduleActivation.MarkIntroductionSeenAsync(
                            CancellationToken.None);
                    }
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

        static string AutoCloseDashboardText(bool enabled) =>
            $"打开漂移仪表盘时自动关闭主仪表盘：{(enabled ? "开" : "关")}";
    }
}
