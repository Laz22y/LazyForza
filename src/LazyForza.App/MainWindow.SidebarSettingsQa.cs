using System.IO;
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
    // Runs only with the explicit capture-qa workflow and its isolated profile/data directories.
    private async Task CaptureSidebarSettingsQaAsync(string directory)
    {
        var results = new List<string>();
        void Check(bool condition, string message)
        {
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
        foreach (var size in new[] { new Size(1440, 900), new Size(960, 640) })
        {
            Width = size.Width; Height = size.Height;
            await ShowSettings(SettingsCategory.General);
            if (content.Content is ScrollViewer scroll) scroll.ScrollToBottom();
            await Task.Delay(120);
            CaptureVisual(this, Path.Combine(directory, $"quick-settings-{size.Width:0}x{size.Height:0}.png"));
            Check(QaChildren<Button>(quickSettingsHost).Count() == 3, $"Three quick actions at {size}");
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

        await ShowSettings(SettingsCategory.Hud);
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
        language = QaChildren<ComboBox>(content).Single(box => box.SelectedItem is AppLanguageOption);
        language.SelectedItem = AppLocalization.SupportedLanguages.First(option => option.Code == originalLanguage);
        await Task.Delay(250);
        Check(AppLocalization.CurrentLanguage == originalLanguage, "Language switches back");
        Width = 1440; Height = 900;
        quickSettingIds = originalChoices;
        store.SetAppSetting(SidebarQuickSettings.StoreKey, SidebarQuickSettings.Save(originalChoices));
        BuildSidebarQuickSettings(); navigation.SelectedIndex = 0;
        await Task.Delay(150); UpdateLayout();
        CaptureVisual(this, Path.Combine(directory, "sidebar-final-overview.png"));
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
