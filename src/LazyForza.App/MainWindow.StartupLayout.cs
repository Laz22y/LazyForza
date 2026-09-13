using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LazyForza.App;

internal sealed partial class MainWindow
{
    // WPF sizes are device-independent; leave a small desktop margin instead of covering the taskbar.
    internal static Size ConstrainStartupWindowSize(Size workArea) => new(
        Math.Min(1440, Math.Max(1, workArea.Width - 32)),
        Math.Min(900, Math.Max(1, workArea.Height - 32)));

    private void SetStartupWindowSize(Size workArea)
    {
        var size = ConstrainStartupWindowSize(workArea);
        MinWidth = Math.Min(960, size.Width);
        MinHeight = Math.Min(640, size.Height);
        Width = size.Width;
        Height = size.Height;
    }

    private void FitStartupWindowToMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = StartupMonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new StartupMonitorInfo { Size = Marshal.SizeOf<StartupMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetStartupMonitorInfo(monitor, ref info)) return;
        var scale = Math.Max(96, GetStartupWindowDpi(handle)) / 96d;
        var available = info.WorkArea;
        SetStartupWindowSize(new Size((available.Right - available.Left) / scale, (available.Bottom - available.Top) / scale));
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        // Native coordinates also handle monitors with negative origins and differing scale factors.
        _ = SetStartupWindowPosition(handle, IntPtr.Zero,
            available.Left + (available.Right - available.Left - width) / 2,
            available.Top + (available.Bottom - available.Top - height) / 2, width, height, 0x14); // NOZORDER | NOACTIVATE
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern IntPtr StartupMonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetStartupWindowDpi(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStartupMonitorInfo(IntPtr monitor, ref StartupMonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStartupWindowPosition(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupMonitorInfo
    {
        public int Size;
        public StartupRect Monitor;
        public StartupRect WorkArea;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupRect { public int Left, Top, Right, Bottom; }
}
