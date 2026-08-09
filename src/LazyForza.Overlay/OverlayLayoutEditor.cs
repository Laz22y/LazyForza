using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Overlay;

public sealed record ForzaHorizonWindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName,
    int PixelLeft,
    int PixelTop,
    int PixelWidth,
    int PixelHeight,
    double DpiScale,
    string MonitorId);

public static class ForzaHorizonWindow
{
    private const int SwRestore = 9;
    private const uint MonitorDefaultToNearest = 2;

    public static bool TryFind(out ForzaHorizonWindowInfo window)
    {
        var candidates = new List<(int Score, ForzaHorizonWindowInfo Window)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || handle == GetShellWindow()) return true;
            var title = WindowTitle(handle);
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return true;
            string processName;
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                processName = process.ProcessName;
            }
            catch
            {
                return true;
            }

            var score = CandidateScore(processName, title);
            if (score <= 0 || !TryClientBounds(handle, out var bounds)) return true;
            if (bounds.Width < 800 || bounds.Height < 450) return true;
            var dpi = Math.Max(96, GetDpiForWindow(handle));
            candidates.Add((
                score + (handle == GetForegroundWindow() ? 8 : 0),
                new ForzaHorizonWindowInfo(
                    handle,
                    title,
                    processName,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    dpi / 96d,
                    MonitorDeviceName(handle))));
            return true;
        }, IntPtr.Zero);

        var selected = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Window.PixelWidth * candidate.Window.PixelHeight)
            .FirstOrDefault();
        window = selected.Window!;
        return selected.Window is not null;
    }

    public static int CandidateScore(string processName, string windowTitle)
    {
        var normalizedProcess = processName.Replace(" ", string.Empty, StringComparison.Ordinal);
        var normalizedTitle = windowTitle.Replace(" ", string.Empty, StringComparison.Ordinal);
        var score = 0;
        if (normalizedProcess.Contains("ForzaHorizon6", StringComparison.OrdinalIgnoreCase))
            score += 120;
        else if (normalizedProcess.Contains("Forza", StringComparison.OrdinalIgnoreCase) &&
                 normalizedProcess.Contains("Horizon", StringComparison.OrdinalIgnoreCase))
            score += 45;
        if (normalizedTitle.Contains("ForzaHorizon6", StringComparison.OrdinalIgnoreCase))
            score += 100;
        else if (windowTitle.Contains("Forza Horizon", StringComparison.OrdinalIgnoreCase))
            score += 25;
        return score;
    }

    public static bool TryActivate(ForzaHorizonWindowInfo window)
    {
        if (window.Handle == IntPtr.Zero || !IsWindow(window.Handle)) return false;
        _ = ShowWindow(window.Handle, SwRestore);
        return SetForegroundWindow(window.Handle);
    }

    private static string WindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool TryClientBounds(IntPtr handle, out PixelBounds bounds)
    {
        bounds = default;
        if (!GetClientRect(handle, out var client)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin)) return false;
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0) return false;
        bounds = new PixelBounds(origin.X, origin.Y, width, height);
        return true;
    }

    private static string MonitorDeviceName(IntPtr handle)
    {
        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.DeviceName.TrimEnd('\0')
            : "primary";
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private readonly record struct PixelBounds(int Left, int Top, int Width, int Height);
}

public readonly record struct OverlaySnapResult(
    double Left,
    double Top,
    double? VerticalGuide,
    double? HorizontalGuide,
    string AlignmentText);

public static class OverlayLayoutSnapping
{
    public static OverlaySnapResult Snap(
        double left,
        double top,
        double width,
        double height,
        double workspaceWidth,
        double workspaceHeight,
        double tolerance = 8,
        double margin = 12)
    {
        var vertical = BestSnap(
        [
            new SnapCandidate(margin - left, margin, "左对齐"),
            new SnapCandidate(
                workspaceWidth / 2 - (left + width / 2),
                workspaceWidth / 2,
                "垂直居中"),
            new SnapCandidate(
                workspaceWidth - margin - (left + width),
                workspaceWidth - margin,
                "右对齐")
        ], tolerance);
        var horizontal = BestSnap(
        [
            new SnapCandidate(margin - top, margin, "顶部对齐"),
            new SnapCandidate(
                workspaceHeight / 2 - (top + height / 2),
                workspaceHeight / 2,
                "水平居中"),
            new SnapCandidate(
                workspaceHeight - margin - (top + height),
                workspaceHeight - margin,
                "底部对齐")
        ], tolerance);
        var labels = new[] { vertical?.Label, horizontal?.Label }
            .Where(label => !string.IsNullOrWhiteSpace(label));
        return new OverlaySnapResult(
            left + (vertical?.Offset ?? 0),
            top + (horizontal?.Offset ?? 0),
            vertical?.Guide,
            horizontal?.Guide,
            string.Join(" · ", labels));
    }

    private static SnapCandidate? BestSnap(
        IReadOnlyList<SnapCandidate> candidates,
        double tolerance) =>
        candidates
            .Where(candidate => Math.Abs(candidate.Offset) <= tolerance)
            .OrderBy(candidate => Math.Abs(candidate.Offset))
            .FirstOrDefault();

    private sealed record SnapCandidate(double Offset, double Guide, string Label);
}

public static class OverlayHudSnapping
{
    public static OverlaySnapResult AssistLapNearDashboard(
        double lapLeft,
        double lapTop,
        double lapWidth,
        double lapHeight,
        double dashboardLeft,
        double dashboardTop,
        double dashboardWidth,
        double dashboardHeight,
        double snapTolerance = 7,
        double guideTolerance = 30)
    {
        var horizontalOffset =
            dashboardLeft + dashboardWidth / 2 -
            (lapLeft + lapWidth / 2);
        var verticalOffset =
            dashboardTop + dashboardHeight / 2 -
            (lapTop + lapHeight / 2);
        var showVerticalGuide = Math.Abs(horizontalOffset) <= guideTolerance;
        var showHorizontalGuide = Math.Abs(verticalOffset) <= guideTolerance;
        var snapHorizontally = Math.Abs(horizontalOffset) <= snapTolerance;
        var snapVertically = Math.Abs(verticalOffset) <= snapTolerance;
        var assistedLeft = lapLeft + (snapHorizontally ? horizontalOffset : 0);
        var assistedTop = lapTop + (snapVertically ? verticalOffset : 0);
        var labels = new List<string>();
        if (snapHorizontally && snapVertically)
            labels.Add("圈速 HUD 已对齐仪表盘");
        else if (showVerticalGuide || showHorizontalGuide)
            labels.Add("接近仪表盘，可继续微调间隔");

        return new OverlaySnapResult(
            assistedLeft,
            assistedTop,
            showVerticalGuide ? dashboardLeft + dashboardWidth / 2 : null,
            showHorizontalGuide ? dashboardTop + dashboardHeight / 2 : null,
            string.Join(" · ", labels));
    }
}

public static class OverlayResizeMath
{
    public static double ScaleFromDrag(
        double startScale,
        double baseWidth,
        double baseHeight,
        double signedHorizontalChange,
        double signedVerticalChange,
        bool resizeHorizontally,
        bool resizeVertically)
    {
        var safeWidth = double.IsFinite(baseWidth) ? Math.Max(1, baseWidth) : 1;
        var safeHeight = double.IsFinite(baseHeight) ? Math.Max(1, baseHeight) : 1;
        double scaleChange;
        if (resizeHorizontally && resizeVertically)
        {
            var denominator = safeWidth * safeWidth + safeHeight * safeHeight;
            scaleChange =
                (signedHorizontalChange * safeWidth + signedVerticalChange * safeHeight) /
                denominator;
        }
        else if (resizeHorizontally)
        {
            scaleChange = signedHorizontalChange / safeWidth;
        }
        else if (resizeVertically)
        {
            scaleChange = signedVerticalChange / safeHeight;
        }
        else
        {
            scaleChange = 0;
        }

        return OverlayScaleSettings.Normalize(startScale + scaleChange);
    }

