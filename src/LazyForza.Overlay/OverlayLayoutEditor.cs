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
}

internal sealed class OverlayLayoutEditorWindow : Window
{
    private const int SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private readonly ForzaHorizonWindowInfo target;
    private readonly OverlayLayout original;
    private readonly Canvas canvas;
    private readonly Border frame;
    private readonly Line verticalGuide;
    private readonly Line horizontalGuide;
    private readonly TextBlock alignmentText;
    private readonly TextBlock metricsText;
    private readonly List<(Thumb Thumb, ResizeHandle Handle)> handles = [];
    private double frameLeft;
    private double frameTop;
    private double scale;
    private bool draggingFrame;
    private Point dragOrigin;
    private double dragStartLeft;
    private double dragStartTop;
    private double resizeStartLeft;
    private double resizeStartTop;
    private double resizeStartWidth;
    private double resizeStartHeight;
    private double resizeStartScale;
    private double resizeAccumulatedX;
    private double resizeAccumulatedY;

    public OverlayLayoutEditorWindow(
        Func<IReadOnlyList<IHudContribution>> getContributions,
        OverlayLayout layout,
        ForzaHorizonWindowInfo target)
    {
        this.target = target;
        original = layout with { ClickThrough = true, IsLocked = true };
        scale = OverlayScaleSettings.Normalize(layout.Scale);
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
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Foreground = Brushes.White;

        var root = new Grid();
        canvas = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent
        };
        root.Children.Add(canvas);

