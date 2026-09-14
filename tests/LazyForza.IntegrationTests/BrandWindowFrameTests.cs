using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