    public static double ScaleFromCenteredDrag(
        double startScale,
        double baseWidth,
        double baseHeight,
        double signedHorizontalChange,
        double signedVerticalChange,
        bool resizeHorizontally,
        bool resizeVertically) =>
        ScaleFromDrag(
            startScale,
            baseWidth,
            baseHeight,
            signedHorizontalChange * 2,
            signedVerticalChange * 2,
            resizeHorizontally,
            resizeVertically);
}

internal sealed class OverlayLayoutEditorWindow : Window
{
    private const int SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private readonly ForzaHorizonWindowInfo target;
    private readonly OverlayLayout original;
    private readonly Canvas canvas;
    private readonly EditorHud dashboardHud;
    private readonly EditorHud lapHud;
    private readonly EditorHud driftHud;
    private readonly EditorHud estateRaceHud;
    private readonly Line verticalGuide;
    private readonly Line horizontalGuide;
    private readonly TextBlock alignmentText;
    private readonly TextBlock metricsText;
    private readonly ToggleButton dashboardSelection;
    private readonly ToggleButton lapSelection;
    private readonly ToggleButton driftSelection;
    private readonly ToggleButton estateRaceSelection;
    private readonly ToggleButton dashboardPreviewVisibility;
    private readonly ToggleButton lapPreviewVisibility;
    private readonly ToggleButton driftPreviewVisibility;
    private readonly ToggleButton estateRacePreviewVisibility;
    private readonly Border dashboardWidgetPanel;
    private readonly ComboBox dashboardWidgetSelection;
    private readonly ToggleButton dashboardWidgetVisibility;
    private readonly Border dashboardWidgetFrame;
    private readonly Border estateRaceWidgetPanel;
    private readonly ComboBox estateRaceWidgetSelection;
    private readonly ToggleButton estateRaceWidgetVisibility;
    private readonly Border estateRaceWidgetFrame;
    private readonly List<(Thumb Thumb, ResizeHandle Handle)> handles = [];
    private EditorHud selectedHud;
    private EditorHud? draggingHud;
    private EditorHud? resizingHud;
    private DashboardWidgetLayout dashboardWidgets;
    private EstateRaceHudLayout estateRaceWidgets;
    private DashboardWidgetKind selectedDashboardWidget;
    private EstateRaceHudWidgetKind selectedEstateRaceWidget;
    private DashboardWidgetPlacement widgetDragStart = new();
    private EstateRaceHudWidgetPlacement estateRaceWidgetDragStart =
        EstateRaceHudLayoutSettings.Default.Get(EstateRaceHudWidgetKind.Leaderboard);
    private bool draggingDashboardWidget;
    private bool draggingEstateRaceWidget;
    private bool dashboardPreviewVisible = true;
    private bool lapPreviewVisible = true;
    private bool driftPreviewVisible = true;
    private bool estateRacePreviewVisible = true;
    private bool lapAttached;
    private Point dragOrigin;
    private Point widgetDragOrigin;
    private double dragStartLeft;
    private double dragStartTop;
    private double resizeStartCenterX;
    private double resizeStartCenterY;
    private double resizeStartScale;
    private double resizeAccumulatedX;
    private double resizeAccumulatedY;
    private readonly double estateRaceViewportWidth;
    private readonly double estateRaceViewportHeight;

    public OverlayLayoutEditorWindow(
        Func<IReadOnlyList<IHudContribution>> getContributions,
        OverlayLayout layout,
        ForzaHorizonWindowInfo target)
    {
        this.target = target;
        original = OverlayLayoutGeometry.Normalize(
            layout with { ClickThrough = true, IsLocked = true });
        dashboardWidgets = DashboardWidgetLayoutSettings.Normalize(
            original.DashboardWidgets);
        estateRaceWidgets = EstateRaceHudLayoutSettings.Normalize(
            original.EstateRaceWidgets);
        selectedDashboardWidget = DashboardWidgetKind.SpeedGear;
        selectedEstateRaceWidget = EstateRaceHudWidgetKind.Leaderboard;
        lapAttached = original.LapHudAttachedToDashboard;
        Title = "设置 Overlay 布局";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(172, 28, 32, 36));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = target.PixelLeft / target.DpiScale;
        Top = target.PixelTop / target.DpiScale;
        Width = target.PixelWidth / target.DpiScale;
        Height = target.PixelHeight / target.DpiScale;
        estateRaceViewportWidth = Width;
        estateRaceViewportHeight = Height;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Foreground = Brushes.White;

