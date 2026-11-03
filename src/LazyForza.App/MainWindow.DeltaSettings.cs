using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LazyForza.Domain;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private event Action? DeltaOptionsChanged;
    private LapAnalysisModule DeltaModule => moduleManager.Modules.OfType<LapAnalysisModule>().Single();
    private sealed record DeltaChoice(LapDeltaComparison Kind, string Name)
    {
        public override string ToString() => Name;
    }
    private static string DeltaComparisonName(LapDeltaComparison kind) => kind switch
    {
        LapDeltaComparison.TrackBest => AppLocalization.Text("delta.trackBest", "同赛道最佳"),
        LapDeltaComparison.PerformanceIndexBest => AppLocalization.Text("delta.piBest", "同性能分最佳"),
        LapDeltaComparison.CarBest => AppLocalization.Text("delta.carBest", "同车辆最佳"),
        _ => AppLocalization.Text("delta.classBest", "同性能等级最佳")
    };

    private void SetDeltaOptions(LapDeltaOptions options)
    {
        DeltaModule.SetDeltaOptions(options);
        DeltaOptionsChanged?.Invoke();
        RefreshSidebar();
    }

    private UIElement BuildDeltaSettings()
    {
        var module = DeltaModule;
        var panel = new WrapPanel { Margin = new Thickness(-4) };
        var toggle = new ToggleButton { Content = AppLocalization.Text("delta.continuous", "连续实时 Delta"),
            IsChecked = module.DeltaOptions.Continuous, Style = (Style)FindResource("SettingSwitch"), Margin = new Thickness(0) };
        AutomationProperties.SetAutomationId(toggle, "delta-continuous");
        AutomationProperties.SetName(toggle, AppLocalization.Text("delta.continuous", "连续实时 Delta"));
        var continuous = WithQuickSettingPin(toggle, SidebarQuickSettings.ContinuousDelta);
        ((FrameworkElement)continuous).Margin = new Thickness(4, 4, 20, 4);
        panel.Children.Add(continuous);
        var reference = new StackPanel();
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        var label = Label(AppLocalization.Text("delta.comparison", "Delta 对比"), 12);
        label.Margin = new Thickness(0, 0, 10, 0); label.VerticalAlignment = VerticalAlignment.Center;
        choices.Children.Add(label);
        var selector = new ComboBox { Width = 220, MinHeight = 38, SelectedValuePath = nameof(DeltaChoice.Kind),
            ItemsSource = Enum.GetValues<LapDeltaComparison>().Select(kind => new DeltaChoice(kind, DeltaComparisonName(kind))).ToArray(),
            SelectedValue = module.DeltaOptions.Comparison, FontSize = 12 };
        AutomationProperties.SetName(selector, AppLocalization.Text("delta.comparison", "Delta 对比"));
        AutomationProperties.SetAutomationId(selector, "delta-comparison");
        choices.Children.Add(selector);
        choices.Children.Add(CreateQuickSettingPin(SidebarQuickSettings.DeltaComparison));
        reference.Children.Add(choices);
        var pending = Label(AppLocalization.Text("delta.nextLap", "下圈生效"), 11, FontWeights.Normal, "MutedBrush");
        pending.Margin = new Thickness(0, 4, 0, 0); reference.Children.Add(pending);
        reference.Margin = new Thickness(4); panel.Children.Add(reference);
        var card = Card(panel);
        var syncing = false;
        void Refresh()
        {
            syncing = true;
            toggle.IsChecked = module.DeltaOptions.Continuous;
            AutomationProperties.SetItemStatus(toggle, AppLocalization.Literal(module.DeltaOptions.Continuous ? "开" : "关"));
            selector.SelectedValue = module.DeltaOptions.Comparison;
            pending.Visibility = module.DeltaComparisonPending ? Visibility.Visible : Visibility.Collapsed;
            syncing = false;
        }
        void Apply(LapDeltaOptions options)
        {
            try { SetDeltaOptions(options); }
            catch (Exception exception)
            {
                Refresh();
                AppDialog.Show(AppLocalization.Format("settings.auto.failed", "未能应用：{0}", exception.Message),
                    AppLocalization.Text("delta.comparison", "Delta 对比"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        toggle.Click += (_, _) => Apply(module.DeltaOptions with { Continuous = toggle.IsChecked == true });
        selector.SelectionChanged += (_, _) =>
        { if (!syncing && selector.SelectedValue is LapDeltaComparison kind) Apply(module.DeltaOptions with { Comparison = kind }); };
        card.Loaded += (_, _) => { DeltaOptionsChanged += Refresh; Refresh(); };
        card.Unloaded += (_, _) => DeltaOptionsChanged -= Refresh;
        Refresh();
        return card;
    }

    private void OpenQuickDeltaComparison(Button target)
    {
        if (quickSettingPopup is not null) quickSettingPopup.IsOpen = false;
        var panel = new StackPanel { Width = 252 };
        panel.Children.Add(Label(AppLocalization.Text("delta.comparison", "Delta 对比"), 13, FontWeights.SemiBold));
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Right, StaysOpen = false, AllowsTransparency = true };
        foreach (var kind in Enum.GetValues<LapDeltaComparison>())
        {
            var row = new DockPanel();
            var check = Label(kind == DeltaModule.DeltaOptions.Comparison ? "✓" : "", 13, FontWeights.SemiBold, "AccentBrush");
            DockPanel.SetDock(check, Dock.Right); row.Children.Add(check); row.Children.Add(Label(DeltaComparisonName(kind), 12));
            var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 8, 10, 8) };
            AutomationProperties.SetName(button, DeltaComparisonName(kind));
            button.Click += (_, _) =>
            {
                try { SetDeltaOptions(DeltaModule.DeltaOptions with { Comparison = kind }); popup.IsOpen = false; }
                catch (Exception exception)
                {
                    AppDialog.Show(AppLocalization.Format("settings.auto.failed", "未能应用：{0}", exception.Message),
                        AppLocalization.Text("delta.comparison", "Delta 对比"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            panel.Children.Add(button);
        }
        popup.Child = new Border { Child = panel, Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(16), CornerRadius = new CornerRadius(8) };
        popup.Closed += (_, _) => { if (ReferenceEquals(quickSettingPopup, popup)) quickSettingPopup = null; };
        quickSettingPopup = popup; popup.IsOpen = true;
        panel.Children.OfType<Button>().First().Focus();
    }
}
