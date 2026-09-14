using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace LazyForza.App;

/// <summary>Opaque WPF window with a branded caption; Windows retains moving, resizing and system commands.</summary>
internal sealed class BrandWindowFrame
{
    internal const double TitleHeight = 48;
    private readonly Window window;
    private readonly CaptionButton minimize, maximize, close;
    private readonly Image logo;
    private HwndSource? source;
    private bool tracking;
    internal Border TitleBar { get; }

    internal BrandWindowFrame(Window window, ReleaseBrandLine release)
    {
        this.window = window;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        // Do not use per-pixel window transparency; let DWM own shadows and rounded corners.
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = TitleHeight,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 420 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        logo = new Image
        {
            Source = BitmapFrame.Create(new Uri("pack://application:,,,/LazyForza.App;component/Assets/LazyForzaWordmark.png", UriKind.Absolute)),
            Width = 144, Height = 26, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 18, 0), SnapsToDevicePixels = true, IsHitTestVisible = false
        };
        AutomationProperties.SetName(logo, "LazyForza");
        row.Children.Add(logo);
        // Optically center the small text with the wordmark's visible strokes, rather than its font line box.
        release.Margin = new Thickness(0, 0, 24, 3);
        release.VerticalAlignment = VerticalAlignment.Center;
        // Reserve most of the header for dragging, yet permit long future release names.
        release.HorizontalAlignment = HorizontalAlignment.Stretch;
        WindowChrome.SetIsHitTestVisibleInChrome(release, true); // Preserve hover pause and full-text tooltip.
        Grid.SetColumn(release, 1);
        row.Children.Add(release);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        minimize = new CaptionButton(CaptionAction.Minimize);
        maximize = new CaptionButton(CaptionAction.Maximize);
        close = new CaptionButton(CaptionAction.Close);
        foreach (var button in new[] { minimize, maximize, close })
        {
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (_, _) => Execute(button.Action);
            actions.Children.Add(button);
        }
        Grid.SetColumn(actions, 3); row.Children.Add(actions);
        TitleBar = new Border { Height = TitleHeight, Child = row, BorderThickness = new Thickness(0, 0, 0, 1) };
        TitleBar.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");
        TitleBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        window.SourceInitialized += Attach;
        window.StateChanged += UpdateState;
        window.Activated += UpdateState;
        window.Deactivated += UpdateState;
        window.IsEnabledChanged += EnabledChanged;
        window.Closed += Detach;
        UpdateState(window, EventArgs.Empty);
    }

    private void Execute(CaptionAction action)
    {
        if (!window.IsEnabled) return;
        switch (action)
        {
            case CaptionAction.Minimize: SystemCommands.MinimizeWindow(window); break;
            case CaptionAction.Maximize:
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
                break;
            // Route through Closing so the existing tray/exit preference remains authoritative.
            case CaptionAction.Close: SystemCommands.CloseWindow(window); break;
        }
    }

    private void Attach(object? sender, EventArgs args)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(WndProc);
        var handle = source?.Handle ?? IntPtr.Zero;
        var rounded = 2; // DWMWCP_ROUND; ignored on Windows 10.
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        UpdateState(window, EventArgs.Empty);
    }

    private void UpdateState(object? sender, EventArgs args)
    {
        maximize.Restore = window.WindowState == WindowState.Maximized;
        foreach (var button in new[] { minimize, maximize, close })
        {
            button.Active = window.IsActive;
            if (!window.IsActive) button.NativeHover = false;
            button.RefreshLabel();
            button.InvalidateVisual();
        }
        logo.Opacity = window.IsActive ? 1 : .76;
    }

    private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateState(sender, EventArgs.Empty);

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x24) // WM_GETMINMAXINFO: full client bounds must fit this monitor's work area.
        {
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition = new(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
                limits.MaxSize = new(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                Marshal.StructureToPtr(limits, lParam, false);
                // Let WPF continue applying its minimum tracking size.
            }
        }
        // Only the maximize button is a native caption hit. This exposes Windows 11 Snap Layouts.
        // Windows tracks its non-client click and executes SC_MAXIMIZE / SC_RESTORE normally.
        if (message == 0x84 && window.IsEnabled && maximize.IsVisible) // WM_NCHITTEST
        {
            var screen = ScreenPoint(lParam);
            if (window.WindowState == WindowState.Normal)
            {
                // IsHitTestVisibleInChrome on a button otherwise takes precedence over the resize border.
                var edge = ResizeHit(window.PointFromScreen(screen), window.RenderSize, SystemParameters.WindowResizeBorderThickness);
                if (edge != 0) { handled = true; return new IntPtr(edge); }
            }
            var point = maximize.PointFromScreen(screen);
            if (new Rect(maximize.RenderSize).Contains(point))
            { handled = true; return new IntPtr(9); }
        }
        if (message == 0xA0) // WM_NCMOUSEMOVE
        {
            maximize.NativeHover = wParam.ToInt32() == 9 && window.IsEnabled;
            if (!tracking)
            {
                var request = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = hwnd }; // LEAVE | NONCLIENT
                tracking = TrackMouseEvent(ref request);
            }
        }
        else if (message is 0x2A2 or 0x215 or 0x200) // NCLEAVE, CAPTURECHANGED, client MOUSEMOVE
        {
            maximize.NativeHover = false;
            if (message == 0x2A2) tracking = false;
        }
        return IntPtr.Zero;
    }

    internal static Point ScreenPoint(IntPtr value) => new(unchecked((short)(value.ToInt64() & 0xffff)),
        unchecked((short)((value.ToInt64() >> 16) & 0xffff)));

    internal static int ResizeHit(Point point, Size size, Thickness border)
    {
        if (size.Width <= 0 || size.Height <= 0 || !new Rect(size).Contains(point)) return 0;
        var left = point.X < border.Left; var right = point.X >= size.Width - border.Right;
        var top = point.Y < border.Top; var bottom = point.Y >= size.Height - border.Bottom;
        if (top) return left ? 13 : right ? 14 : 12;
        if (bottom) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : 0;
    }

    private void Detach(object? sender, EventArgs args)
    {
        source?.RemoveHook(WndProc);
        source = null;
        window.SourceInitialized -= Attach;
        window.StateChanged -= UpdateState;
        window.Activated -= UpdateState;
        window.Deactivated -= UpdateState;
        window.IsEnabledChanged -= EnabledChanged;
        window.Closed -= Detach;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse { public int Size; public uint Flags; public IntPtr Window; public uint HoverTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouse request);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal enum CaptionAction { Minimize, Maximize, Close }

/// <summary>Vector caption glyphs, fixed hit targets, no idle animation; Button retains keyboard/automation semantics.</summary>
internal sealed class CaptionButton : Button
{
    internal CaptionAction Action { get; }
    internal bool Restore { get; set; }
    internal bool Active { get; set; }
    private bool nativeHover;
    internal bool NativeHover { get => nativeHover; set { if (nativeHover != value) { nativeHover = value; InvalidateVisual(); } } }

    internal CaptionButton(CaptionAction action)
    {
        Action = action;
        Width = 46; Height = BrandWindowFrame.TitleHeight - 1;
        Padding = new Thickness(0); BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Style = null;
        Template = new ControlTemplate(typeof(Button));
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RefreshLabel();
    }

    internal void RefreshLabel()
    {
        var text = Action switch
        {
            CaptionAction.Minimize => AppLocalization.Text("window.minimize", "最小化"),
            CaptionAction.Maximize when Restore => AppLocalization.Text("window.restore", "还原"),
            CaptionAction.Maximize => AppLocalization.Text("window.maximize", "最大化"),
            _ => AppLocalization.Text("window.close", "关闭")
        };
        ToolTip = text;
        AutomationProperties.SetName(this, text);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsKeyboardFocusedProperty ||
            e.Property == IsEnabledProperty) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var hover = IsEnabled && (IsMouseOver || NativeHover);
        var fill = hover ? Action == CaptionAction.Close ? Color.FromRgb(196, 43, 59)
            : Color.FromRgb(38, 48, 60) : Colors.Transparent;
        if (IsPressed && hover) fill.A = 185;
        dc.DrawRectangle(new SolidColorBrush(fill), null, new Rect(RenderSize));
        var foreground = hover || Active ? Colors.White : Color.FromRgb(154, 164, 178);
        if (!IsEnabled) foreground.A = 95;
        var pen = new Pen(new SolidColorBrush(foreground), 1);
        var x = Math.Floor((ActualWidth - 10) / 2) + .5;
        var y = Math.Floor((ActualHeight - 10) / 2) + .5;
        if (Action == CaptionAction.Minimize) dc.DrawLine(pen, new Point(x, y + 5), new Point(x + 10, y + 5));
        else if (Action == CaptionAction.Close)
        {
            dc.DrawLine(pen, new Point(x, y), new Point(x + 10, y + 10));
            dc.DrawLine(pen, new Point(x + 10, y), new Point(x, y + 10));
        }
        else if (Restore)
        {
            var back = new StreamGeometry();
            using (var context = back.Open())
            {
                context.BeginFigure(new Point(x + 2, y + 2), false, false);
                context.LineTo(new Point(x + 2, y), true, false); context.LineTo(new Point(x + 10, y), true, false);
                context.LineTo(new Point(x + 10, y + 8), true, false); context.LineTo(new Point(x + 8, y + 8), true, false);
            }
            dc.DrawGeometry(null, pen, back);
            dc.DrawRectangle(null, pen, new Rect(x, y + 2, 8, 8));
        }
        else dc.DrawRectangle(null, pen, new Rect(x, y, 10, 10));
        if (IsKeyboardFocused) dc.DrawRectangle(null, pen, new Rect(4.5, 4.5, Math.Max(0, ActualWidth - 9), Math.Max(0, ActualHeight - 9)));
    }
}