        var root = new Grid();
        canvas = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent
        };
        canvas.PreviewMouseLeftButtonDown += SelectEstateRaceWidgetAtPointer;
        root.Children.Add(canvas);

        dashboardHud = CreateHud(
            EditorHudKind.Dashboard,
            getContributions,
            HudSurfaceKind.Dashboard);
        lapHud = CreateHud(
            EditorHudKind.Lap,
            getContributions,
            HudSurfaceKind.Lap);
        driftHud = CreateHud(
            EditorHudKind.Drift,
            getContributions,
            HudSurfaceKind.Drift);
        estateRaceHud = CreateHud(
            EditorHudKind.EstateRace,
            getContributions,
            HudSurfaceKind.EstateRace);
        selectedHud = dashboardHud;
        canvas.Children.Add(dashboardHud.Frame);
        canvas.Children.Add(lapHud.Frame);
        canvas.Children.Add(driftHud.Frame);
        canvas.Children.Add(estateRaceHud.Frame);

        dashboardWidgetFrame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(73, 211, 235)),
            Background = new SolidColorBrush(Color.FromArgb(18, 73, 211, 235)),
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动当前选中的仪表盘部件"
        };
        dashboardWidgetFrame.MouseLeftButtonDown += BeginDashboardWidgetDrag;
        dashboardWidgetFrame.MouseMove += ContinueDashboardWidgetDrag;
        dashboardWidgetFrame.MouseLeftButtonUp += EndDashboardWidgetDrag;
        Canvas.SetZIndex(dashboardWidgetFrame, 24);
        canvas.Children.Add(dashboardWidgetFrame);

        estateRaceWidgetFrame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 99, 255)),
            Background = new SolidColorBrush(Color.FromArgb(18, 180, 99, 255)),
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动当前选中的地产赛事 HUD 部件"
        };
        estateRaceWidgetFrame.MouseLeftButtonDown += BeginEstateRaceWidgetDrag;
        estateRaceWidgetFrame.MouseMove += ContinueEstateRaceWidgetDrag;
        estateRaceWidgetFrame.MouseLeftButtonUp += EndEstateRaceWidgetDrag;
        Canvas.SetZIndex(estateRaceWidgetFrame, 25);
        canvas.Children.Add(estateRaceWidgetFrame);

        verticalGuide = GuideLine();
        horizontalGuide = GuideLine();
        canvas.Children.Add(verticalGuide);
        canvas.Children.Add(horizontalGuide);

        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var thumb = CreateResizeThumb(handle);
            thumb.DragStarted += (_, _) => BeginResize();
            thumb.DragDelta += (_, eventArgs) => Resize(handle, eventArgs);
            thumb.DragCompleted += (_, _) =>
            {
                resizingHud = null;
                HideGuides();
            };
            handles.Add((thumb, handle));
            canvas.Children.Add(thumb);
        }

        var header = new Border
        {
            Margin = new Thickness(18),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(225, 13, 18, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, 81, 96, 108)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "设置 Overlay 布局",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "选择 HUD 后拖动或中心缩放；赛事四块使用独立全屏画布，不跟随主仪表盘。Esc 取消，Ctrl+S 保存。",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201))
        });
        headerGrid.Children.Add(heading);

        var selectionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        dashboardSelection = new ToggleButton
        {
            Content = "仪表盘 HUD",
            Padding = new Thickness(11, 6, 11, 6),
            MinWidth = 92
        };
        dashboardSelection.Click += (_, _) => SelectHud(dashboardHud);
        selectionPanel.Children.Add(dashboardSelection);
        lapSelection = new ToggleButton
        {
            Content = "圈速 HUD",
            Padding = new Thickness(11, 6, 11, 6),
            Margin = new Thickness(7, 0, 0, 0),
            MinWidth = 82
        };
        lapSelection.Click += (_, _) => SelectHud(lapHud);
        selectionPanel.Children.Add(lapSelection);
        driftSelection = new ToggleButton
        {
            Content = "漂移 HUD · 实验性",
            Padding = new Thickness(11, 6, 11, 6),
            Margin = new Thickness(7, 0, 0, 0),
            MinWidth = 126
        };
        driftSelection.Click += (_, _) => SelectHud(driftHud);
        selectionPanel.Children.Add(driftSelection);
        estateRaceSelection = new ToggleButton
        {
            Content = "地产赛事 HUD",
            Padding = new Thickness(11, 6, 11, 6),
            Margin = new Thickness(7, 0, 0, 0),
            MinWidth = 108
        };
        estateRaceSelection.Click += (_, _) => SelectHud(estateRaceHud);
        selectionPanel.Children.Add(estateRaceSelection);
        var attachLap = new Button
        {
            Content = "使圈速 HUD 吸附仪表盘",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "将圈速 HUD 的中心、位置和缩放精确同步到仪表盘"
        };
        attachLap.Click += (_, _) => AttachLapToDashboard();
        selectionPanel.Children.Add(attachLap);
        Grid.SetColumn(selectionPanel, 2);
        headerGrid.Children.Add(selectionPanel);

        metricsText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(61, 232, 143)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(metricsText, 4);
        headerGrid.Children.Add(metricsText);
        header.Child = headerGrid;
        root.Children.Add(header);

        var previewVisibilityPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        previewVisibilityPanel.Children.Add(new TextBlock
        {
            Text = "编辑器显示",
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201))
        });
        dashboardPreviewVisibility = new ToggleButton
        {
            Content = "仪表盘：显示",
            IsChecked = true,
            Padding = new Thickness(9, 5, 9, 5)
        };
        dashboardPreviewVisibility.Click += (_, _) =>
            SetHudPreviewVisibility(
                dashboardHud,
                dashboardPreviewVisibility.IsChecked == true);
        previewVisibilityPanel.Children.Add(dashboardPreviewVisibility);
        lapPreviewVisibility = new ToggleButton
        {
            Content = "圈速：显示",
            IsChecked = true,
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(6, 0, 0, 0)
        };
        lapPreviewVisibility.Click += (_, _) =>
            SetHudPreviewVisibility(
                lapHud,
                lapPreviewVisibility.IsChecked == true);
        previewVisibilityPanel.Children.Add(lapPreviewVisibility);
        driftPreviewVisibility = new ToggleButton
        {
            Content = "漂移：显示",
            IsChecked = true,
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(6, 0, 0, 0)
        };
        driftPreviewVisibility.Click += (_, _) =>
            SetHudPreviewVisibility(
                driftHud,
                driftPreviewVisibility.IsChecked == true);
        previewVisibilityPanel.Children.Add(driftPreviewVisibility);
        estateRacePreviewVisibility = new ToggleButton
        {
            Content = "赛事：显示",
            IsChecked = true,
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(6, 0, 0, 0)
        };
        estateRacePreviewVisibility.Click += (_, _) =>
            SetHudPreviewVisibility(
                estateRaceHud,
                estateRacePreviewVisibility.IsChecked == true);
        previewVisibilityPanel.Children.Add(estateRacePreviewVisibility);
        var previewVisibilityBar = new Border
        {
            Margin = new Thickness(18, 82, 18, 0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(225, 13, 18, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 81, 96, 108)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = previewVisibilityPanel
        };
        root.Children.Add(previewVisibilityBar);

        var widgetControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        widgetControls.Children.Add(new TextBlock
        {
            Text = "仪表盘部件",
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201))
        });
        dashboardWidgetSelection = new ComboBox
        {
            Width = 132,
            Padding = new Thickness(7, 4, 7, 4),
            ItemsSource = Enum.GetValues<DashboardWidgetKind>()
                .Select(DashboardWidgetName)
                .ToArray(),
            SelectedIndex = (int)selectedDashboardWidget
        };
        dashboardWidgetSelection.SelectionChanged += (_, _) =>
        {
            if (dashboardWidgetSelection.SelectedIndex < 0) return;
            selectedDashboardWidget =
                (DashboardWidgetKind)dashboardWidgetSelection.SelectedIndex;
            RefreshDashboardWidgetControls();
            PositionDashboardWidgetFrame();
        };
        widgetControls.Children.Add(dashboardWidgetSelection);
        dashboardWidgetVisibility = new ToggleButton
        {
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(7, 0, 0, 0)
        };
        dashboardWidgetVisibility.Click += (_, _) =>
        {
            var current = dashboardWidgets.Get(selectedDashboardWidget);
            dashboardWidgets = dashboardWidgets.Set(
                selectedDashboardWidget,
                current with
                {
                    IsVisible = dashboardWidgetVisibility.IsChecked == true
                });
            ApplyHuds(clamp: false);
        };
        widgetControls.Children.Add(dashboardWidgetVisibility);
        var resetDashboardWidgets = new Button
        {
            Content = "恢复当前仪表盘布局",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(7, 0, 0, 0),
            ToolTip = "恢复全部仪表盘部件的默认显示状态和当前位置"
        };
        resetDashboardWidgets.Click += (_, _) => ResetDashboardWidgets();
        widgetControls.Children.Add(resetDashboardWidgets);
        dashboardWidgetPanel = new Border
        {
            Margin = new Thickness(18, 82, 18, 0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(225, 13, 18, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 81, 96, 108)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = widgetControls
        };
        root.Children.Add(dashboardWidgetPanel);

        var estateRaceWidgetControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        estateRaceWidgetControls.Children.Add(new TextBlock
        {
            Text = "地产赛事部件",
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201))
        });
        estateRaceWidgetSelection = new ComboBox
        {
            Width = 126,
            Padding = new Thickness(7, 4, 7, 4),
            ItemsSource = Enum.GetValues<EstateRaceHudWidgetKind>()
                .Select(EstateRaceWidgetName)
                .ToArray(),
            SelectedIndex = (int)selectedEstateRaceWidget
        };
        estateRaceWidgetSelection.SelectionChanged += (_, _) =>
        {
            if (estateRaceWidgetSelection.SelectedIndex < 0) return;
            selectedEstateRaceWidget =
                (EstateRaceHudWidgetKind)estateRaceWidgetSelection.SelectedIndex;
            RefreshEstateRaceWidgetControls();
            PositionEstateRaceWidgetFrame();
            PositionHandles();
        };
        estateRaceWidgetControls.Children.Add(estateRaceWidgetSelection);
        estateRaceWidgetVisibility = new ToggleButton
        {
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(7, 0, 0, 0)
        };
        estateRaceWidgetVisibility.Click += (_, _) =>
        {
            var current = estateRaceWidgets.Get(selectedEstateRaceWidget);
            estateRaceWidgets = estateRaceWidgets.Set(
                selectedEstateRaceWidget,
                current with
                {
                    IsVisible = estateRaceWidgetVisibility.IsChecked == true
                });
            ApplyHuds(clamp: false);
        };
        estateRaceWidgetControls.Children.Add(estateRaceWidgetVisibility);
        var resetEstateRaceWidgets = new Button
        {
            Content = "恢复赛事默认布局",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(7, 0, 0, 0),
            ToolTip = "恢复全部地产赛事 HUD 部件的默认显示状态、位置和大小"
        };
        resetEstateRaceWidgets.Click += (_, _) => ResetEstateRaceWidgets();
        estateRaceWidgetControls.Children.Add(resetEstateRaceWidgets);
        estateRaceWidgetPanel = new Border
        {
            Margin = new Thickness(18, 82, 18, 0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(225, 13, 18, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 81, 96, 108)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = estateRaceWidgetControls
        };
        root.Children.Add(estateRaceWidgetPanel);

        var footer = new Border
        {
            Margin = new Thickness(18),
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(232, 13, 18, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, 81, 96, 108)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var footerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        alignmentText = new TextBlock
        {
            Text = DefaultAlignmentText,
            Width = 350,
            Margin = new Thickness(4, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201))
        };
        footerPanel.Children.Add(alignmentText);
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 82,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => Cancel();
        footerPanel.Children.Add(cancel);
        var save = new Button
        {
            Content = "保存布局",
            MinWidth = 96,
            Padding = new Thickness(14, 7, 14, 7),
            FontWeight = FontWeights.SemiBold
        };
        save.Click += (_, _) => Save();
        footerPanel.Children.Add(save);
        footer.Child = footerPanel;
        root.Children.Add(footer);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _ = SetWindowPos(
                handle,
                new IntPtr(-1),
                target.PixelLeft,
                target.PixelTop,
                target.PixelWidth,
                target.PixelHeight,
                SwpNoActivate | SwpShowWindow);
        };
        Loaded += (_, _) => InitializeHuds();
        SizeChanged += (_, _) =>
        {
            if (IsLoaded) ApplyHuds(clamp: true);
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private const string DefaultAlignmentText =
        "HUD 可临时隐藏后重叠摆放；地产赛事四块可在整个游戏窗口内独立调整";

    public OverlayLayout? Result { get; private set; }

    private EditorHud CreateHud(
        EditorHudKind kind,
        Func<IReadOnlyList<IHudContribution>> getContributions,
        HudSurfaceKind surfaceKind)
    {
        var frame = new Border
        {
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = new HudSurface(
                getContributions,
                () => CurrentLayout(),
                surfaceKind,
                layoutPreview: true)
        };
        var hud = new EditorHud(kind, frame);
        if (kind == EditorHudKind.EstateRace)
        {
            frame.IsHitTestVisible = false;
        }
        else
        {
            frame.MouseLeftButtonDown += (_, eventArgs) => BeginHudDrag(hud, eventArgs);
            frame.MouseMove += (_, eventArgs) => ContinueHudDrag(hud, eventArgs);
            frame.MouseLeftButtonUp += (_, eventArgs) => EndHudDrag(hud, eventArgs);
        }
        return hud;
    }

    private void InitializeHuds()
    {
        var dashboard = OverlayLayoutGeometry.Bounds(original, OverlayHudKind.Dashboard);
        var lap = OverlayLayoutGeometry.Bounds(original, OverlayHudKind.Lap);
        var drift = OverlayLayoutGeometry.Bounds(original, OverlayHudKind.Drift);
        dashboardHud.Scale = Math.Min(original.Scale, MaximumScale());
        dashboardHud.Left = dashboard.Left - Left;
        dashboardHud.Top = dashboard.Top - Top;
        lapHud.Scale = Math.Min(original.LapHudScale ?? original.Scale, MaximumScale());
        lapHud.Left = lap.Left - Left;
        lapHud.Top = lap.Top - Top;
        driftHud.Scale = Math.Min(
            original.DriftHudScale ?? original.Scale,
            MaximumScale());
        driftHud.Left = drift.Left - Left;
        driftHud.Top = drift.Top - Top;
        estateRaceHud.Scale = 1;
        estateRaceHud.Left = 0;
        estateRaceHud.Top = 0;
        if (lapAttached) SyncLapToDashboard();
        SelectHud(dashboardHud);
        ApplyHuds(clamp: true);
    }

    private OverlayLayout CurrentLayout()
    {
        var current = original with
        {
            Left = Left + dashboardHud.Left,
            Top = Top + dashboardHud.Top,
            Scale = dashboardHud.Scale,
            LapHudLeft = Left + lapHud.Left,
            LapHudTop = Top + lapHud.Top,
            LapHudScale = lapHud.Scale,
            LapHudAttachedToDashboard = lapAttached,
            DriftHudLeft = Left + driftHud.Left,
            DriftHudTop = Top + driftHud.Top,
            DriftHudScale = driftHud.Scale,
            EstateRaceHudLeft = Left + estateRaceHud.Left,
            EstateRaceHudTop = Top + estateRaceHud.Top,
            EstateRaceHudWidth = estateRaceViewportWidth,
            EstateRaceHudHeight = estateRaceViewportHeight,
            DashboardWidgets = dashboardWidgets,
            EstateRaceWidgets = estateRaceWidgets,
            MonitorId = target.MonitorId,
            ClickThrough = true,
            IsLocked = true
        };
        return OverlayLayoutGeometry.Normalize(current);
    }

    private void ApplyHuds(bool clamp)
    {
        if (lapAttached) SyncLapToDashboard();
        ApplyHud(dashboardHud, clamp);
        ApplyHud(lapHud, clamp);
        ApplyHud(driftHud, clamp);
        ApplyHud(estateRaceHud, clamp);
        dashboardHud.Frame.Visibility = dashboardPreviewVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        lapHud.Frame.Visibility = lapPreviewVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        driftHud.Frame.Visibility = driftPreviewVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        estateRaceHud.Frame.Visibility = estateRacePreviewVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        PositionDashboardWidgetFrame();
        PositionEstateRaceWidgetFrame();
        PositionHandles();
        UpdateSelectionVisuals();
    }

    private void ApplyHud(EditorHud hud, bool clamp)
    {
        var width = HudWidth(hud);
        var height = HudHeight(hud);
        if (clamp)
        {
            hud.Left = Math.Clamp(hud.Left, 0, Math.Max(0, ActualWidth - width));
            hud.Top = Math.Clamp(hud.Top, 0, Math.Max(0, ActualHeight - height));
        }
        hud.Frame.Width = width;
        hud.Frame.Height = height;
        Canvas.SetLeft(hud.Frame, hud.Left);
        Canvas.SetTop(hud.Frame, hud.Top);
    }

    private void SelectHud(EditorHud hud)
    {
        selectedHud = hud;
        Canvas.SetZIndex(dashboardHud.Frame, hud == dashboardHud ? 14 : 10);
        Canvas.SetZIndex(lapHud.Frame, hud == lapHud ? 14 : 10);
        Canvas.SetZIndex(driftHud.Frame, hud == driftHud ? 14 : 10);
        Canvas.SetZIndex(estateRaceHud.Frame, hud == estateRaceHud ? 14 : 10);
        foreach (var (thumb, _) in handles) Canvas.SetZIndex(thumb, 30);
        UpdateSelectionVisuals();
        PositionDashboardWidgetFrame();
        PositionEstateRaceWidgetFrame();
        PositionHandles();
    }

    private void UpdateSelectionVisuals()
    {
        dashboardSelection.IsChecked = selectedHud == dashboardHud;
        lapSelection.IsChecked = selectedHud == lapHud;
        driftSelection.IsChecked = selectedHud == driftHud;
        estateRaceSelection.IsChecked = selectedHud == estateRaceHud;
        dashboardWidgetPanel.Visibility =
            selectedHud == dashboardHud && dashboardPreviewVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        estateRaceWidgetPanel.Visibility =
            selectedHud == estateRaceHud && estateRacePreviewVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        dashboardHud.Frame.BorderBrush = new SolidColorBrush(
            selectedHud == dashboardHud
                ? Color.FromRgb(61, 232, 143)
                : Color.FromArgb(150, 61, 232, 143));
        lapHud.Frame.BorderBrush = new SolidColorBrush(
            selectedHud == lapHud
                ? Color.FromRgb(73, 211, 235)
                : Color.FromArgb(150, 73, 211, 235));
        driftHud.Frame.BorderBrush = new SolidColorBrush(
            selectedHud == driftHud
                ? Color.FromRgb(242, 184, 39)
                : Color.FromArgb(150, 242, 184, 39));
        estateRaceHud.Frame.BorderBrush = new SolidColorBrush(
            selectedHud == estateRaceHud
                ? Color.FromRgb(180, 99, 255)
                : Color.FromArgb(135, 180, 99, 255));
        dashboardHud.Frame.BorderThickness = new Thickness(
            selectedHud == dashboardHud ? 2.5 : 1.2);
        lapHud.Frame.BorderThickness = new Thickness(
            selectedHud == lapHud ? 2.5 : 1.2);
        driftHud.Frame.BorderThickness = new Thickness(
            selectedHud == driftHud ? 2.5 : 1.2);
        estateRaceHud.Frame.BorderThickness = new Thickness(
            selectedHud == estateRaceHud ? 2.5 : 1.2);
        var name = selectedHud.Kind switch
        {
            EditorHudKind.Dashboard => "仪表盘 HUD",
            EditorHudKind.Lap => "圈速 HUD",
            EditorHudKind.Drift => "漂移 HUD · 实验性",
            _ => $"地产赛事 · {EstateRaceWidgetName(selectedEstateRaceWidget)}"
        };
        var attachment = selectedHud.Kind == EditorHudKind.Lap && lapAttached
            ? " · 已吸附"
            : string.Empty;
        metricsText.Text =
            $"{name}{attachment} · {SelectedScale():P1} · " +
            $"{SelectedWidth():0} × {SelectedHeight():0}";
        RefreshDashboardWidgetControls();
        RefreshEstateRaceWidgetControls();
    }

    private void PositionHandles()
    {
        var handlesVisible = IsHudPreviewVisible(selectedHud) &&
            (selectedHud != estateRaceHud ||
             estateRaceWidgets.Get(selectedEstateRaceWidget).IsVisible);
        var left = selectedHud == estateRaceHud
            ? Canvas.GetLeft(estateRaceWidgetFrame)
            : selectedHud.Left;
        var top = selectedHud == estateRaceHud
            ? Canvas.GetTop(estateRaceWidgetFrame)
            : selectedHud.Top;
        var width = SelectedWidth();
        var height = SelectedHeight();
        foreach (var (thumb, handle) in handles)
        {
            thumb.Visibility = handlesVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!handlesVisible) continue;
            var point = handle switch
            {
                ResizeHandle.TopLeft => new Point(left, top),
                ResizeHandle.Top => new Point(left + width / 2, top),
                ResizeHandle.TopRight => new Point(left + width, top),
                ResizeHandle.Right => new Point(left + width, top + height / 2),
                ResizeHandle.BottomRight => new Point(left + width, top + height),
                ResizeHandle.Bottom => new Point(left + width / 2, top + height),
                ResizeHandle.BottomLeft => new Point(left, top + height),
                _ => new Point(left, top + height / 2)
            };
            Canvas.SetLeft(thumb, point.X - thumb.Width / 2);
            Canvas.SetTop(thumb, point.Y - thumb.Height / 2);
        }
    }

    private bool IsHudPreviewVisible(EditorHud hud) => hud.Kind switch
    {
        EditorHudKind.Dashboard => dashboardPreviewVisible,
        EditorHudKind.Lap => lapPreviewVisible,
        EditorHudKind.Drift => driftPreviewVisible,
        _ => estateRacePreviewVisible
    };

    private void SetHudPreviewVisibility(EditorHud hud, bool isVisible)
    {
        if (hud == dashboardHud) dashboardPreviewVisible = isVisible;
        else if (hud == lapHud) lapPreviewVisible = isVisible;
        else if (hud == driftHud) driftPreviewVisible = isVisible;
        else estateRacePreviewVisible = isVisible;

        dashboardPreviewVisibility.IsChecked = dashboardPreviewVisible;
        dashboardPreviewVisibility.Content = dashboardPreviewVisible
            ? "仪表盘：显示"
            : "仪表盘：隐藏";
        lapPreviewVisibility.IsChecked = lapPreviewVisible;
        lapPreviewVisibility.Content = lapPreviewVisible
            ? "圈速：显示"
            : "圈速：隐藏";
        driftPreviewVisibility.IsChecked = driftPreviewVisible;
        driftPreviewVisibility.Content = driftPreviewVisible
            ? "漂移：显示"
            : "漂移：隐藏";
        estateRacePreviewVisibility.IsChecked = estateRacePreviewVisible;
        estateRacePreviewVisibility.Content = estateRacePreviewVisible
            ? "赛事：显示"
            : "赛事：隐藏";

        if (!isVisible && selectedHud == hud)
        {
            var replacement = new[] { dashboardHud, lapHud, driftHud, estateRaceHud }
                .FirstOrDefault(IsHudPreviewVisible);
            if (replacement is not null) SelectHud(replacement);
        }
        ApplyHuds(clamp: false);
    }

    private void RefreshDashboardWidgetControls()
    {
        var placement = dashboardWidgets.Get(selectedDashboardWidget);
        dashboardWidgetVisibility.IsChecked = placement.IsVisible;
        dashboardWidgetVisibility.Content = placement.IsVisible
            ? "部件：显示"
            : "部件：隐藏";
        dashboardWidgetFrame.Visibility =
            selectedHud == dashboardHud &&
            dashboardPreviewVisible &&
            placement.IsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void PositionDashboardWidgetFrame()
    {
        var placement = dashboardWidgets.Get(selectedDashboardWidget);
        if (selectedHud != dashboardHud ||
            !dashboardPreviewVisible ||
            !placement.IsVisible)
        {
            dashboardWidgetFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var bounds = DashboardWidgetLayoutSettings.DefaultBounds(
            selectedDashboardWidget);
        var dashboardWidth = HudWidth(dashboardHud);
        var dashboardHeight = HudHeight(dashboardHud);
        dashboardWidgetFrame.Width = Math.Max(16, bounds.Width * dashboardWidth);
        dashboardWidgetFrame.Height = Math.Max(16, bounds.Height * dashboardHeight);
        Canvas.SetLeft(
            dashboardWidgetFrame,
            dashboardHud.Left +
            (bounds.Left + placement.OffsetX) * dashboardWidth);
        Canvas.SetTop(
            dashboardWidgetFrame,
            dashboardHud.Top +
            (bounds.Top + placement.OffsetY) * dashboardHeight);
        dashboardWidgetFrame.Visibility = Visibility.Visible;
    }

    private void BeginDashboardWidgetDrag(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left) return;
        if (eventArgs.ClickCount >= 2)
        {
            var current = dashboardWidgets.Get(selectedDashboardWidget);
            dashboardWidgets = dashboardWidgets.Set(
                selectedDashboardWidget,
                current with { OffsetX = 0, OffsetY = 0 });
            ApplyHuds(clamp: false);
            eventArgs.Handled = true;
            return;
        }

        draggingDashboardWidget = true;
        widgetDragOrigin = eventArgs.GetPosition(canvas);
        widgetDragStart = dashboardWidgets.Get(selectedDashboardWidget);
        dashboardWidgetFrame.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void ContinueDashboardWidgetDrag(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (!draggingDashboardWidget ||
            eventArgs.LeftButton != MouseButtonState.Pressed)
            return;
        var pointer = eventArgs.GetPosition(canvas);
        var dashboardWidth = HudWidth(dashboardHud);
        var dashboardHeight = HudHeight(dashboardHud);
        var bounds = DashboardWidgetLayoutSettings.DefaultBounds(selectedDashboardWidget);
        var proposed = widgetDragStart with
        {
            OffsetX = widgetDragStart.OffsetX +
                      (pointer.X - widgetDragOrigin.X) / dashboardWidth,
            OffsetY = widgetDragStart.OffsetY +
                      (pointer.Y - widgetDragOrigin.Y) / dashboardHeight
        };
        var relativeLeft = (bounds.Left + proposed.OffsetX) * dashboardWidth;
        var relativeTop = (bounds.Top + proposed.OffsetY) * dashboardHeight;
        var snapped = OverlayLayoutSnapping.Snap(
            relativeLeft,
            relativeTop,
            Math.Max(16, bounds.Width * dashboardWidth),
            Math.Max(16, bounds.Height * dashboardHeight),
            dashboardWidth,
            dashboardHeight,
            tolerance: 7,
            margin: 0);
        var next = proposed with
        {
            OffsetX = snapped.Left / dashboardWidth - bounds.Left,
            OffsetY = snapped.Top / dashboardHeight - bounds.Top
        };
        dashboardWidgets = dashboardWidgets.Set(
            selectedDashboardWidget,
            DashboardWidgetLayoutSettings.ClampToCanvas(
                selectedDashboardWidget,
                next));
        PositionDashboardWidgetFrame();
        InvalidateDashboardPreview();
        ShowGuides(snapped with
        {
            VerticalGuide = snapped.VerticalGuide is double x ? dashboardHud.Left + x : null,
            HorizontalGuide = snapped.HorizontalGuide is double y ? dashboardHud.Top + y : null,
            AlignmentText = string.IsNullOrWhiteSpace(snapped.AlignmentText)
                ? $"{DashboardWidgetName(selectedDashboardWidget)} · 双击恢复默认位置"
                : $"{DashboardWidgetName(selectedDashboardWidget)} · {snapped.AlignmentText}"
        });
        eventArgs.Handled = true;
    }

    private void EndDashboardWidgetDrag(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!draggingDashboardWidget) return;
        draggingDashboardWidget = false;
        dashboardWidgetFrame.ReleaseMouseCapture();
        HideGuides();
        eventArgs.Handled = true;
    }

    private void ResetDashboardWidgets()
    {
        dashboardWidgets = DashboardWidgetLayoutSettings.CreateDefault();
        RefreshDashboardWidgetControls();
        PositionDashboardWidgetFrame();
        InvalidateDashboardPreview();
        alignmentText.Text = "仪表盘部件已恢复当前默认布局";
        alignmentText.Foreground = new SolidColorBrush(
            Color.FromRgb(61, 232, 143));
    }

    private void InvalidateDashboardPreview() =>
        (dashboardHud.Frame.Child as HudSurface)?.InvalidateVisual();

    private static string DashboardWidgetName(DashboardWidgetKind kind) =>
        kind switch
        {
            DashboardWidgetKind.RpmArc => "转速灯带",
            DashboardWidgetKind.SpeedGear => "速度 / 挡位",
            DashboardWidgetKind.EngineOutput => "转速 / 动力",
            DashboardWidgetKind.Tires => "轮胎状态",
            DashboardWidgetKind.Pedals => "油门 / 制动",
            DashboardWidgetKind.Steering => "方向指示",
            _ => "等级 / PI"
        };

    private void RefreshEstateRaceWidgetControls()
    {
        var placement = estateRaceWidgets.Get(selectedEstateRaceWidget);
        estateRaceWidgetVisibility.IsChecked = placement.IsVisible;
        estateRaceWidgetVisibility.Content = placement.IsVisible
            ? "部件：显示"
            : "部件：隐藏";
        estateRaceWidgetFrame.Visibility =
            selectedHud == estateRaceHud &&
            estateRacePreviewVisible &&
            placement.IsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void PositionEstateRaceWidgetFrame()
    {
        var placement = estateRaceWidgets.Get(selectedEstateRaceWidget);
        if (selectedHud != estateRaceHud ||
            !estateRacePreviewVisible ||
            !placement.IsVisible)
        {
            estateRaceWidgetFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var estateWidth = HudWidth(estateRaceHud);
        var estateHeight = HudHeight(estateRaceHud);
        var baseSize = EstateRaceWidgetBaseSize(
            selectedEstateRaceWidget,
            estateWidth,
            estateHeight);
        estateRaceWidgetFrame.Width = Math.Max(16, baseSize.Width * placement.Scale);
        estateRaceWidgetFrame.Height = Math.Max(16, baseSize.Height * placement.Scale);
        Canvas.SetLeft(
            estateRaceWidgetFrame,
            estateRaceHud.Left + placement.Left * estateWidth);
        Canvas.SetTop(
            estateRaceWidgetFrame,
            estateRaceHud.Top + placement.Top * estateHeight);
        estateRaceWidgetFrame.Visibility = Visibility.Visible;
    }

    private void BeginEstateRaceWidgetDrag(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left) return;
        SelectHud(estateRaceHud);
        if (eventArgs.ClickCount >= 2)
        {
            estateRaceWidgets = estateRaceWidgets.Set(
                selectedEstateRaceWidget,
                EstateRaceHudLayoutSettings.Default.Get(selectedEstateRaceWidget));
            ApplyHuds(clamp: false);
            eventArgs.Handled = true;
            return;
        }

        draggingEstateRaceWidget = true;
        widgetDragOrigin = eventArgs.GetPosition(canvas);
        estateRaceWidgetDragStart = estateRaceWidgets.Get(selectedEstateRaceWidget);
        estateRaceWidgetFrame.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void SelectEstateRaceWidgetAtPointer(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!estateRacePreviewVisible || eventArgs.ChangedButton != MouseButton.Left)
            return;

        var pointer = eventArgs.GetPosition(canvas);
        var estateWidth = HudWidth(estateRaceHud);
        var estateHeight = HudHeight(estateRaceHud);
        var hits = Enum.GetValues<EstateRaceHudWidgetKind>()
            .Select(kind =>
            {
                var placement = estateRaceWidgets.Get(kind);
                var baseSize = EstateRaceWidgetBaseSize(kind, estateWidth, estateHeight);
                var bounds = new Rect(
                    estateRaceHud.Left + placement.Left * estateWidth,
                    estateRaceHud.Top + placement.Top * estateHeight,
                    Math.Max(16, baseSize.Width * placement.Scale),
                    Math.Max(16, baseSize.Height * placement.Scale));
                return (Kind: kind, Placement: placement, Bounds: bounds);
            })
            .Where(candidate => candidate.Placement.IsVisible && candidate.Bounds.Contains(pointer))
            .OrderBy(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .ToArray();
        if (hits.Length == 0) return;
        var hit = hits[0];

        selectedEstateRaceWidget = hit.Kind;
        estateRaceWidgetSelection.SelectedIndex = (int)hit.Kind;
        SelectHud(estateRaceHud);
        RefreshEstateRaceWidgetControls();
        PositionEstateRaceWidgetFrame();
        BeginEstateRaceWidgetDrag(estateRaceWidgetFrame, eventArgs);
    }

    private void ContinueEstateRaceWidgetDrag(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (!draggingEstateRaceWidget ||
            eventArgs.LeftButton != MouseButtonState.Pressed)
            return;
        var pointer = eventArgs.GetPosition(canvas);
        var estateWidth = HudWidth(estateRaceHud);
        var estateHeight = HudHeight(estateRaceHud);
        var baseSize = EstateRaceWidgetBaseSize(
            selectedEstateRaceWidget,
            estateWidth,
            estateHeight);
        var maxLeft = Math.Max(
            0,
            1 - baseSize.Width * estateRaceWidgetDragStart.Scale / estateWidth);
        var maxTop = Math.Max(
            0,
            1 - baseSize.Height * estateRaceWidgetDragStart.Scale / estateHeight);
        var proposedLeft = Math.Clamp(
            estateRaceWidgetDragStart.Left +
            (pointer.X - widgetDragOrigin.X) / estateWidth,
            0,
            maxLeft);
        var proposedTop = Math.Clamp(
            estateRaceWidgetDragStart.Top +
            (pointer.Y - widgetDragOrigin.Y) / estateHeight,
            0,
            maxTop);
        var width = Math.Max(16, baseSize.Width * estateRaceWidgetDragStart.Scale);
        var height = Math.Max(16, baseSize.Height * estateRaceWidgetDragStart.Scale);
        var snapped = OverlayLayoutSnapping.Snap(
            estateRaceHud.Left + proposedLeft * estateWidth,
            estateRaceHud.Top + proposedTop * estateHeight,
            width,
            height,
            ActualWidth,
            ActualHeight);
        var next = estateRaceWidgetDragStart with
        {
            Left = Math.Clamp((snapped.Left - estateRaceHud.Left) / estateWidth, 0, maxLeft),
            Top = Math.Clamp((snapped.Top - estateRaceHud.Top) / estateHeight, 0, maxTop)
        };
        estateRaceWidgets = estateRaceWidgets.Set(selectedEstateRaceWidget, next);
        PositionEstateRaceWidgetFrame();
        InvalidateEstateRacePreview();
        PositionHandles();
        ShowGuides(snapped with
        {
            AlignmentText = string.IsNullOrWhiteSpace(snapped.AlignmentText)
                ? $"{EstateRaceWidgetName(selectedEstateRaceWidget)} · 双击恢复默认位置与大小"
                : $"{EstateRaceWidgetName(selectedEstateRaceWidget)} · {snapped.AlignmentText}"
        });
        eventArgs.Handled = true;
    }

    private void EndEstateRaceWidgetDrag(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!draggingEstateRaceWidget) return;
        draggingEstateRaceWidget = false;
        estateRaceWidgetFrame.ReleaseMouseCapture();
        HideGuides();
        eventArgs.Handled = true;
    }

    private void ResetEstateRaceWidgets()
    {
        estateRaceWidgets = EstateRaceHudLayoutSettings.Default;
        RefreshEstateRaceWidgetControls();
        PositionEstateRaceWidgetFrame();
        PositionHandles();
        InvalidateEstateRacePreview();
        alignmentText.Text = "地产赛事 HUD 已恢复默认布局";
        alignmentText.Foreground = new SolidColorBrush(Color.FromRgb(180, 99, 255));
    }

    private void InvalidateEstateRacePreview() =>
        (estateRaceHud.Frame.Child as HudSurface)?.InvalidateVisual();

    private static string EstateRaceWidgetName(EstateRaceHudWidgetKind kind) =>
        kind switch
        {
            EstateRaceHudWidgetKind.Leaderboard => "比赛排行榜",
            EstateRaceHudWidgetKind.TrackMap => "赛道一览",
            EstateRaceHudWidgetKind.GripStatus => "抓地提示",
            EstateRaceHudWidgetKind.Banner => "赛事横幅",
            EstateRaceHudWidgetKind.StartLights => "五盏起跑灯",
            EstateRaceHudWidgetKind.PitStopInfo => "维修站信息",
            EstateRaceHudWidgetKind.PitLimiter => "维修区限速",
            _ => "罚时指示器"
        };

    private static Size EstateRaceWidgetBaseSize(
        EstateRaceHudWidgetKind kind,
        double width,
        double height) => kind switch
        {
            EstateRaceHudWidgetKind.Leaderboard => new Size(
                width * 0.235,
                height * 0.058 + Math.Max(36, height * 0.045) * 6),
            EstateRaceHudWidgetKind.TrackMap => new Size(
                Math.Min(width * 0.19, height * 0.28),
                Math.Min(width * 0.19, height * 0.28)),
            EstateRaceHudWidgetKind.GripStatus => new Size(
                width * 0.19,
                height * 0.085),
            EstateRaceHudWidgetKind.Banner => new Size(width * 0.50, height * 0.082),
            EstateRaceHudWidgetKind.StartLights => new Size(width * 0.30, height * 0.09),
            EstateRaceHudWidgetKind.PitStopInfo => new Size(width * 0.255, height * 0.14),
            EstateRaceHudWidgetKind.PitLimiter => new Size(height * 0.11, height * 0.11),
            _ => new Size(width * 0.25, height * 0.09)
        };

    private void BeginHudDrag(EditorHud hud, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left) return;
        SelectHud(hud);
        if (eventArgs.ClickCount >= 2)
        {
            CenterHud(hud);
            eventArgs.Handled = true;
            return;
        }
        if (hud == lapHud) lapAttached = false;
        draggingHud = hud;
        dragOrigin = eventArgs.GetPosition(canvas);
        dragStartLeft = hud.Left;
        dragStartTop = hud.Top;
        hud.Frame.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void ContinueHudDrag(
        EditorHud hud,
        MouseEventArgs eventArgs)
    {
        if (draggingHud != hud ||
            eventArgs.LeftButton != MouseButtonState.Pressed)
            return;
        var pointer = eventArgs.GetPosition(canvas);
        var width = HudWidth(hud);
        var height = HudHeight(hud);
        var left = Math.Clamp(
            dragStartLeft + pointer.X - dragOrigin.X,
            0,
            Math.Max(0, ActualWidth - width));
        var top = Math.Clamp(
            dragStartTop + pointer.Y - dragOrigin.Y,
            0,
            Math.Max(0, ActualHeight - height));
        var snapped = OverlayLayoutSnapping.Snap(
            left,
            top,
            width,
            height,
            ActualWidth,
            ActualHeight);

        if (hud == lapHud)
        {
            var assisted = OverlayHudSnapping.AssistLapNearDashboard(
                snapped.Left,
                snapped.Top,
                width,
                height,
                dashboardHud.Left,
                dashboardHud.Top,
                HudWidth(dashboardHud),
                HudHeight(dashboardHud));
            if (assisted.VerticalGuide is not null ||
                assisted.HorizontalGuide is not null)
                snapped = assisted;
        }

        hud.Left = Math.Clamp(snapped.Left, 0, Math.Max(0, ActualWidth - width));
        hud.Top = Math.Clamp(snapped.Top, 0, Math.Max(0, ActualHeight - height));
        if (hud == dashboardHud && lapAttached) SyncLapToDashboard();
        ShowGuides(snapped);
        ApplyHuds(clamp: false);
        eventArgs.Handled = true;
    }

    private void EndHudDrag(
        EditorHud hud,
        MouseButtonEventArgs eventArgs)
    {
        if (draggingHud != hud) return;
        draggingHud = null;
        hud.Frame.ReleaseMouseCapture();
        HideGuides();
        eventArgs.Handled = true;
    }

    private void BeginResize()
    {
        resizingHud = selectedHud;
        if (resizingHud == lapHud) lapAttached = false;
        if (resizingHud == estateRaceHud)
        {
            resizeStartCenterX = Canvas.GetLeft(estateRaceWidgetFrame) +
                                 estateRaceWidgetFrame.Width / 2;
            resizeStartCenterY = Canvas.GetTop(estateRaceWidgetFrame) +
                                 estateRaceWidgetFrame.Height / 2;
            resizeStartScale = estateRaceWidgets
                .Get(selectedEstateRaceWidget)
                .Scale;
        }
        else
        {
            resizeStartCenterX = resizingHud.Left + HudWidth(resizingHud) / 2;
            resizeStartCenterY = resizingHud.Top + HudHeight(resizingHud) / 2;
            resizeStartScale = resizingHud.Scale;
        }
        resizeAccumulatedX = 0;
        resizeAccumulatedY = 0;
    }

    private void Resize(
        ResizeHandle handle,
        DragDeltaEventArgs eventArgs)
    {
        if (resizingHud is not { } hud) return;
        resizeAccumulatedX += eventArgs.HorizontalChange;
        resizeAccumulatedY += eventArgs.VerticalChange;
        var signedHorizontal = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft =>
                -resizeAccumulatedX,
            ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight =>
                resizeAccumulatedX,
            _ => 0
        };
        var signedVertical = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight =>
                -resizeAccumulatedY,
            ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight =>
                resizeAccumulatedY,
            _ => 0
        };
        var resizeHorizontally = handle is
            ResizeHandle.TopLeft or
            ResizeHandle.Left or
            ResizeHandle.BottomLeft or
            ResizeHandle.TopRight or
            ResizeHandle.Right or
            ResizeHandle.BottomRight;
        var resizeVertically = handle is
            ResizeHandle.TopLeft or
            ResizeHandle.Top or
            ResizeHandle.TopRight or
            ResizeHandle.BottomLeft or
            ResizeHandle.Bottom or
            ResizeHandle.BottomRight;
        if (hud == estateRaceHud)
        {
            ResizeEstateRaceWidget(
                signedHorizontal,
                signedVertical,
                resizeHorizontally,
                resizeVertically);
            return;
        }
        hud.Scale = Math.Min(
            OverlayResizeMath.ScaleFromCenteredDrag(
                resizeStartScale,
                original.Width,
                original.Height,
                signedHorizontal,
                signedVertical,
                resizeHorizontally,
                resizeVertically),
            MaximumScale());
        var width = HudWidth(hud);
        var height = HudHeight(hud);
        hud.Left = resizeStartCenterX - width / 2;
        hud.Top = resizeStartCenterY - height / 2;
        if (hud == dashboardHud && lapAttached) SyncLapToDashboard();
        ApplyHuds(clamp: true);
        ShowGuides(OverlayLayoutSnapping.Snap(
            hud.Left,
            hud.Top,
            width,
            height,
            ActualWidth,
            ActualHeight));
    }

    private void ResizeEstateRaceWidget(
        double signedHorizontal,
        double signedVertical,
        bool resizeHorizontally,
        bool resizeVertically)
    {
        var estateWidth = HudWidth(estateRaceHud);
        var estateHeight = HudHeight(estateRaceHud);
        var baseSize = EstateRaceWidgetBaseSize(
            selectedEstateRaceWidget,
            estateWidth,
            estateHeight);
        double scaleChange;
        if (resizeHorizontally && resizeVertically)
        {
            var denominator = baseSize.Width * baseSize.Width +
                              baseSize.Height * baseSize.Height;
            scaleChange =
                (signedHorizontal * baseSize.Width + signedVertical * baseSize.Height) *
                2 / denominator;
        }
        else if (resizeHorizontally)
        {
            scaleChange = signedHorizontal * 2 / baseSize.Width;
        }
        else if (resizeVertically)
        {
            scaleChange = signedVertical * 2 / baseSize.Height;
        }
        else
        {
            scaleChange = 0;
        }

        var scale = Math.Clamp(resizeStartScale + scaleChange, 0.5, 1.75);
        var width = baseSize.Width * scale;
        var height = baseSize.Height * scale;
        var left = Math.Clamp(
            resizeStartCenterX - width / 2,
            estateRaceHud.Left,
            Math.Max(estateRaceHud.Left, estateRaceHud.Left + estateWidth - width));
        var top = Math.Clamp(
            resizeStartCenterY - height / 2,
            estateRaceHud.Top,
            Math.Max(estateRaceHud.Top, estateRaceHud.Top + estateHeight - height));
        var current = estateRaceWidgets.Get(selectedEstateRaceWidget);
        estateRaceWidgets = estateRaceWidgets.Set(
            selectedEstateRaceWidget,
            current with
            {
                Left = (left - estateRaceHud.Left) / estateWidth,
                Top = (top - estateRaceHud.Top) / estateHeight,
                Scale = scale
            });
        ApplyHuds(clamp: false);
        InvalidateEstateRacePreview();
        ShowGuides(OverlayLayoutSnapping.Snap(
            left,
            top,
            width,
            height,
            ActualWidth,
            ActualHeight));
    }

    private void CenterHud(EditorHud hud)
    {
        if (hud == lapHud) lapAttached = false;
        hud.Left = (ActualWidth - HudWidth(hud)) / 2;
        hud.Top = (ActualHeight - HudHeight(hud)) / 2;
        if (hud == dashboardHud && lapAttached) SyncLapToDashboard();
        ApplyHuds(clamp: true);
        ShowGuides(new OverlaySnapResult(
            hud.Left,
            hud.Top,
            ActualWidth / 2,
            ActualHeight / 2,
            "垂直居中 · 水平居中"));
    }

    private void AttachLapToDashboard()
    {
        lapAttached = true;
        SyncLapToDashboard();
        SelectHud(lapHud);
        ApplyHuds(clamp: true);
        ShowGuides(new OverlaySnapResult(
            lapHud.Left,
            lapHud.Top,
            dashboardHud.Left + HudWidth(dashboardHud) / 2,
            dashboardHud.Top + HudHeight(dashboardHud) / 2,
            "圈速 HUD 已吸附仪表盘并同步缩放"));
    }

    private void SyncLapToDashboard()
    {
        lapHud.Left = dashboardHud.Left;
        lapHud.Top = dashboardHud.Top;
        lapHud.Scale = dashboardHud.Scale;
    }

    private double SelectedScale() => selectedHud == estateRaceHud
        ? estateRaceWidgets.Get(selectedEstateRaceWidget).Scale
        : selectedHud.Scale;

    private double SelectedWidth() => selectedHud == estateRaceHud
        ? estateRaceWidgetFrame.Width
        : HudWidth(selectedHud);

    private double SelectedHeight() => selectedHud == estateRaceHud
        ? estateRaceWidgetFrame.Height
        : HudHeight(selectedHud);

    private double HudWidth(EditorHud hud) => hud == estateRaceHud
        ? estateRaceViewportWidth
        : OverlayScaleSettings.ScaledDimension(original.Width, hud.Scale);

    private double HudHeight(EditorHud hud) => hud == estateRaceHud
        ? estateRaceViewportHeight
        : OverlayScaleSettings.ScaledDimension(original.Height, hud.Scale);

    private double MaximumScale() => Math.Min(
        OverlayScaleSettings.Maximum,
        Math.Min(
            Math.Max(
                OverlayScaleSettings.Minimum,
                (ActualWidth - 48) / original.Width),
            Math.Max(
                OverlayScaleSettings.Minimum,
                (ActualHeight - 120) / original.Height)));

    private void ShowGuides(OverlaySnapResult result)
    {
        verticalGuide.Visibility = result.VerticalGuide is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (result.VerticalGuide is double x)
        {
            verticalGuide.X1 = x;
            verticalGuide.X2 = x;
            verticalGuide.Y1 = 0;
            verticalGuide.Y2 = ActualHeight;
        }
        horizontalGuide.Visibility = result.HorizontalGuide is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (result.HorizontalGuide is double y)
        {
            horizontalGuide.X1 = 0;
            horizontalGuide.X2 = ActualWidth;
            horizontalGuide.Y1 = y;
            horizontalGuide.Y2 = y;
        }
        alignmentText.Text = string.IsNullOrWhiteSpace(result.AlignmentText)
            ? DefaultAlignmentText
            : result.AlignmentText;
        alignmentText.Foreground = string.IsNullOrWhiteSpace(result.AlignmentText)
            ? new SolidColorBrush(Color.FromRgb(179, 192, 201))
            : new SolidColorBrush(Color.FromRgb(73, 211, 235));
    }

    private void HideGuides()
    {
        verticalGuide.Visibility = Visibility.Collapsed;
        horizontalGuide.Visibility = Visibility.Collapsed;
        alignmentText.Text = DefaultAlignmentText;
        alignmentText.Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201));
    }

    private void Save()
    {
        Result = CurrentLayout();
        DialogResult = true;
    }

    private void Cancel()
    {
        Result = null;
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Cancel();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.S &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Save();
            eventArgs.Handled = true;
        }
    }

    private static Line GuideLine() => new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(73, 211, 235)),
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection([5, 4]),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

    private static Thumb CreateResizeThumb(ResizeHandle handle)
    {
        var cursor = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            _ => Cursors.SizeWE
        };
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetValue(
            Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(61, 232, 143)));
        visual.SetValue(
            Border.BorderBrushProperty,
            new SolidColorBrush(Color.FromRgb(8, 33, 24)));
        visual.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        visual.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        return new Thumb
        {
            Width = 12,
            Height = 12,
            Cursor = cursor,
            Template = new ControlTemplate(typeof(Thumb))
            {
                VisualTree = visual
            }
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed class EditorHud(
        EditorHudKind kind,
        Border frame)
    {
        public EditorHudKind Kind { get; } = kind;
        public Border Frame { get; } = frame;
        public double Left { get; set; }
        public double Top { get; set; }
        public double Scale { get; set; } = OverlayScaleSettings.Default;
    }

    private enum EditorHudKind
    {
        Dashboard,
        Lap,
        Drift,
        EstateRace
    }

    private enum ResizeHandle
    {
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }
}
