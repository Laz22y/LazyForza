using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Domain;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private string[] quickSettingIds = [SidebarQuickSettings.Mute];
    private readonly StackPanel quickSettingsHost = new();
    private readonly List<Action> refreshQuickSettings = [];
    private TextBlock? sidebarStatus, sidebarPort;
    private System.Windows.Shapes.Ellipse? sidebarDot;
    private Popup? quickVolumePopup;
    private Action? refreshEngineerControls;
    private Action? syncHudMotionPreference;

    private void InitializeSidebar()
    {
        quickSettingIds = SidebarQuickSettings.Load(store.GetAppSetting(SidebarQuickSettings.StoreKey));
        var status = new DockPanel { Margin = new Thickness(14, 2, 12, 10), Height = 24 };
        sidebarDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(sidebarDot);
        sidebarPort = Label("", 10, FontWeights.Normal, "MutedBrush");
        sidebarPort.Margin = new Thickness(6, 0, 0, 0);
        sidebarPort.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(sidebarPort, Dock.Right); status.Children.Add(sidebarPort);
        sidebarStatus = Label("", 12, FontWeights.Normal, "MutedBrush");
        sidebarStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        sidebarStatus.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(sidebarStatus);
        navigation.SetFooter(status, quickSettingsHost);
        navigation.CompactChanged += (_, _) => BuildSidebarQuickSettings();
        Deactivated += (_, _) => { if (quickVolumePopup is not null) quickVolumePopup.IsOpen = false; };
        BuildSidebarQuickSettings();
        RefreshSidebar();
    }

    private void RefreshSidebar()
    {
        if (sidebarStatus is null || sidebarPort is null || sidebarDot is null) return;
        var diagnostics = telemetry.Diagnostics;
        sidebarStatus.Text = SidebarStatusText(sourceKind, diagnostics.State);
        sidebarPort.Text = sourceKind == TelemetrySourceKind.Live
            ? diagnostics.ListenPort.ToString(CultureInfo.InvariantCulture) : "";
        sidebarDot.Fill = Brush(diagnostics.State is TelemetryStreamState.Live or TelemetryStreamState.Replay
            ? "AccentBrush" : diagnostics.State == TelemetryStreamState.Faulted ? "DangerBrush" : "MutedBrush");
        foreach (var refresh in refreshQuickSettings) refresh();
    }

    internal static string SidebarStatusText(TelemetrySourceKind source, TelemetryStreamState state) =>
        AppLocalization.Literal(source switch
        {
            TelemetrySourceKind.Simulator => "模拟遥测",
            TelemetrySourceKind.Replay => "遥测回放",
            _ => state switch
            {
                TelemetryStreamState.Live => "遥测已连接",
                TelemetryStreamState.Stale => "遥测已中断",
                TelemetryStreamState.Faulted => "监听失败",
                _ => "等待遥测"
            }
        });

    private static string QuickSettingTitle(string id) => AppLocalization.Literal(id switch
    {
        SidebarQuickSettings.Mute => "语音工程师",
        SidebarQuickSettings.Volume => "语音音量",
        _ => "减少动态"
    });

    private void BuildSidebarQuickSettings()
    {
        if (quickVolumePopup is not null) quickVolumePopup.IsOpen = false;
        quickSettingsHost.Children.Clear(); refreshQuickSettings.Clear();
        if (quickSettingIds.Length == 0) return;
        var heading = Label("快速设置", 10, FontWeights.Normal, "MutedBrush");
        heading.Margin = new Thickness(14, 0, 0, 4);
        quickSettingsHost.Children.Add(heading);
        var compact = navigation.IsCompact && quickSettingIds.Length > 1;
        Panel rows = compact ? new WrapPanel { Margin = new Thickness(10, 0, 10, 8) } : new StackPanel();
        quickSettingsHost.Children.Add(rows);
        foreach (var id in quickSettingIds)
        {
            var action = new Button { Width = 36, Height = 36, Padding = new Thickness(6), Margin = new Thickness(0),
                Style = (Style)FindResource("SidebarQuickAction") };
            AutomationProperties.SetName(action, QuickSettingTitle(id));
            ToolTipService.SetShowOnDisabled(action, true);
            if (compact) { action.Margin = new Thickness(0, 0, 6, 0); rows.Children.Add(action); }
            else
            {
                var row = new DockPanel { Margin = new Thickness(14, 0, 8, 2), Height = 38 };
                DockPanel.SetDock(action, Dock.Right); row.Children.Add(action);
                var title = Label(QuickSettingTitle(id), 12);
                title.VerticalAlignment = VerticalAlignment.Center;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(title); rows.Children.Add(row);
            }
            string? previousState = null;
            void Refresh()
            {
                var state = id switch
                {
                    SidebarQuickSettings.Mute => $"{engineerEnabled}:{engineerMuted}:{raceEngineer?.Error}",
                    SidebarQuickSettings.Volume => engineerVolume.ToString(CultureInfo.InvariantCulture),
                    _ => overlay.TimingLayout.ReduceMotion.ToString()
                };
                if (state == previousState) return;
                previousState = state;
                var mute = id == SidebarQuickSettings.Mute;
                var active = mute ? !engineerMuted && engineerEnabled : overlay.TimingLayout.ReduceMotion;
                action.IsEnabled = !mute || engineerEnabled;
                action.Content = id == SidebarQuickSettings.Volume ? Label($"{engineerVolume}", 11)
                    : QuickIcon(mute
                        ? active ? "M3 9 H7 L12 5 V19 L7 15 H3 Z M16 8 Q21 12 16 16" : "M3 9 H7 L12 5 V19 L7 15 H3 Z M16 9 L22 15 M22 9 L16 15"
                        : "M5 8 H19 M5 12 H15 M5 16 H11", active);
                action.ToolTip = id switch
                {
                    SidebarQuickSettings.Mute => EngineerText(!engineerEnabled ? "语音未启用" : engineerMuted ? "恢复声音" : "立即静音",
                        !engineerEnabled ? "Speech is disabled" : engineerMuted ? "Unmute" : "Mute now"),
                    SidebarQuickSettings.Volume => QuickSettingTitle(id) + $" · {engineerVolume}%",
                    _ => QuickSettingTitle(id) + " · " + AppLocalization.Literal(active ? "开" : "关")
                };
                AutomationProperties.SetHelpText(action, action.ToolTip.ToString());
            }
            refreshQuickSettings.Add(Refresh); Refresh();
            action.Click += async (_, _) =>
            {
                if (id == SidebarQuickSettings.Mute)
                {
                    engineerMuted = !engineerMuted;
                    ApplyEngineerPreferences();
                }
                else if (id == SidebarQuickSettings.Volume) OpenQuickVolume(action);
                else
                {
                    var next = overlay.CurrentLayout with { ReduceMotion = !overlay.TimingLayout.ReduceMotion };
                    store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(next));
                    await overlay.SetLayoutAsync(next, lifetimeCancellation.Token);
                    syncHudMotionPreference?.Invoke();
                }
                RefreshSidebar();
            };
        }
    }

    private static UIElement QuickIcon(string data, bool active)
    {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 1.5,
            Width = 20, Height = 20, Stretch = Stretch.Uniform };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, active ? "TextBrush" : "MutedBrush");
        return icon;
    }

    private void OpenQuickVolume(Button target)
    {
        if (quickVolumePopup is not null) quickVolumePopup.IsOpen = false;
        var panel = new StackPanel { Width = 184 };
        var value = Label($"{QuickSettingTitle(SidebarQuickSettings.Volume)} · {engineerVolume}%", 12);
        panel.Children.Add(value);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = engineerVolume, TickFrequency = 1,
            IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(slider, QuickSettingTitle(SidebarQuickSettings.Volume));
        slider.ValueChanged += (_, _) =>
        {
            engineerVolume = (int)slider.Value;
            ApplyEngineerPreferences();
            value.Text = $"{QuickSettingTitle(SidebarQuickSettings.Volume)} · {engineerVolume}%";
        };
        panel.Children.Add(slider);
        quickVolumePopup = new Popup { PlacementTarget = target, Placement = PlacementMode.Right,
            StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Child = panel, Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1), Padding = new Thickness(16), CornerRadius = new CornerRadius(8) } };
        quickVolumePopup.IsOpen = true;
        slider.Focus();
    }

    private UIElement BuildQuickSettingsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("快速设置", 17, FontWeights.SemiBold));
        panel.Children.Add(Label("选择在侧栏底部显示的项目。", 12, FontWeights.Normal, "MutedBrush"));
        var choices = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var id in SidebarQuickSettings.Available)
        {
            var choice = new CheckBox { Content = QuickSettingTitle(id), IsChecked = quickSettingIds.Contains(id),
                Margin = new Thickness(0, 0, 22, 8) };
            choice.Click += (_, _) =>
            {
                quickSettingIds = SidebarQuickSettings.Normalize(choice.IsChecked == true
                    ? quickSettingIds.Append(id) : quickSettingIds.Where(value => value != id));
                store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save(quickSettingIds));
                BuildSidebarQuickSettings();
            };
            choices.Children.Add(choice);
        }
        panel.Children.Add(choices);
        return Card(panel);
    }

    private void ApplyEngineerPreferences()
    {
        raceEngineer?.Configure(engineerEnabled, engineerMuted, engineerVolume);
        store.SetAppSetting("raceEngineer.enabled", engineerEnabled.ToString());
        store.SetAppSetting("raceEngineer.muted", engineerMuted.ToString());
        store.SetAppSetting("raceEngineer.volume", engineerVolume.ToString(CultureInfo.InvariantCulture));
        refreshEngineerControls?.Invoke();
        UpdateRaceEngineer(); RefreshSidebar();
    }
}
