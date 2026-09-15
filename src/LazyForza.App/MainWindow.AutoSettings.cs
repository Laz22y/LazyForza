using System.Windows;
using System.Windows.Controls;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    private readonly List<SettingsAutoApply> settingApplications = [];

    private SettingsAutoApply AutoApplySettings(FrameworkElement owner, Func<Task> apply, TextBlock status, int milliseconds = 180)
    {
        var automatic = new SettingsAutoApply(apply, exception =>
        {
            status.Text = AppLocalization.Format("settings.auto.failed", "未能应用：{0}", exception.Message);
            status.Visibility = Visibility.Visible;
        }, TimeSpan.FromMilliseconds(milliseconds));
        settingApplications.Add(automatic);
        return automatic;
    }

    internal Task FlushSettingsAsync() => Task.WhenAll(settingApplications.ToArray().Select(application => application.FlushAsync()));

    private static TextBlock AutoApplyStatus() => new()
    {
        Visibility = Visibility.Collapsed, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };
}
