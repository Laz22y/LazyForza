using System.Windows;

namespace LazyForza.Fh5MapProbe;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        var capturePath = CapturePath(e.Args);
        if (capturePath is null) return;
        await window.CaptureQaAsync(capturePath);
        window.Close();
        Shutdown();
    }

    private static string? CapturePath(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("--capture-qa=", StringComparison.OrdinalIgnoreCase))
                return arguments[index][13..];
            if (arguments[index].Equals("--capture-qa", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
                return arguments[index + 1];
        }
        return null;
    }
}
