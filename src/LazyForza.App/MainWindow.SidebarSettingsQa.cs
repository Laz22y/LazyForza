using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LazyForza.Domain;
using LazyForza.Modules.Dashboard;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    // Runs only with the explicit capture-qa workflow and its isolated profile/data directories.
    private async Task CaptureSidebarSettingsQaAsync(string directory)
    {
        var results = new List<string>();
        void Check(bool condition, string message)
        {
            File.AppendAllText(Path.Combine(directory, "settings-checks.txt"), $"{(condition ? "PASS" : "FAIL")}: {message}\n");
            if (!condition) throw new InvalidOperationException("Settings QA: " + message);
            results.Add(message);
        }
        async Task ShowSettings(SettingsCategory category)
        {
            selectedSettingsCategory = category;
            navigation.SelectedIndex = 8;
            RenderSelectedPage();
            await Task.Delay(150); UpdateLayout();
        }
        var originalChoices = quickSettingIds;
        quickSettingIds = [.. SidebarQuickSettings.Available];
        BuildSidebarQuickSettings();
        var dashboard = moduleManager.Modules.OfType<DashboardModule>().Single();
        Button ShiftButton() => QaChildren<Button>(quickSettingsHost).Single(button =>
            AutomationProperties.GetName(button) == QuickSettingTitle(SidebarQuickSettings.ShiftRecommendations));
        UpdateLayout();
        if (dashboard.ActiveVehicleProfileId is { } profileId)
        {
            var originalShift = await store.GetShiftRecommendationsEnabledAsync(profileId, lifetimeCancellation.Token);
            var otherProfiles = store.ListVehicleProfiles().Where(profile => profile.Id != profileId)
                .ToDictionary(profile => profile.Id, profile => profile.ShiftRecommendationsEnabled);
            ShiftButton().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(dashboard.ShiftRecommendationsEnabled == !originalShift, "Quick shift setting applies immediately");
            Check(await store.GetShiftRecommendationsEnabledAsync(profileId, lifetimeCancellation.Token) == !originalShift,
                "Quick shift setting persisted to the active vehicle");
            Check(store.ListVehicleProfiles().Where(profile => otherProfiles.ContainsKey(profile.Id))
                .All(profile => profile.ShiftRecommendationsEnabled == otherProfiles[profile.Id]), "Other vehicles retain their shift preferences");
            await moduleManager.SetEnabledAsync(DashboardModule.ModuleId, false, lifetimeCancellation.Token);
            RefreshSidebar();
            Check(!ShiftButton().IsEnabled, "Quick shift setting is disabled without an active vehicle");
            await moduleManager.SetEnabledAsync(DashboardModule.ModuleId, true, lifetimeCancellation.Token);
            for (var attempt = 0; dashboard.ActiveVehicleProfileId is null && attempt < 100; attempt++) await Task.Delay(50);
            Check(dashboard.ActiveVehicleProfileId == profileId && dashboard.ShiftRecommendationsEnabled == !originalShift,
                "Restarted dashboard restores the vehicle shift preference");
            store.SetShiftRecommendationsEnabled(profileId, originalShift);
            dashboard.SetShiftRecommendationsEnabled(profileId, originalShift);
            RefreshSidebar();
            Check(ShiftButton().IsEnabled, "Quick shift setting becomes available when a vehicle is identified again");
        }
        else Check(!ShiftButton().IsEnabled, "Quick shift setting waits until the vehicle configuration is learned");
        foreach (var size in new[] { new Size(1440, 900), new Size(960, 640) })
        {
            Width = size.Width; Height = size.Height;
            await ShowSettings(SettingsCategory.General);
            if (content.Content is ScrollViewer scroll) scroll.ScrollToBottom();
            await Task.Delay(120);
            CaptureVisual(this, Path.Combine(directory, $"quick-settings-{size.Width:0}x{size.Height:0}.png"));
            Check(QaChildren<Button>(quickSettingsHost).Count() == SidebarQuickSettings.Available.Length, $"All quick actions at {size}");
            Check(!QaChildren<Button>(content).Any(button => button.Content is string text &&
                new[] { "保存", "应用", "Save", "Apply" }.Any(text.StartsWith)), $"No general apply buttons at {size}");
            await ShowSettings(SettingsCategory.Telemetry);
            CaptureVisual(this, Path.Combine(directory, $"network-auto-{size.Width:0}x{size.Height:0}.png"));
            Check(!QaChildren<Button>(content).Any(button => button.Content is string text &&
                new[] { "保存", "应用", "Save", "Apply" }.Any(text.StartsWith)), $"No apply buttons at {size}");
        }

        var portBox = QaChildren<TextBox>(content).Single(box => AutomationProperties.GetName(box) == AppLocalization.Literal("UDP 端口"));
        var originalPort = portBox.Text;
        portBox.Text = "not-a-port";
        await FlushSettingsAsync();
        Check((store.GetAppSetting("telemetry.port") ?? LazyForzaDefaults.TelemetryPort.ToString()) == originalPort, "Invalid input preserves saved port");
        portBox.Text = originalPort == "23001" ? "23002" : "23001";
        await FlushSettingsAsync();
        Check(store.GetAppSetting("telemetry.port") == portBox.Text, "Port auto-saved without button");
        portBox.Text = originalPort; await FlushSettingsAsync();

        Button QuickButton(string id) => QaChildren<Button>(quickSettingsHost).Single(button =>
            AutomationProperties.GetName(button) == QuickSettingTitle(id));
        var recordingToggle = QaChildren<ToggleButton>(content).Single(toggle => toggle.Content is string text &&
            text.StartsWith(AppLocalization.Format("settings.recording.enabled", "自动录制：{0}", ""), StringComparison.Ordinal));
        var originalRecording = recorder.AutomaticOptions;
        QuickButton(SidebarQuickSettings.AutomaticRecording).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(150); await FlushSettingsAsync();
        Check(recorder.AutomaticOptions == originalRecording with { Enabled = !originalRecording.Enabled } &&
            recordingToggle.IsChecked == !originalRecording.Enabled &&
            AutomaticRecordingOptions.Load(store).Enabled == !originalRecording.Enabled,
            "Recording quick toggle updates full settings and persists without changing capacity or rotation");
        recordingToggle.IsChecked = originalRecording.Enabled;
        recordingToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await FlushSettingsAsync();
        Check(recorder.AutomaticOptions == originalRecording &&
            QuickButton(SidebarQuickSettings.AutomaticRecording).ToolTip?.ToString() ==
            QuickSettingTitle(SidebarQuickSettings.AutomaticRecording) + " · " + AppLocalization.Literal(originalRecording.Enabled ? "开" : "关"),
            "Recording full settings update the quick action");

        await ShowSettings(SettingsCategory.General);
        var identity = QaChildren<TextBox>(content).Single();
        var originalIdentity = identity.Text;
        identity.Text = "QA Driver";
        await FlushSettingsAsync();
        Check(store.GetAppSetting(PlayerIdentitySettings.PlayerCodeSettingKey) == "QA Driver", "Player code auto-saved");
        identity.Text = originalIdentity; await FlushSettingsAsync();
        var choice = QaChildren<CheckBox>(content).Single(box => Equals(box.Content, QuickSettingTitle(SidebarQuickSettings.Volume)));
        choice.IsChecked = false; choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!SidebarQuickSettings.Load(store.GetAppSetting(SidebarQuickSettings.StoreKey)).Contains(SidebarQuickSettings.Volume), "Quick selection persisted");
        choice.IsChecked = true; choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        hudComponentsExpanded = true;
        await ShowSettings(SettingsCategory.Hud);
        var originalOpacity = overlay.TimingLayout.Opacity;
        var originalBackdrop = overlay.TimingLayout.EstateRaceBackdropOpacity;
        var fullOpacity = QaChildren<Slider>(content).Single(slider => AutomationProperties.GetName(slider) == AppLocalization.Literal("整体不透明度"));
        var fullBackdrop = QaChildren<Slider>(content).Single(slider => AutomationProperties.GetName(slider) == QuickSettingTitle(SidebarQuickSettings.EstateBackdropOpacity));
        // Leave a full-page edit pending as the popup opens: it must not later overwrite the quick value.
        fullOpacity.Value = 0.8;
        async Task<Slider> OpenSlider(string id)
        {
            QuickButton(id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(150); UpdateLayout();
            Check(quickSettingPopup?.IsOpen == true, $"Slider popup opens for {id}");
            return QaChildren<Slider>(quickSettingPopup!.Child).Single();
        }
        var opacitySlider = await OpenSlider(SidebarQuickSettings.HudOpacity);
        Check(opacitySlider.Value == 80 && opacitySlider.Minimum == 25 && opacitySlider.Maximum == 100,
            "HUD opacity uses the saved setting and the same limits as the full page");
        opacitySlider.Value = 25; opacitySlider.Value = 100; opacitySlider.Value = 65;
        quickSettingPopup!.IsOpen = false;
        await FlushSettingsAsync();
        await Task.Delay(100); await FlushSettingsAsync();
        Check(overlay.TimingLayout.Opacity == 0.65 && fullOpacity.Value == 0.65 &&
            overlay.TimingLayout.EstateRaceBackdropOpacity == originalBackdrop &&
            JsonSerializer.Deserialize<OverlayLayout>(store.GetAppSetting("overlay.layout")!)!.Opacity == 0.65,
            "Rapid opacity edits persist the last value on dismiss and synchronize without changing the backdrop");

        var backdropSlider = await OpenSlider(SidebarQuickSettings.EstateBackdropOpacity);
        Check(backdropSlider.Minimum == 0 && backdropSlider.Maximum == 100, "Backdrop quick slider includes fully transparent");
        backdropSlider.Value = 0;
        await FlushSettingsAsync();
        Check(overlay.TimingLayout.EstateRaceBackdropOpacity == 0 && fullBackdrop.Value == 0 && overlay.TimingLayout.Opacity == 0.65,
            "Backdrop can become transparent without changing overall opacity");
        backdropSlider.Value = 35;
        await FlushSettingsAsync();
        UpdateLayout();
        CaptureVisual((FrameworkElement)quickSettingPopup!.Child, Path.Combine(directory, "quick-backdrop-popup.png"));
        quickSettingPopup.IsOpen = false;
        fullBackdrop.Value = 0.55;
        await FlushSettingsAsync();
        backdropSlider = await OpenSlider(SidebarQuickSettings.EstateBackdropOpacity);
        Check(Math.Abs(backdropSlider.Value - 55) < 0.000001, "Reopened slider reflects full-page edits");
        backdropSlider.Value = 45;
        await ShowSettings(SettingsCategory.General);
        Check(quickSettingPopup is null && overlay.TimingLayout.EstateRaceBackdropOpacity == 0.45 &&
            JsonSerializer.Deserialize<OverlayLayout>(store.GetAppSetting("overlay.layout")!)!.EstateRaceBackdropOpacity == 0.45,
            "Navigating away closes the slider and saves its last edit");
        await ShowSettings(SettingsCategory.Hud);
        fullOpacity = QaChildren<Slider>(content).Single(slider => AutomationProperties.GetName(slider) == AppLocalization.Literal("整体不透明度"));
        fullBackdrop = QaChildren<Slider>(content).Single(slider => AutomationProperties.GetName(slider) == QuickSettingTitle(SidebarQuickSettings.EstateBackdropOpacity));
        fullOpacity.Value = originalOpacity; fullBackdrop.Value = originalBackdrop;
        await FlushSettingsAsync();
        var originalIndicators = overlay.TimingLayout.ShowShiftIndicators;
        var indicatorsToggle = QaChildren<ToggleButton>(content).Single(toggle => toggle.Content is string text &&
            text.StartsWith(AppLocalization.Literal("HUD 换挡提示"), StringComparison.Ordinal));
        var quickIndicators = QaChildren<Button>(quickSettingsHost).Single(button =>
            AutomationProperties.GetName(button) == QuickSettingTitle(SidebarQuickSettings.ShiftIndicators));
        var savedVehiclePreferences = store.ListVehicleProfiles().Select(profile => (profile.Id, profile.ShiftRecommendationsEnabled)).ToArray();
        Check(quickIndicators.IsEnabled, "HUD shift visibility is available without a learned vehicle");
        quickIndicators.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(200); await FlushSettingsAsync();
        Check(overlay.TimingLayout.ShowShiftIndicators == !originalIndicators && indicatorsToggle.IsChecked == !originalIndicators,
            "HUD shift quick action updates full settings");
        Check(JsonSerializer.Deserialize<OverlayLayout>(store.GetAppSetting("overlay.layout")!)!.ShowShiftIndicators == !originalIndicators,
            "HUD shift visibility is saved");
        Check(savedVehiclePreferences.SequenceEqual(store.ListVehicleProfiles().Select(profile => (profile.Id, profile.ShiftRecommendationsEnabled))),
            "HUD shift visibility preserves all vehicle preferences");
        indicatorsToggle.IsChecked = originalIndicators;
        await FlushSettingsAsync(); RefreshSidebar();
        Check(overlay.TimingLayout.ShowShiftIndicators == originalIndicators &&
            Equals(indicatorsToggle.Content, AppLocalization.Literal(originalIndicators
                ? "HUD 换挡提示：显示" : "HUD 换挡提示：隐藏")) &&
            quickIndicators.ToolTip?.ToString() == AppLocalization.Literal(originalIndicators
                ? "HUD 换挡提示已显示，点击隐藏。" : "HUD 换挡提示已隐藏，点击显示。"), "Full settings updates HUD shift quick action");
        indicatorsToggle.BringIntoView();
        await Task.Delay(120); UpdateLayout();
        CaptureVisual(this, Path.Combine(directory, "hud-shift-indicators.png"));
        var originalMotion = overlay.TimingLayout.ReduceMotion;
        Check(!QaChildren<Button>(content).Any(button => button.Content is string text &&
            new[] { "保存", "应用", "Save", "Apply" }.Any(text.StartsWith)), "No HUD apply button");
        var motion = QaChildren<ToggleButton>(content).Single(toggle => toggle.Content is string text &&
            (text.StartsWith("减少动态") || text.StartsWith("Reduce motion", StringComparison.OrdinalIgnoreCase)));
        motion.IsChecked = !originalMotion;
        await FlushSettingsAsync();
        Check(overlay.TimingLayout.ReduceMotion == !originalMotion, "HUD changed immediately");
        var quickMotion = QaChildren<Button>(quickSettingsHost).Single(button => AutomationProperties.GetName(button) == QuickSettingTitle(SidebarQuickSettings.Motion));
        quickMotion.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(250); await FlushSettingsAsync();
        Check(overlay.TimingLayout.ReduceMotion == originalMotion && motion.IsChecked == originalMotion, "Quick and full HUD settings stay synchronized");

        await ShowSettings(SettingsCategory.General);
        var originalLanguage = AppLocalization.CurrentLanguage;
        var language = QaChildren<ComboBox>(content).Single(box => box.SelectedItem is AppLanguageOption);
        language.SelectedItem = AppLocalization.SupportedLanguages.First(option => option.Code != originalLanguage);
        await Task.Delay(250);
        Check(AppLocalization.CurrentLanguage != originalLanguage, "Language applied without restarting");
        var translatedSlider = await OpenSlider(SidebarQuickSettings.HudOpacity);
        CaptureVisual((FrameworkElement)quickSettingPopup!.Child, Path.Combine(directory, "quick-opacity-popup-translated.png"));
        Check(AutomationProperties.GetName(translatedSlider) == QuickSettingTitle(SidebarQuickSettings.HudOpacity),
            "Opacity popup follows the interface language");
        quickSettingPopup.IsOpen = false;
        language = QaChildren<ComboBox>(content).Single(box => box.SelectedItem is AppLanguageOption);
        language.SelectedItem = AppLocalization.SupportedLanguages.First(option => option.Code == originalLanguage);
        await Task.Delay(250);
        Check(AppLocalization.CurrentLanguage == originalLanguage, "Language switches back");
        Width = 1440; Height = 900;
        quickSettingIds = [SidebarQuickSettings.Mute, SidebarQuickSettings.ShiftIndicators, SidebarQuickSettings.HudOpacity,
            SidebarQuickSettings.EstateBackdropOpacity, SidebarQuickSettings.AutomaticRecording];
        BuildSidebarQuickSettings();
        navigation.SelectedIndex = 0;
        await Task.Delay(150); UpdateLayout();
        CaptureVisual(this, Path.Combine(directory, "quick-settings-selected-overview.png"));
        quickSettingIds = originalChoices;
        store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save(originalChoices));
        BuildSidebarQuickSettings(); navigation.SelectedIndex = 0;
        await Task.Delay(150); UpdateLayout();
        CaptureVisual(this, Path.Combine(directory, "sidebar-final-overview.png"));
        // Show the real connection row using synthetic states; never alter the telemetry source or stored settings.
        void CaptureConnection(string name, TelemetryStreamState state)
        {
            try
            {
                UpdateSidebarConnection(TelemetrySourceKind.Live, state, LazyForzaDefaults.TelemetryPort);
                UpdateLayout();
                var footer = (FrameworkElement)VisualTreeHelper.GetParent(quickSettingsHost);
                var width = footer.ActualWidth + 24;
                var height = footer.ActualHeight + 12;
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(Brush("SidebarBrush"), null, new Rect(0, 0, width, height));
                    dc.DrawRectangle(new VisualBrush(footer), null, new Rect(12, 0, footer.ActualWidth, footer.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, name + ".png"));
                encoder.Save(output);
            }
            finally { RefreshSidebar(); }
        }
        CaptureConnection("sidebar-connected", TelemetryStreamState.Live);
        CaptureConnection("sidebar-disconnected", TelemetryStreamState.Stale);
        Height = 640;
        await Task.Delay(120); UpdateLayout();
        CaptureConnection("sidebar-connected-compact", TelemetryStreamState.Live);
        Height = 900;
        File.WriteAllText(Path.Combine(directory, "settings-auto-qa.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<T> QaChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in QaChildren<T>(child)) yield return nested;
        }
    }
}
