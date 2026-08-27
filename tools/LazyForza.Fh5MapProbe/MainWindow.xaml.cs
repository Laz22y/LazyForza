using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LazyForza.Fh5MapProbe;

public partial class MainWindow : Window
{
    private static readonly Fh5MapOption[] MapOptions =
    [
        new(Fh5MapRegion.Mexico, "主地图 · 墨西哥", "Mexico"),
        new(Fh5MapRegion.HotWheelsPark, "风火轮地图 · Hot Wheels Park", "Hot-Wheels"),
        new(Fh5MapRegion.SierraNueva, "拉力 DLC · Sierra Nueva", "Sierra-Nueva")
    ];
    private readonly ObservableCollection<Fh5CoordinateMarker> markers = [];
    private readonly DispatcherTimer uiTimer;
    private Fh5MapCaptureSession? session;
    private DateTimeOffset rateSampleAt = DateTimeOffset.UtcNow;
    private long rateSamplePackets;
    private bool outputPathCustomized;
    private bool closeAfterStop;
    private string? lastCompletedOutput;

    public MainWindow()
    {
        InitializeComponent();
        MapBox.ItemsSource = MapOptions;
        MapBox.SelectedIndex = 0;
        MarkerGrid.ItemsSource = markers;
        TargetAddressText.Text = TargetAddressGuidance();
        SetAutomaticOutputPath();
        uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, UpdateLiveUi, Dispatcher);
        uiTimer.Start();
        AppendLog("工具已就绪。先选择地图和输出文件，再开始采集。");
    }

    internal async Task CaptureQaAsync(string path)
    {
        await Task.Delay(350);
        UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private void MapBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!outputPathCustomized) SetAutomaticOutputPath();
    }

    private void SessionLabelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!outputPathCustomized) SetAutomaticOutputPath();
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择 FH5 地图坐标采集文件",
            Filter = "FH5 地图采集包 (*.fh5mapcapture)|*.fh5mapcapture",
            DefaultExt = CapturePackageWriter.Extension,
            AddExtension = true,
            OverwritePrompt = false,
            FileName = Path.GetFileName(OutputPathBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text)
        };
        if (dialog.ShowDialog(this) != true) return;
        OutputPathBox.Text = dialog.FileName;
        outputPathCustomized = true;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (session is not null) return;
        try
        {
            if (MapBox.SelectedItem is not Fh5MapOption map)
                throw new InvalidOperationException("请选择地图。");
            if (string.IsNullOrWhiteSpace(SessionLabelBox.Text))
                throw new InvalidOperationException("请输入采集批次。");
            if (!IPAddress.TryParse(ListenAddressBox.Text.Trim(), out _))
                throw new InvalidOperationException("监听地址不是有效 IP。");
            if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65_535)
                throw new InvalidOperationException("端口必须是 1–65535 的整数。");
            if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
                throw new InvalidOperationException("请选择输出文件。");

            var settings = new Fh5CaptureSettings(
                map.Value,
                map.DisplayName,
                SessionLabelBox.Text.Trim(),
                ListenAddressBox.Text.Trim(),
                port,
                Path.GetFullPath(OutputPathBox.Text),
                DateTimeOffset.UtcNow);
            session = new Fh5MapCaptureSession(settings);
            markers.Clear();
            rateSampleAt = DateTimeOffset.UtcNow;
            rateSamplePackets = 0;
            SetRunningControls(true);
            SetStatus("正在监听", Brushes.DarkGreen);
            LatestStateText.Text = "等待 FH5 Data Out 数据…";
            AppendLog($"开始采集：{map.DisplayName} · {settings.SessionLabel}");
            AppendLog($"原始数据恢复目录：{session.RecoveryDirectory}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or
                                           UnauthorizedAccessException or SocketException)
        {
            MessageBox.Show(this, exception.Message, "无法开始采集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await StopAndSaveAsync();

    private void CaptureMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (session is null)
        {
            MessageBox.Show(this, "请先开始采集。", "尚未采集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var marker = session.CaptureMarker(MarkerNameBox.Text);
            markers.Add(marker);
            AppendLog($"已记录地标“{marker.Name}”：({marker.X:F3}, {marker.Y:F3}, {marker.Z:F3})，离散 {marker.SpreadMeters:F3} m");
            MarkerNameBox.SelectAll();
            MarkerNameBox.Focus();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "无法记录地标", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void DeleteMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (MarkerGrid.SelectedItem is not Fh5CoordinateMarker marker) return;
        session?.RemoveMarker(marker.Id);
        markers.Remove(marker);
        AppendLog($"已删除地标“{marker.Name}”。");
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = lastCompletedOutput ?? OutputPathBox.Text;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is null) return;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (session is null || closeAfterStop) return;
        e.Cancel = true;
        if (MessageBox.Show(
                this,
                "采集仍在进行。是否停止、保存采集包并退出？",
                "正在采集",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!await StopAndSaveAsync()) return;
        closeAfterStop = true;
        Close();
    }

    private async Task<bool> StopAndSaveAsync()
    {
        if (session is null) return true;
        StopButton.IsEnabled = false;
        SetStatus("正在保存", Brushes.DarkOrange);
        var current = session;
        try
        {
            await current.StopAndSaveAsync(NotesBox.Text);
            var snapshot = current.Snapshot();
            lastCompletedOutput = snapshot.OutputPath;
            AppendLog($"采集包已保存：{snapshot.OutputPath}");
            AppendLog($"有效包 {snapshot.ValidPackets:N0}，无效包 {snapshot.InvalidPackets:N0}，地标 {snapshot.Markers.Count}。 ");
            SetStatus("已保存", Brushes.DarkGreen);
            await current.DisposeAsync();
            session = null;
            SetRunningControls(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppendLog($"保存失败：{exception.Message}");
            AppendLog($"未打包数据仍保留在：{current.RecoveryDirectory}");
            SetStatus("保存失败", Brushes.DarkRed);
            MessageBox.Show(
                this,
                $"无法完成采集包。未打包数据仍保留在下面的恢复目录：\n\n{current.RecoveryDirectory}\n\n{exception.Message}",
                "保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await current.DisposeAsync();
            session = null;
            SetRunningControls(false);
            return false;
        }
    }

    private void UpdateLiveUi(object? sender, EventArgs e)
    {
        if (session is null) return;
        var snapshot = session.Snapshot();
        var now = DateTimeOffset.UtcNow;
        var elapsed = Math.Max((now - rateSampleAt).TotalSeconds, 0.001);
        var packetsPerSecond = (snapshot.TotalPackets - rateSamplePackets) / elapsed;
        rateSampleAt = now;
        rateSamplePackets = snapshot.TotalPackets;
        PacketRateText.Text = $"{packetsPerSecond:F1}/s · {snapshot.ValidPackets:N0} · {snapshot.InvalidPackets:N0}";
        var lengths = snapshot.PacketLengths.Count == 0
            ? "—"
            : string.Join(" / ", snapshot.PacketLengths.OrderBy(item => item.Key).Select(item => $"{item.Key}B:{item.Value:N0}"));
        if (snapshot.LatestFrame is Fh5DataOutFrame frame)
        {
            PositionText.Text = $"{frame.PositionX:F3} / {frame.PositionY:F3} / {frame.PositionZ:F3}";
            SpeedText.Text = $"{frame.SpeedMps * 3.6:F1} km/h · Δ {frame.SpeedDeltaMps:F3} m/s";
            PacketDetailText.Text = $"{lengths} · {frame.IsRaceOn} · {frame.TimestampMs}";
            LatestStateText.Text = frame.IsRaceOn == 1
                ? $"驾驶数据有效 · 车辆 {frame.CarOrdinal} · PI {frame.PerformanceIndex} · 最大物理误差 {snapshot.MaximumSpeedDeltaMps:F3} m/s"
                : "当前包表示菜单、暂停或非驾驶状态；继续等待驾驶数据。";
        }
        else
        {
            PacketDetailText.Text = lengths;
            LatestStateText.Text = snapshot.LastError ?? "等待 FH5 Data Out 数据…";
        }
        BoundsText.Text = snapshot.ActiveCoordinateBounds is Fh5CoordinateBounds bounds
            ? $"X {bounds.MinimumX:F1} … {bounds.MaximumX:F1}    Y {bounds.MinimumY:F1} … {bounds.MaximumY:F1}    Z {bounds.MinimumZ:F1} … {bounds.MaximumZ:F1}"
            : "—";
        if (snapshot.TotalPackets > 0 && snapshot.ValidPackets == 0)
            SetStatus("收到包但无法解析", Brushes.DarkRed);
        else if (snapshot.ValidPackets > 0)
            SetStatus("正在采集", Brushes.DarkGreen);
    }

    private void SetRunningControls(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        MapBox.IsEnabled = !running;
        SessionLabelBox.IsEnabled = !running;
        ListenAddressBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        BrowseOutputButton.IsEnabled = !running;
    }

    private void SetAutomaticOutputPath()
    {
        if (MapBox.SelectedItem is not Fh5MapOption map || OutputPathBox is null) return;
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "LazyForza FH5 Map Probe");
        var sessionLabel = SafeFileName(SessionLabelBox?.Text ?? "第1轮");
        OutputPathBox.Text = Path.Combine(
            directory,
            $"FH5-{map.FileName}-{sessionLabel}-{DateTime.Now:yyyyMMdd-HHmmss}{CapturePackageWriter.Extension}");
    }

    private static string TargetAddressGuidance()
    {
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()
            .Select(address => address.ToString())
            .ToArray();
        var targets = localAddresses.Length == 0
            ? "127.0.0.1"
            : $"127.0.0.1；若收不到则试 {string.Join(" / ", localAddresses)}";
        return $"FH5 Data Out 目标 IP：{targets}，端口与上方一致。";
    }

    private static string SafeFileName(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "round" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '-');
        return result.Replace(' ', '-');
    }

    private void SetStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusText.Foreground = color;
    }

    private void AppendLog(string message)
    {
        LogText.Text = $"最近操作 [{DateTime.Now:HH:mm:ss}]：{message}";
    }
}
