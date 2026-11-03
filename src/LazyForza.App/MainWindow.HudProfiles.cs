using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private readonly OverlayLayoutProfileStore hudProfiles;
    private bool changingHudProfile;
    private OverlayLayout? pendingHudLayout;

    private async Task SaveHudLayoutAsync(OverlayLayout layout)
    {
        pendingHudLayout = layout;
        hudProfiles.SaveCurrent(layout);
        await overlay.SetLayoutAsync(hudProfiles.Active.Layout, CancellationToken.None);
        if (ReferenceEquals(pendingHudLayout, layout)) pendingHudLayout = null;
    }

    private UIElement BuildHudProfiles() => new HudLayoutProfilesPanel(hudProfiles,
        id => ChangeHudProfileAsync(() => hudProfiles.Select(id)),
        (name, rename) => ChangeHudProfileAsync(() =>
        {
            if (rename) hudProfiles.Rename(name); else hudProfiles.Create(name);
        }),
        () => ChangeHudProfileAsync(hudProfiles.DeleteActive), CreateQuickSettingPin(SidebarQuickSettings.HudProfiles));

    private async Task ChangeHudProfileAsync(Action change)
    {
        if (changingHudProfile) return;
        changingHudProfile = true;
        // Freeze the existing controls while their debounced changes are flushed.
        // They must never write old slider values into the newly selected profile.
        content.IsEnabled = false;
        quickSettingsHost.IsEnabled = false;
        try
        {
            await FlushSettingsAsync();
            if (pendingHudLayout is { } pending) await SaveHudLayoutAsync(pending);
            change();
            await overlay.SetLayoutAsync(hudProfiles.Active.Layout, CancellationToken.None);
            if (quickSettingPopup is not null) quickSettingPopup.IsOpen = false;
            if (navigation.SelectedIndex == 8 && selectedSettingsCategory == SettingsCategory.Hud)
                RenderSelectedPage(true);
            RefreshSidebar();
        }
        finally
        {
            content.IsEnabled = true; quickSettingsHost.IsEnabled = true; changingHudProfile = false;
        }
    }

    private void OpenQuickHudProfiles(Button target)
    {
        if (quickSettingPopup is not null) quickSettingPopup.IsOpen = false;
        var panel = new StackPanel { Width = 252 };
        panel.Children.Add(Label(HudLayoutProfilesPanel.Title, 13, FontWeights.SemiBold));
        var choices = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Right,
            StaysOpen = false, AllowsTransparency = true };
        foreach (var profile in hudProfiles.State.Profiles)
        {
            var selected = profile.Id == hudProfiles.State.ActiveId;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            var label = Label(profile.Name, 12);
            label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(label);
            if (selected)
            {
                var check = Label("✓", 13, FontWeights.SemiBold, "AccentBrush");
                Grid.SetColumn(check, 1); row.Children.Add(check);
            }
            var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 8, 10, 8), ToolTip = profile.Name };
            AutomationProperties.SetName(button, profile.Name);
            if (selected) button.SetResourceReference(Control.BackgroundProperty, "AccentSoftBrush");
            button.Click += async (_, _) =>
            {
                if (selected) { popup.IsOpen = false; return; }
                panel.IsEnabled = false;
                try { await ChangeHudProfileAsync(() => hudProfiles.Select(profile.Id)); }
                catch (Exception exception)
                {
                    AppDialog.Show(AppLocalization.Format("hud.profiles.failed", "未能保存布局方案：{0}", exception.Message),
                        HudLayoutProfilesPanel.Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { panel.IsEnabled = true; }
            };
            choices.Children.Add(button);
        }
        panel.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var manage = new Button { Content = AppLocalization.Text("hud.profiles.manage", "管理布局方案"),
            Margin = new Thickness(0), Padding = new Thickness(10, 7, 10, 7) };
        manage.Click += (_, _) =>
        {
            popup.IsOpen = false; selectedSettingsCategory = SettingsCategory.Hud;
            if (navigation.SelectedIndex == 8) RenderSelectedPage(); else navigation.SelectedIndex = 8;
        };
        panel.Children.Add(manage);
        popup.Child = new Border { Child = panel, Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(16), CornerRadius = new CornerRadius(8) };
        popup.Closed += (_, _) => { if (ReferenceEquals(quickSettingPopup, popup)) quickSettingPopup = null; };
        quickSettingPopup = popup; popup.IsOpen = true;
        choices.Children.OfType<Button>().First().Focus();
    }
}
