using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private event Action? QuickSettingPinsChanged;

    private void SetQuickSettingPinned(string id, bool pinned)
    {
        var next = SidebarQuickSettings.Normalize(pinned ? quickSettingIds.Append(id) : quickSettingIds.Where(value => value != id));
        store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save(next));
        quickSettingIds = next;
        BuildSidebarQuickSettings();
        QuickSettingPinsChanged?.Invoke();
    }

    private ToggleButton CreateQuickSettingPin(string id)
    {
        var button = new ToggleButton { Width = 28, Height = 28, Padding = new Thickness(5),
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.Transparent };
        AutomationProperties.SetAutomationId(button, "quick-pin-" + id);
        void Refresh()
        {
            var pinned = quickSettingIds.Contains(id);
            button.IsChecked = pinned;
            button.Content = QuickIcon("M8 3 H16 V5 H15 V10 L18 13 V15 H6 V13 L9 10 V5 H8 Z M12 15 V23", pinned);
            button.ToolTip = AppLocalization.Format(pinned ? "quick.pin.remove" : "quick.pin.add",
                pinned ? "从侧栏移除：{0}" : "固定到侧栏：{0}", QuickSettingTitle(id));
            AutomationProperties.SetName(button, button.ToolTip.ToString());
        }
        button.Click += (_, _) =>
        {
            try { SetQuickSettingPinned(id, button.IsChecked == true); }
            catch (Exception exception)
            {
                Refresh();
                AppDialog.Show(AppLocalization.Format("settings.auto.failed", "未能应用：{0}", exception.Message),
                    QuickSettingTitle(id), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        button.Loaded += (_, _) => { QuickSettingPinsChanged += Refresh; Refresh(); };
        button.Unloaded += (_, _) => QuickSettingPinsChanged -= Refresh;
        Refresh();
        return button;
    }

    private UIElement WithQuickSettingPin(UIElement control, string id)
    {
        var row = new DockPanel { LastChildFill = true };
        var pin = CreateQuickSettingPin(id); DockPanel.SetDock(pin, Dock.Right);
        row.Children.Add(pin); row.Children.Add(control); return row;
    }
}
