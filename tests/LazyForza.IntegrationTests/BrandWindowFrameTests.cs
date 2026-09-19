using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class BrandWindowFrameTests
{
    [TestMethod]
    [DataRow(960)]
    [DataRow(1440)]
    public void HeaderKeepsBrandMetadataAndCaptionTargetsApart(int width) => EstateHudRenderingTests.Sta(() =>
    {
        var window = new Window { Width = width, Height = 640 };
        try
        {
            var release = new ReleaseBrandLine("v1.5.3-alpha-3", "Radio Check");
            var frame = new BrandWindowFrame(window, release);
            window.Content = frame.TitleBar;
            frame.TitleBar.Measure(new Size(width, 48));
            frame.TitleBar.Arrange(new Rect(0, 0, width, 48)); frame.TitleBar.UpdateLayout();
            var buttons = Descendants<CaptionButton>(frame.TitleBar).ToArray();
            Assert.AreEqual(3, buttons.Length);
            Assert.AreEqual(ReleaseLabelMode.Combined, release.DisplayMode, "Normal preview metadata should fit without motion.");
            var start = release.TranslatePoint(new Point(), frame.TitleBar);
            var firstButton = buttons[0].TranslatePoint(new Point(), frame.TitleBar);
            var wordmark = Descendants<Image>(frame.TitleBar).Single();
            var wordmarkRight = wordmark.TranslatePoint(new Point(wordmark.ActualWidth, 0), frame.TitleBar).X;
            Assert.AreEqual(18, start.X - wordmarkRight, "Brand spacing must follow the wordmark, not the sidebar divider.");
            Assert.IsTrue(start.X + release.ActualWidth + 24 <= firstButton.X);
            Assert.AreEqual(1, Descendants<Image>(frame.TitleBar).Count());
            foreach (var button in buttons)
            {
                Assert.AreEqual(46, button.ActualWidth);
                Assert.IsTrue(button.ActualHeight >= 44);
                Assert.IsFalse(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
                Assert.IsInstanceOfType<ButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button));
            }
            Assert.AreEqual(WindowStyle.None, window.WindowStyle);
            Assert.IsFalse(window.AllowsTransparency);
            Assert.IsTrue(WindowChrome.GetWindowChrome(window).ResizeBorderThickness.Left > 0);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    [DoNotParallelize]
    public void NativeCaptionHitTestingAndSystemCommandsPreserveWindowBehavior() => EstateHudRenderingTests.Sta(() =>
    {
        var window = new Window { Width = 960, Height = 640, Left = -16000, Top = -16000,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        CancelEventHandler? closeHandler = null;
        try
        {
            var release = new ReleaseBrandLine("v1.5.3-alpha-3", "Radio Check");
            var frame = new BrandWindowFrame(window, release);
            var root = new DockPanel(); DockPanel.SetDock(frame.TitleBar, Dock.Top);
            root.Children.Add(frame.TitleBar); root.Children.Add(new Border { Background = Brushes.Black });
            window.Content = root;
            window.Show(); Pump(); window.UpdateLayout();
            var hwnd = new WindowInteropHelper(window).Handle;
            var buttons = Descendants<CaptionButton>(frame.TitleBar).ToDictionary(button => button.Action);
            IntPtr At(Point screen) => new(unchecked((int)((ushort)(short)screen.X | ((uint)(ushort)(short)screen.Y << 16))));
            int Hit(Point screen) => SendMessage(hwnd, 0x84, IntPtr.Zero, At(screen)).ToInt32();
            Assert.AreEqual(9, Hit(buttons[CaptionAction.Maximize].PointToScreen(new Point(23, 24))), "Maximize must expose HTMAXBUTTON to Snap Layouts.");
            Assert.AreEqual(2, Hit(frame.TitleBar.PointToScreen(new Point(750, 24))), "Blank header must remain a native drag region.");
            Assert.AreEqual(1, Hit(release.PointToScreen(new Point(10, 10))), "Release metadata must retain its full-text tooltip and hover pause.");
            Assert.AreEqual(12, Hit(buttons[CaptionAction.Maximize].PointToScreen(new Point(23, 1))), "The top resize strip must not open Snap Layouts.");
            Assert.AreEqual(1, Hit(buttons[CaptionAction.Close].PointToScreen(new Point(23, 24))), "Close is an accessible client button.");
            Assert.AreEqual(13, Hit(window.PointToScreen(new Point(1, 1))), "Corner resizing must remain native.");
            Assert.IsFalse(release.IsAnimationScheduled, "An inactive window must not animate its branding.");

            var maximize = buttons[CaptionAction.Maximize];
            var source = HwndSource.FromHwnd(hwnd)!;
            var nativeMaximizeCommands = 0;
            IntPtr ObserveCommand(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message == 0x112 && (wParam.ToInt64() & 0xfff0) == 0xf030)
                { nativeMaximizeCommands++; handled = true; }
                return IntPtr.Zero;
            }
            source.AddHook(ObserveCommand);
            try
            {
                var center = maximize.PointToScreen(new Point(23, 24));
                var outside = maximize.PointToScreen(new Point(-20, 24));
                IntPtr ClientAt(Point screen)
                {
                    var point = new NativePoint { X = (int)screen.X, Y = (int)screen.Y };
                    Assert.IsTrue(ScreenToClient(hwnd, ref point));
                    return At(new Point(point.X, point.Y));
                }
                void Down(int message = 0xA1)
                {
                    SendMessage(hwnd, message, new IntPtr(9), At(center));
                    Assert.AreEqual(hwnd, GetCapture(), "The custom button must own capture instead of entering native caption painting.");
                    Assert.IsTrue(maximize.NativePressed);
                }
                Down();
                SendMessage(hwnd, 0x200, new IntPtr(1), ClientAt(outside));
                Assert.IsFalse(maximize.NativePressed, "Dragging out must remove pressed feedback.");
                SendMessage(hwnd, 0x200, new IntPtr(1), ClientAt(center));
                Assert.IsTrue(maximize.NativePressed, "Dragging back in must restore pressed feedback.");
                SendMessage(hwnd, 0x202, IntPtr.Zero, ClientAt(outside)); Pump();
                Assert.AreEqual(0, nativeMaximizeCommands, "Releasing outside must cancel.");
                Assert.AreEqual(IntPtr.Zero, GetCapture());

                foreach (var cancel in new[] { 0x1F, 0x100, 0x215 })
                {
                    Down();
                    if (cancel == 0x215) ReleaseCapture();
                    else SendMessage(hwnd, cancel, new IntPtr(0x1B), IntPtr.Zero);
                    Assert.IsFalse(maximize.NativePressed);
                    Assert.IsFalse(maximize.NativeHover);
                    Assert.AreEqual(IntPtr.Zero, GetCapture());
                    SendMessage(hwnd, 0xA2, new IntPtr(9), At(center)); Pump();
                    Assert.AreEqual(0, nativeMaximizeCommands, "A canceled press must not execute on a late release.");
                }
                Down(); window.IsEnabled = false;
                Assert.IsFalse(maximize.NativePressed);
                Assert.AreEqual(IntPtr.Zero, GetCapture());
                window.IsEnabled = true;

                Down();
                SendMessage(hwnd, 0x202, IntPtr.Zero, ClientAt(center)); Pump();
                Assert.AreEqual(1, nativeMaximizeCommands, "A native caption click must execute exactly once.");
                Assert.IsFalse(maximize.NativePressed);
                Down(0xA3);
                SendMessage(hwnd, 0xA2, new IntPtr(9), At(center)); Pump();
                Assert.AreEqual(2, nativeMaximizeCommands, "A double-click message must use the same custom press path.");
            }
            finally { source.RemoveHook(ObserveCommand); }
            window.Hide();
            void Invoke(CaptionAction action)
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(buttons[action])!;
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                Pump();
            }
            Invoke(CaptionAction.Maximize);
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            Assert.IsTrue(buttons[CaptionAction.Maximize].Restore);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.IsTrue(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info));
            Assert.IsTrue(GetWindowRect(hwnd, out var bounds));
            Assert.AreEqual(info.Work, bounds, "Maximized custom chrome must fit the work area without overscan or covering the taskbar.");
            Invoke(CaptionAction.Maximize);
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            Invoke(CaptionAction.Minimize);
            Assert.AreEqual(WindowState.Minimized, window.WindowState);
            window.WindowState = WindowState.Normal;
            var closingCount = 0;
            closeHandler = (_, args) => { closingCount++; args.Cancel = true; window.Hide(); };
            window.Closing += closeHandler;
            Invoke(CaptionAction.Close);
            Assert.AreEqual(1, closingCount, "Closing must still reach the existing tray/exit handler.");
            Assert.IsFalse(window.IsVisible);
        }
        finally
        {
            if (closeHandler is not null) window.Closing -= closeHandler;
            window.Close(); Pump();
        }
    });

    [TestMethod]
    [DoNotParallelize]
    public void CaptionStatesKeepSmallGlyphsAndDoNotPaintAMouseFocusBox() => EstateHudRenderingTests.Sta(() =>
    {
        var window = new Window { Width = 960, Height = 640, Left = -16000, Top = -16000,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            var frame = new BrandWindowFrame(window, new ReleaseBrandLine("v1.5.4-dev", "Radio Check"));
            window.Content = frame.TitleBar;
            window.Show(); Pump(); window.UpdateLayout();
            var buttons = Descendants<CaptionButton>(frame.TitleBar).ToArray();
            var sheet = new DrawingVisual();
            using (var dc = sheet.RenderOpen())
            {
                dc.DrawRectangle((Brush)window.FindResource("SidebarBrush"), null, new Rect(0, 0, 520, 308));
                var labels = new[] { "默认", "悬停", "按下", "不可用" };
                for (var state = 0; state < labels.Length; state++)
                {
                    dc.DrawText(new FormattedText(labels[state], System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 13, Brushes.LightSlateGray, 1), new Point(20, 35 + state * 72));
                    for (var column = 0; column < 4; column++)
                    {
                        var button = buttons[column == 3 ? 2 : Math.Min(column, 1)];
                        button.Restore = column == 2;
                        button.Active = true;
                        button.NativeHover = state is 1 or 2;
                        button.NativePressed = state == 2;
                        button.IsEnabled = state != 3;
                        button.InvalidateVisual(); button.UpdateLayout();
                        var before = Render(button);
                        if (state == 0)
                        {
                            Assert.IsTrue(button.Focus());
                            button.UpdateLayout();
                            CollectionAssert.AreEqual(Pixels(before), Pixels(Render(button)),
                                "Acquiring focus after a click must not draw a white rectangle into the caption surface.");
                            Assert.AreSame(window.FindResource("KeyboardFocusVisual"), button.FocusVisualStyle,
                                "Keyboard navigation keeps the shared accent focus adorner.");
                        }
                        // Native and WPF input share the same drawing at 100%, 150%, and 200% scale.
                        foreach (var dpi in new[] { 96d, 144d, 192d })
                        {
                            var scaled = Render(button, dpi);
                            var pixel = new byte[4];
                            scaled.CopyPixels(new Int32Rect((int)(8 * dpi / 96), (int)(8 * dpi / 96), 1, 1), pixel, 4, 0);
                            Assert.AreEqual(state is 1 or 2 ? (byte)255 : (byte)0, pixel[3],
                                $"Unexpected caption surface: state {state}, glyph {column}, DPI {dpi}.");
                        }
                        dc.DrawImage(before, new Rect(112 + column * 88, 10 + state * 72, 69, 70.5));
                    }
                }
            }
            if (Environment.GetEnvironmentVariable("LAZYFORZA_CAPTION_QA") is { Length: > 0 } path)
            {
                Directory.CreateDirectory(path);
                var bitmap = new RenderTargetBitmap(520, 308, 96, 96, PixelFormats.Pbgra32); bitmap.Render(sheet);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(path, "caption-states.png")); encoder.Save(output);
            }
        }
        finally { window.Close(); Pump(); }
    });

    private static RenderTargetBitmap Render(CaptionButton button, double dpi = 96)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(button.ActualWidth * dpi / 96),
            (int)Math.Ceiling(button.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(button), null, new Rect(0, 0, button.ActualWidth, button.ActualHeight));
        bitmap.Render(visual);
        Assert.IsTrue(Pixels(bitmap).Any(value => value != 0), "The rendered caption must include its glyph.");
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    [TestMethod]
    [DataRow(1, 1, 13)]
    [DataRow(500, 1, 12)]
    [DataRow(959, 1, 14)]
    [DataRow(1, 300, 10)]
    [DataRow(959, 300, 11)]
    [DataRow(1, 639, 16)]
    [DataRow(500, 639, 15)]
    [DataRow(959, 639, 17)]
    [DataRow(500, 24, 0)]
    [DataRow(-1, 24, 0)]
    public void ResizeBordersExcludeTheInnerCaptionAndOutsidePoints(int x, int y, int expected)
    {
        Assert.AreEqual(expected, BrandWindowFrame.ResizeHit(new Point(x, y), new Size(960, 640), new Thickness(8)));
    }

    [TestMethod]
    public void HitCoordinatesPreserveNegativeMonitorOrigins()
    {
        Assert.AreEqual(new Point(-1920, -120), BrandWindowFrame.ScreenPoint(new IntPtr(unchecked((int)0xff88f880))));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
