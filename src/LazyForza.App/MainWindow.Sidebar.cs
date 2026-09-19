using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LazyForza.Domain;
using LazyForza.Modules.Dashboard;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private string[] quickSettingIds = SidebarQuickSettings.Default;
    private readonly StackPanel quickSettingsHost = new();
    private readonly List<Action> refreshQuickSettings = [];
    private TextBlock? sidebarStatus, sidebarPort;
    private System.Windows.Shapes.Ellipse? sidebarDot;
    private Popup? quickSettingPopup;
    private Action? refreshEngineerControls;
    private Action? refreshShiftControls;
    private Action? syncHudMotionPreference;
    private Action? syncHudShiftPreference;
    private Action? syncHudOpacityPreferences;
    private Action? syncAutomaticRecordingPreference;

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
        Deactivated += (_, _) => { if (quickSettingPopup is not null) quickSettingPopup.IsOpen = false; };
        BuildSidebarQuickSettings();
        RefreshSidebar();
    }

    private void RefreshSidebar()
    {
        if (sidebarStatus is null || sidebarPort is null || sidebarDot is null) return;
        var diagnostics = telemetry.Diagnostics;
        UpdateSidebarConnection(sourceKind, diagnostics.State, diagnostics.ListenPort);
        foreach (var refresh in refreshQuickSettings) refresh();
    }

    private void UpdateSidebarConnection(TelemetrySourceKind source, TelemetryStreamState state, int port)
    {
        if (sidebarStatus is null || sidebarPort is null || sidebarDot is null) return;
        sidebarStatus.Text = SidebarStatusText(source, state);
        sidebarPort.Text = source == TelemetrySourceKind.Live ? port.ToString(CultureInfo.InvariantCulture) : "";
        sidebarDot.Fill = Brush(state is TelemetryStreamState.Live or TelemetryStreamState.Replay
            ? "AccentBrush" : state == TelemetryStreamState.Faulted ? "DangerBrush" : "MutedBrush");
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
        SidebarQuickSettings.ShiftRecommendations => "本车换挡推荐",
        SidebarQuickSettings.ShiftIndicators => "HUD 换挡提示",
        SidebarQuickSettings.HudOpacity => "HUD 整体不透明度",
        SidebarQuickSettings.EstateBackdropOpacity => "地产赛事底板不透明度",
        SidebarQuickSettings.AutomaticRecording => "比赛自动录制",
        _ => "减少动态"
    });

    private void BuildSidebarQuickSettings()
    {
        var dashboard = moduleManager.Modules.OfType<DashboardModule>().Single();
        if (quickSettingPopup is not null) quickSettingPopup.IsOpen = false;
        quickSettingsHost.Children.Clear(); refreshQuickSettings.Clear();
        if (quickSettingIds.Length == 0) return;
        var heading = Label("快速设置", 10, FontWeights.Normal, "MutedBrush");
        heading.Margin = new Thickness(14, 0, 0, 4);
        quickSettingsHost.Children.Add(heading);
        var compact = (navigation.IsCompact || quickSettingIds.Length > 5) && quickSettingIds.Length > 1;
        Panel rows = compact ? new WrapPanel { Margin = new Thickness(10, 0, 10, 8) } : new StackPanel();
        quickSettingsHost.Children.Add(rows);
        foreach (var id in quickSettingIds)
        {
            var action = new Button { Width = 36, Height = 36, Padding = new Thickness(6), Margin = new Thickness(0),
                Style = (Style)FindResource("SidebarQuickAction") };
            AutomationProperties.SetName(action, QuickSettingTitle(id));
            ToolTipService.SetShowOnDisabled(action, true);
            if (compact) { action.Margin = new Thickness(0, 0, 4, 0); rows.Children.Add(action); }
            else
            {
                var row = new DockPanel { Margin = new Thickness(14, 0, 8, 2), Height = 38 };
                DockPanel.SetDock(action, Dock.Right); row.Children.Add(action);
                var title = Label(QuickSettingTitle(id), 12);
                title.VerticalAlignment = VerticalAlignment.Center;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(title); rows.Children.Add(row);
            }
            var busy = false;
            string? previousState = null;
            void Refresh()
            {
                var state = id switch
                {
                    SidebarQuickSettings.Mute => $"{engineerEnabled}:{engineerMuted}:{raceEngineer?.Error}",
                    SidebarQuickSettings.Volume => engineerVolume.ToString(CultureInfo.InvariantCulture),
                    SidebarQuickSettings.ShiftRecommendations => $"{dashboard.ActiveVehicleProfileId}:{dashboard.ShiftRecommendationsEnabled}",
                    SidebarQuickSettings.ShiftIndicators => overlay.TimingLayout.ShowShiftIndicators.ToString(),
                    SidebarQuickSettings.HudOpacity => overlay.TimingLayout.Opacity.ToString(CultureInfo.InvariantCulture),
                    SidebarQuickSettings.EstateBackdropOpacity => overlay.TimingLayout.EstateRaceBackdropOpacity.ToString(CultureInfo.InvariantCulture),
                    SidebarQuickSettings.AutomaticRecording => recorder.AutomaticOptions.Enabled.ToString(),
                    _ => overlay.TimingLayout.ReduceMotion.ToString()
                };
                state += $":{busy}";
                if (state == previousState) return;
                previousState = state;
                var mute = id == SidebarQuickSettings.Mute;
                var shift = id == SidebarQuickSettings.ShiftRecommendations;
                var indicators = id == SidebarQuickSettings.ShiftIndicators;
                var recording = id == SidebarQuickSettings.AutomaticRecording;
                var active = mute ? !engineerMuted && engineerEnabled : shift
                    ? dashboard.ActiveVehicleProfileId is not null && dashboard.ShiftRecommendationsEnabled
                    : indicators ? overlay.TimingLayout.ShowShiftIndicators : recording ? recorder.AutomaticOptions.Enabled
                    : id is SidebarQuickSettings.HudOpacity or SidebarQuickSettings.EstateBackdropOpacity || overlay.TimingLayout.ReduceMotion;
                action.IsEnabled = !busy && (shift ? dashboard.ActiveVehicleProfileId is not null : !mute || engineerEnabled);
                action.Content = id == SidebarQuickSettings.Volume ? Label($"{engineerVolume}", 11)
                    : QuickIcon(id == SidebarQuickSettings.HudOpacity
                        ? "M12 2 A10 10 0 1 1 12 22 A10 10 0 1 1 12 2 Z M12 2 V22 M12 6 H20 M12 10 H22 M12 14 H22 M12 18 H20"
                        : id == SidebarQuickSettings.EstateBackdropOpacity
                        ? "M3 3 H21 V21 H3 Z M3 15 L15 3 M3 21 L21 3 M9 21 L21 9 M15 21 L21 15"
                        : recording ? "M12 2 A10 10 0 1 1 12 22 A10 10 0 1 1 12 2 Z M12 8 A4 4 0 1 1 12 16 A4 4 0 1 1 12 8 Z"
                        : indicators ? "M5 4.5 H19 Q21 4.5 21 6.5 V17.5 Q21 19.5 19 19.5 H5 Q3 19.5 3 17.5 V6.5 Q3 4.5 5 4.5 Z M6 13.5 L9 10.5 L12 13.5 M13 10.5 L16 13.5 L19 10.5"
                        : shift ? "M6 18 V5 M2 9 L6 5 L10 9 M18 6 V19 M14 15 L18 19 L22 15" : mute
                        ? active ? "M3 9 H7 L12 5 V19 L7 15 H3 Z M16 8 Q21 12 16 16" : "M3 9 H7 L12 5 V19 L7 15 H3 Z M16 9 L22 15 M22 9 L16 15"
                        : "M5 8 H19 M5 12 H15 M5 16 H11", active, rounded: indicators);
                action.ToolTip = id switch
                {
                    SidebarQuickSettings.Mute => EngineerText(!engineerEnabled ? "语音未启用" : engineerMuted ? "恢复声音" : "立即静音",
                        !engineerEnabled ? "Speech is disabled" : engineerMuted ? "Unmute" : "Mute now"),
                    SidebarQuickSettings.Volume => QuickSettingTitle(id) + $" · {engineerVolume}%",
                    SidebarQuickSettings.HudOpacity => QuickSettingTitle(id) + $" · {overlay.TimingLayout.Opacity:P0}",
                    SidebarQuickSettings.EstateBackdropOpacity => QuickSettingTitle(id) + $" · {overlay.TimingLayout.EstateRaceBackdropOpacity:P0}",
                    SidebarQuickSettings.ShiftIndicators => AppLocalization.Literal(active
                        ? "HUD 换挡提示已显示，点击隐藏。" : "HUD 换挡提示已隐藏，点击显示。"),
                    SidebarQuickSettings.ShiftRecommendations => AppLocalization.Literal(dashboard.ActiveVehicleProfileId is null
                        ? "识别车辆后可切换推荐换挡。"
                        : active ? "当前车辆：推荐换挡已开启，点击关闭。" : "当前车辆：推荐换挡已关闭，点击开启。"),
                    _ => QuickSettingTitle(id) + " · " + AppLocalization.Literal(active ? "开" : "关")
                };
                AutomationProperties.SetHelpText(action, action.ToolTip.ToString());
            }
            refreshQuickSettings.Add(Refresh); Refresh();
            action.Click += async (_, _) =>
            {
                if (busy) return;
                busy = true; Refresh();
                try
                {
                    if (id == SidebarQuickSettings.Mute)
                    {
                        engineerMuted = !engineerMuted;
                        ApplyEngineerPreferences();
                    }
                    else if (id is SidebarQuickSettings.Volume or SidebarQuickSettings.HudOpacity or SidebarQuickSettings.EstateBackdropOpacity)
                    {
                        await FlushSettingsAsync();
                        if (action.IsLoaded) OpenQuickSlider(action, id);
                    }
                    else if (id == SidebarQuickSettings.AutomaticRecording)
                    {
                        await FlushSettingsAsync();
                        await recorder.SetAutomaticOptionsAsync(
                            recorder.AutomaticOptions with { Enabled = !recorder.AutomaticOptions.Enabled }, lifetimeCancellation.Token);
                        syncAutomaticRecordingPreference?.Invoke();
                    }
                    else if (id == SidebarQuickSettings.ShiftIndicators)
                    {
                        await FlushSettingsAsync();
                        var next = overlay.CurrentLayout with { ShowShiftIndicators = !overlay.TimingLayout.ShowShiftIndicators };
                        store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(next));
                        await overlay.SetLayoutAsync(next, lifetimeCancellation.Token);
                        syncHudShiftPreference?.Invoke();
                    }
                    else if (id == SidebarQuickSettings.ShiftRecommendations)
                    {
                        if (dashboard.ActiveVehicleProfileId is not { } profileId) return;
                        var enabled = !await store.GetShiftRecommendationsEnabledAsync(profileId, lifetimeCancellation.Token);
                        store.SetShiftRecommendationsEnabled(profileId, enabled);
                        dashboard.SetShiftRecommendationsEnabled(profileId, enabled);
                        refreshShiftControls?.Invoke();
                    }
                    else
                    {
                        var next = overlay.CurrentLayout with { ReduceMotion = !overlay.TimingLayout.ReduceMotion };
                        store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(next));
                        await overlay.SetLayoutAsync(next, lifetimeCancellation.Token);
                        syncHudMotionPreference?.Invoke();
                    }
                }
                catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    AppDialog.Show(AppLocalization.Format("settings.auto.failed", "未能应用：{0}", exception.Message),
                        QuickSettingTitle(id), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { busy = false; RefreshSidebar(); }
            };
        }
    }

    private static UIElement QuickIcon(string data, bool active, bool rounded = false)
    {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 1.5,
            Width = 20, Height = 20, Stretch = Stretch.Uniform,
            StrokeStartLineCap = rounded ? PenLineCap.Round : PenLineCap.Flat,
            StrokeEndLineCap = rounded ? PenLineCap.Round : PenLineCap.Flat,
            StrokeLineJoin = rounded ? PenLineJoin.Round : PenLineJoin.Miter };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, active ? "TextBrush" : "MutedBrush");
        return icon;
    }

    private void OpenQuickSlider(Button target, string id)
    {
        if (quickSettingPopup is not null) quickSettingPopup.IsOpen = false;
        var volume = id == SidebarQuickSettings.Volume;
        var panel = new StackPanel { Width = 252 };
        var heading = new DockPanel();
        var value = Label("", 12, FontWeights.SemiBold, "AccentBrush");
        value.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(value, Dock.Right); heading.Children.Add(value);
        heading.Children.Add(Label(QuickSettingTitle(id), 12));
        panel.Children.Add(heading);
        var initial = volume ? engineerVolume : 100 * (id == SidebarQuickSettings.HudOpacity
            ? overlay.TimingLayout.Opacity : overlay.TimingLayout.EstateRaceBackdropOpacity);
        var slider = new Slider { Minimum = id == SidebarQuickSettings.HudOpacity ? 25 : 0, Maximum = 100,
            Value = initial, TickFrequency = volume ? 1 : 5, SmallChange = volume ? 1 : 5, LargeChange = 10,
            IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(slider, QuickSettingTitle(id));
        value.Text = $"{slider.Value:0}%";
        panel.Children.Add(slider);
        var status = AutoApplyStatus();
        panel.Children.Add(status);
        var automatic = AutoApplySettings(panel, async () =>
        {
            if (volume)
            {
                engineerVolume = (int)slider.Value;
                ApplyEngineerPreferences();
            }
            else
            {
                var next = id == SidebarQuickSettings.HudOpacity
                    ? overlay.CurrentLayout with { Opacity = slider.Value / 100 }
                    : overlay.CurrentLayout with { EstateRaceBackdropOpacity = slider.Value / 100 };
                store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(next));
                await overlay.SetLayoutAsync(next, lifetimeCancellation.Token);
                syncHudOpacityPreferences?.Invoke();
                RefreshSidebar();
            }
            status.Visibility = Visibility.Collapsed;
        }, status, milliseconds: 50);
        slider.ValueChanged += (_, _) =>
        {
            value.Text = $"{slider.Value:0}%";
            automatic.Request();
        };
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Right,
            StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Child = panel, Background = Brush("PanelBrush"), BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1), Padding = new Thickness(16), CornerRadius = new CornerRadius(8) } };
        popup.Closed += async (_, _) =>
        {
            await automatic.FlushAsync();
            automatic.Dispose();
            settingApplications.Remove(automatic);
            if (ReferenceEquals(quickSettingPopup, popup)) quickSettingPopup = null;
        };
        quickSettingPopup = popup;
        popup.IsOpen = true;
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
        raceEngineer?.Configure(engineerEnabled, engineerMuted, engineerVolume, engineerPreferences);
        store.SetAppSetting("raceEngineer.enabled", engineerEnabled.ToString());
        store.SetAppSetting("raceEngineer.muted", engineerMuted.ToString());
        store.SetAppSetting("raceEngineer.volume", engineerVolume.ToString(CultureInfo.InvariantCulture));
        refreshEngineerControls?.Invoke();
        UpdateRaceEngineer(); RefreshSidebar();
    }
}
