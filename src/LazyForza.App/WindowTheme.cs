using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LazyForza.App;

internal static class WindowTheme
{
    internal static void UseDarkTitleBar(Window window) => window.SourceInitialized += (_, _) =>
    {
        var dark = 1;
        var handle = new WindowInteropHelper(window).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
