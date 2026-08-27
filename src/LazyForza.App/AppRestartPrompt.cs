using System.Windows;

namespace LazyForza.App;

internal static class AppRestartPrompt
{
    public static void Show(Window owner, string message)
    {
        var result = AppDialog.ShowChoice(
            owner,
            message,
            AppLocalization.Text("settings.app.restartTitle", "需要重启"),
            AppLocalization.Text("settings.app.restartNow", "立即重启"),
            AppLocalization.Text("settings.app.restartLater", "稍后重启"),
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
            App.RequestRestart();
    }
}