        frame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 232, 143)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = new HudSurface(
                getContributions,
                () => CurrentLayout(),
                layoutPreview: true)
        };
        frame.MouseLeftButtonDown += BeginFrameDrag;
        frame.MouseMove += ContinueFrameDrag;
        frame.MouseLeftButtonUp += EndFrameDrag;
        canvas.Children.Add(frame);

        verticalGuide = GuideLine();
        horizontalGuide = GuideLine();
        canvas.Children.Add(verticalGuide);
        canvas.Children.Add(horizontalGuide);

        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var thumb = CreateResizeThumb(handle);
            thumb.DragStarted += (_, _) => BeginResize();
            thumb.DragDelta += (_, eventArgs) => Resize(handle, eventArgs);
            thumb.DragCompleted += (_, _) => HideGuides();
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
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
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
            Text = "已固定显示全部 HUD 元素。拖动绿色区域移动，拖动控制点等比缩放。Esc 取消，Ctrl+S 保存。",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 192, 201))
        });
        headerGrid.Children.Add(heading);
        metricsText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(61, 232, 143)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(metricsText, 2);
        headerGrid.Children.Add(metricsText);
        header.Child = headerGrid;
        root.Children.Add(header);

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
            Text = "拖动时会自动吸附到窗口边缘与中心线",
            Width = 260,
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
        Loaded += (_, _) => InitializeFrame();
        SizeChanged += (_, _) =>
        {
            if (IsLoaded) ApplyFrame(clamp: true);
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public OverlayLayout? Result { get; private set; }

    private void InitializeFrame()
    {
        var maximumScale = Math.Min(
            Math.Max(OverlayScaleSettings.Minimum, (ActualWidth - 48) / original.Width),
            Math.Max(OverlayScaleSettings.Minimum, (ActualHeight - 120) / original.Height));
        scale = OverlayScaleSettings.Normalize(Math.Min(scale, maximumScale));
        var width = OverlayScaleSettings.ScaledDimension(original.Width, scale);
        var height = OverlayScaleSettings.ScaledDimension(original.Height, scale);
        frameLeft = Math.Clamp(
            original.Left - Left,
            12,
            Math.Max(12, ActualWidth - width - 12));
        frameTop = Math.Clamp(
            original.Top - Top,
            72,
            Math.Max(72, ActualHeight - height - 72));
        ApplyFrame(clamp: true);
    }

    private OverlayLayout CurrentLayout() => original with
    {
        Left = Left + frameLeft,
        Top = Top + frameTop,
        Scale = scale,
        MonitorId = target.MonitorId,
        ClickThrough = true,
        IsLocked = true
    };

    private void ApplyFrame(bool clamp)
    {
        var width = OverlayScaleSettings.ScaledDimension(original.Width, scale);
        var height = OverlayScaleSettings.ScaledDimension(original.Height, scale);
        if (clamp)
        {
            frameLeft = Math.Clamp(frameLeft, 0, Math.Max(0, ActualWidth - width));
            frameTop = Math.Clamp(frameTop, 0, Math.Max(0, ActualHeight - height));
        }
        frame.Width = width;
        frame.Height = height;
        Canvas.SetLeft(frame, frameLeft);
        Canvas.SetTop(frame, frameTop);
        PositionHandles(width, height);
        metricsText.Text = $"{scale:P1} · {width:0} × {height:0}";
    }

    private void PositionHandles(double width, double height)
    {
        foreach (var (thumb, handle) in handles)
        {
            var point = handle switch
            {
                ResizeHandle.TopLeft => new Point(frameLeft, frameTop),
                ResizeHandle.Top => new Point(frameLeft + width / 2, frameTop),
                ResizeHandle.TopRight => new Point(frameLeft + width, frameTop),
                ResizeHandle.Right => new Point(frameLeft + width, frameTop + height / 2),
                ResizeHandle.BottomRight => new Point(frameLeft + width, frameTop + height),
                ResizeHandle.Bottom => new Point(frameLeft + width / 2, frameTop + height),
                ResizeHandle.BottomLeft => new Point(frameLeft, frameTop + height),
                _ => new Point(frameLeft, frameTop + height / 2)
            };
            Canvas.SetLeft(thumb, point.X - thumb.Width / 2);
            Canvas.SetTop(thumb, point.Y - thumb.Height / 2);
        }
    }

    private void BeginFrameDrag(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left) return;
        if (eventArgs.ClickCount >= 2)
        {
            CenterFrame();
            eventArgs.Handled = true;
            return;
        }
        draggingFrame = true;
        dragOrigin = eventArgs.GetPosition(canvas);
        dragStartLeft = frameLeft;
        dragStartTop = frameTop;
        frame.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void ContinueFrameDrag(object sender, MouseEventArgs eventArgs)
    {
        if (!draggingFrame || eventArgs.LeftButton != MouseButtonState.Pressed) return;
        var pointer = eventArgs.GetPosition(canvas);
        var width = frame.Width;
        var height = frame.Height;
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
        frameLeft = Math.Clamp(snapped.Left, 0, Math.Max(0, ActualWidth - width));
        frameTop = Math.Clamp(snapped.Top, 0, Math.Max(0, ActualHeight - height));
        ShowGuides(snapped);
        ApplyFrame(clamp: false);
        eventArgs.Handled = true;
    }

    private void EndFrameDrag(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!draggingFrame) return;
        draggingFrame = false;
        frame.ReleaseMouseCapture();
        HideGuides();
        eventArgs.Handled = true;
    }

    private void BeginResize()
    {
        resizeStartLeft = frameLeft;
        resizeStartTop = frameTop;
        resizeStartWidth = frame.Width;
        resizeStartHeight = frame.Height;
        resizeStartScale = scale;
        resizeAccumulatedX = 0;
        resizeAccumulatedY = 0;
    }

    private void Resize(
        ResizeHandle handle,
        DragDeltaEventArgs eventArgs)
    {
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
        scale = OverlayResizeMath.ScaleFromDrag(
            resizeStartScale,
            original.Width,
            original.Height,
            signedHorizontal,
            signedVertical,
            resizeHorizontally,
            resizeVertically);
        var width = OverlayScaleSettings.ScaledDimension(original.Width, scale);
        var height = OverlayScaleSettings.ScaledDimension(original.Height, scale);
        frameLeft = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft
            ? resizeStartLeft + resizeStartWidth - width
            : resizeStartLeft;
        frameTop = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight
            ? resizeStartTop + resizeStartHeight - height
            : resizeStartTop;
        ApplyFrame(clamp: true);
        var snapped = OverlayLayoutSnapping.Snap(
            frameLeft,
            frameTop,
            width,
            height,
            ActualWidth,
            ActualHeight);
        ShowGuides(snapped);
    }

    private void CenterFrame()
    {
        frameLeft = (ActualWidth - frame.Width) / 2;
        frameTop = (ActualHeight - frame.Height) / 2;
        ApplyFrame(clamp: true);
        ShowGuides(new OverlaySnapResult(
            frameLeft,
            frameTop,
            ActualWidth / 2,
            ActualHeight / 2,
            "垂直居中 · 水平居中"));
    }

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
            ? "拖动时会自动吸附到窗口边缘与中心线"
            : result.AlignmentText;
        alignmentText.Foreground = string.IsNullOrWhiteSpace(result.AlignmentText)
            ? new SolidColorBrush(Color.FromRgb(179, 192, 201))
            : new SolidColorBrush(Color.FromRgb(73, 211, 235));
    }

    private void HideGuides()
    {
        verticalGuide.Visibility = Visibility.Collapsed;
        horizontalGuide.Visibility = Visibility.Collapsed;
        alignmentText.Text = "拖动时会自动吸附到窗口边缘与中心线";
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
        visual.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(61, 232, 143)));
        visual.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(8, 33, 24)));
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
