using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Telemetry;
using Microsoft.Win32;

namespace LazyForza.EstateProbe;

public partial class MainWindow : Window
{
    private const int MaximumTraceSamples = 216_000;
    private readonly object stateGate = new();
    private readonly ObservableCollection<ProbeMarkerDocument> markers = [];
    private readonly Queue<TelemetryFrame> recentFrames = new();
    private readonly List<ProbeTraceSample> trace = [];
    private readonly DispatcherTimer uiTimer;
    private UdpTelemetrySource? source;
    private CancellationTokenSource? receiveCancellation;
    private Task? receiveTask;
    private TelemetryFrame? latestFrame;
    private TelemetryFrame? previousFrame;
    private DateTimeOffset? nextTraceAt;
    private DateTimeOffset? firstPacketAt;
    private DateTimeOffset? lastPacketAt;
    private DateTimeOffset sessionStartedAt = DateTimeOffset.UtcNow;
    private Guid sessionId = Guid.NewGuid();
    private string? lastReceiveError;
    private long validPackets;
    private long invalidPackets;
    private long drivingPackets;
    private long currentLapPositivePackets;
    private long currentRacePositivePackets;
    private long racePositionPositivePackets;
    private int coordinateJumpCount;
    private int timestampBackwardCount;
    private long previousRatePacketCount;
    private DateTimeOffset previousRateAt = DateTimeOffset.UtcNow;
    private double packetsPerSecond;
    private bool traceLimitReached;

    public MainWindow()
    {
        InitializeComponent();
        MarkerGrid.ItemsSource = markers;
        uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => RefreshLiveDisplay(), Dispatcher);
        uiTimer.Start();
        Closing += (_, _) => ShutdownReceiver();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IPAddress.TryParse(AddressBox.Text.Trim(), out _) ||
            !int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            MessageBox.Show(this, "请输入有效的 IP 地址和 UDP 端口。", "无法开始", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await StopReceiverAsync();
        ResetSessionState();
        var address = AddressBox.Text.Trim();
        source = new UdpTelemetrySource(new TelemetryOptions(address, port));
        receiveCancellation = new CancellationTokenSource();
        receiveTask = RunReceiverAsync(source, receiveCancellation.Token);

        AddressBox.IsEnabled = false;
        PortBox.IsEnabled = false;
        SessionNameBox.IsEnabled = false;
        SessionRoleBox.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SetStatus("正在等待 FH6 UDP…", "#E5B94B");
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopReceiverAsync();

    private async Task RunReceiverAsync(UdpTelemetrySource telemetrySource, CancellationToken cancellationToken)
    {
        try
        {
            await telemetrySource.RunAsync(
                frame =>
                {
                    ObserveFrame(frame);
                    return ValueTask.CompletedTask;
                },
                error =>
                {
                    Interlocked.Increment(ref invalidPackets);
                    lock (stateGate) lastReceiveError = error;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (stateGate) lastReceiveError = exception.Message;
            await Dispatcher.InvokeAsync(() =>
            {
                SetStatus("监听失败", "#FF6B78");
                MessageBox.Show(this, $"UDP 监听失败：{exception.Message}\n\n请确认端口没有被 LazyForza 主程序或其他工具占用。",
                    "监听失败", MessageBoxButton.OK, MessageBoxImage.Error);
                RestoreControlsAfterStop();
            });
        }
    }

    private void ObserveFrame(TelemetryFrame frame)
    {
        Interlocked.Increment(ref validPackets);
        if (frame.Raw.IsRaceOn == 1) Interlocked.Increment(ref drivingPackets);
        if (frame.Raw.CurrentLap > 0.05f) Interlocked.Increment(ref currentLapPositivePackets);
        if (frame.Raw.CurrentRaceTime > 0.05f) Interlocked.Increment(ref currentRacePositivePackets);
        if (frame.Raw.RacePosition > 0) Interlocked.Increment(ref racePositionPositivePackets);

        lock (stateGate)
        {
            latestFrame = frame;
            firstPacketAt ??= frame.ArrivalTime;
            lastPacketAt = frame.ArrivalTime;
            recentFrames.Enqueue(frame);
            while (recentFrames.Count > 0 && frame.ArrivalTime - recentFrames.Peek().ArrivalTime > TimeSpan.FromSeconds(1.2))
                recentFrames.Dequeue();

            if (previousFrame is { } previous)
            {
                var elapsed = (frame.ArrivalTime - previous.ArrivalTime).TotalSeconds;
                if (elapsed is > 0 and <= 0.5)
                {
                    var distance = Distance(previous.Raw.Position, frame.Raw.Position);
                    var plausibleTravel = Math.Max(25, Math.Max(previous.Raw.Speed, frame.Raw.Speed) * elapsed * 4 + 10);
                    if (distance > plausibleTravel) coordinateJumpCount++;
                }

                if (frame.Raw.TimestampMS < previous.Raw.TimestampMS &&
                    previous.Raw.TimestampMS - frame.Raw.TimestampMS < uint.MaxValue / 2)
                    timestampBackwardCount++;
            }

            previousFrame = frame;
            if ((nextTraceAt is null || frame.ArrivalTime >= nextTraceAt) && trace.Count < MaximumTraceSamples)
            {
                trace.Add(ToTraceSample(frame));
                nextTraceAt = frame.ArrivalTime + TimeSpan.FromMilliseconds(100);
            }
            else if (trace.Count >= MaximumTraceSamples)
            {
                traceLimitReached = true;
            }
        }
    }

    private void CaptureMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        var name = MarkerNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请先填写标记名称。两台电脑必须使用相同名称。", "无法采样",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TelemetryFrame[] frames;
        lock (stateGate)
        {
            if (latestFrame is null)
            {
                MessageBox.Show(this, "尚未收到 FH6 UDP 数据。", "无法采样", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var threshold = latestFrame.ArrivalTime - TimeSpan.FromSeconds(1);
            frames = recentFrames.Where(frame => frame.ArrivalTime >= threshold).ToArray();
        }

        if (frames.Length < 5)
        {
            MessageBox.Show(this, "最近一秒的有效包不足，请等待数据稳定后重试。", "无法采样",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var x = frames.Average(frame => frame.Raw.Position.X);
        var y = frames.Average(frame => frame.Raw.Position.Y);
        var z = frames.Average(frame => frame.Raw.Position.Z);
        var yaw = Math.Atan2(frames.Average(frame => Math.Sin(frame.Raw.Yaw)),
            frames.Average(frame => Math.Cos(frame.Raw.Yaw)));
        var spread = Math.Sqrt(frames.Average(frame =>
        {
            var dx = frame.Raw.Position.X - x;
            var dy = frame.Raw.Position.Y - y;
            var dz = frame.Raw.Position.Z - z;
            return dx * dx + dy * dy + dz * dz;
        }));
        var marker = new ProbeMarkerDocument(
            name,
            frames[^1].ArrivalTime,
            x,
            y,
            z,
            yaw,
            spread,
            frames.Length,
            frames.Average(frame => frame.Raw.Speed));

        var existing = markers.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) markers.Remove(existing);
        markers.Add(marker);
        MarkerNameBox.SelectAll();
        SetStatus(spread <= 0.5 ? $"已采样：{name}" : $"已采样，但车辆离散度 {spread:F2} m",
            spread <= 0.5 ? "#34D1C6" : "#E5B94B");
    }

    private void DeleteMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (MarkerGrid.SelectedItem is ProbeMarkerDocument marker) markers.Remove(marker);
    }

    private async void SaveSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "LazyForza 地产验证会话 (*.lfzestateprobe.json)|*.lfzestateprobe.json|JSON 文件 (*.json)|*.json",
            DefaultExt = ProbeSessionFile.Extension,
            AddExtension = true,
            FileName = $"{SafeFileName(SessionNameBox.Text)}-{DateTime.Now:yyyyMMdd-HHmmss}{ProbeSessionFile.Extension}"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var session = CreateSessionDocument();
            await ProbeSessionFile.SaveAsync(dialog.FileName, session);
            SetStatus("会话已保存", "#34D1C6");
            MessageBox.Show(this,
                $"已保存：\n{dialog.FileName}\n\n标记点 {session.Markers.Count} 个，10 Hz 轨迹样本 {session.Trace.Count:N0} 个。",
                "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"保存失败：{exception.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseReferenceButton_Click(object sender, RoutedEventArgs e) => BrowseSessionInto(ReferencePathBox);
    private void BrowseCandidateButton_Click(object sender, RoutedEventArgs e) => BrowseSessionInto(CandidatePathBox);

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var referencePath = ReferencePathBox.Text.Trim();
            var candidatePath = CandidatePathBox.Text.Trim();
            if (!File.Exists(referencePath) || !File.Exists(candidatePath))
                throw new FileNotFoundException("请选择两份存在的地产验证会话文件。");

            var reference = await ProbeSessionFile.LoadAsync(referencePath);
            var candidate = await ProbeSessionFile.LoadAsync(candidatePath);
            var comparison = EstateCoordinateAnalyzer.Compare(
                reference.Markers.Select(ToAnalysisMarker).ToArray(),
                candidate.Markers.Select(ToAnalysisMarker).ToArray());
            ComparisonResultBox.Text = FormatComparison(reference, candidate, comparison);
            SetStatus("坐标比较完成", comparison.Compatibility is EstateCoordinateCompatibility.DirectMatch or
                EstateCoordinateCompatibility.RigidTransform ? "#34D1C6" : "#E5B94B");
        }
        catch (Exception exception)
        {
            ComparisonResultBox.Text = $"比较失败：{exception.Message}";
            SetStatus("比较失败", "#FF6B78");
        }
    }

    private void RefreshLiveDisplay()
    {
        TelemetryFrame? frame;
        string? receiveError;
        int jumps;
        int backwards;
        int traceCount;
        bool limitReached;
        lock (stateGate)
        {
            frame = latestFrame;
            receiveError = lastReceiveError;
            jumps = coordinateJumpCount;
            backwards = timestampBackwardCount;
            traceCount = trace.Count;
            limitReached = traceLimitReached;
        }

        var now = DateTimeOffset.UtcNow;
        var rateElapsed = (now - previousRateAt).TotalSeconds;
        if (rateElapsed >= 0.75)
        {
            var count = Interlocked.Read(ref validPackets);
            packetsPerSecond = (count - previousRatePacketCount) / rateElapsed;
            previousRatePacketCount = count;
            previousRateAt = now;
        }

        PacketText.Text = $"{packetsPerSecond:F1} Hz / {Interlocked.Read(ref validPackets):N0}";
        if (frame is null)
        {
            PositionText.Text = MotionText.Text = RaceStateText.Text = TimerText.Text = "—";
        }
        else
        {
            PositionText.Text = $"{frame.Raw.Position.X:F2} / {frame.Raw.Position.Y:F2} / {frame.Raw.Position.Z:F2}";
            MotionText.Text = $"{frame.Normalized.SpeedKph:F1} km/h / {frame.Raw.Yaw * 180 / Math.PI:F1}°";
            RaceStateText.Text = $"{frame.Raw.IsRaceOn} / {frame.Raw.RacePosition}";
            TimerText.Text = $"{frame.Raw.CurrentLap:F2} / {frame.Raw.CurrentRaceTime:F2} / {frame.Raw.LapNumber}";
            if (receiveTask is { IsCompleted: false } && now - frame.ArrivalTime < TimeSpan.FromSeconds(2))
                SetStatus("正在采集", "#34D1C6");
            else if (receiveTask is { IsCompleted: false })
                SetStatus("数据中断", "#E5B94B");
        }

        SessionSummaryText.Text =
            $"有效/无效包：{Interlocked.Read(ref validPackets):N0} / {Interlocked.Read(ref invalidPackets):N0}\n" +
            $"轨迹样本：{traceCount:N0}{(limitReached ? "（已达 6 小时上限）" : string.Empty)}\n" +
            $"坐标突跳：{jumps}　时间戳倒退：{backwards}\n" +
            $"比赛字段出现比例：CurrentLap {Ratio(currentLapPositivePackets, validPackets)}，" +
            $"RaceTime {Ratio(currentRacePositivePackets, validPackets)}，RacePosition {Ratio(racePositionPositivePackets, validPackets)}" +
            (string.IsNullOrWhiteSpace(receiveError) ? string.Empty : $"\n最近错误：{receiveError}");
    }

    private ProbeSessionDocument CreateSessionDocument()
    {
        ProbeTraceSample[] traceSnapshot;
        TelemetryFrame? frame;
        DateTimeOffset? first;
        DateTimeOffset? last;
        int jumps;
        int backwards;
        lock (stateGate)
        {
            traceSnapshot = trace.ToArray();
            frame = latestFrame;
            first = firstPacketAt;
            last = lastPacketAt;
            jumps = coordinateJumpCount;
            backwards = timestampBackwardCount;
        }

        var summary = new ProbeSessionSummary(
            Interlocked.Read(ref validPackets),
            Interlocked.Read(ref invalidPackets),
            Interlocked.Read(ref drivingPackets),
            Interlocked.Read(ref currentLapPositivePackets),
            Interlocked.Read(ref currentRacePositivePackets),
            Interlocked.Read(ref racePositionPositivePackets),
            jumps,
            backwards,
            first,
            last,
            frame?.Raw.CarOrdinal ?? 0,
            frame?.Raw.CarClass ?? 0,
            frame?.Raw.CarPerformanceIndex ?? 0);
        return new ProbeSessionDocument(
            ProbeSessionFile.CurrentSchemaVersion,
            "0.1.1",
            sessionId,
            SessionNameBox.Text.Trim(),
            SelectedRole(),
            sessionStartedAt,
            DateTimeOffset.UtcNow,
            AddressBox.Text.Trim(),
            int.TryParse(PortBox.Text.Trim(), out var port) ? port : 2299,
            summary,
            markers.ToArray(),
            traceSnapshot,
            NotesBox.Text.Trim());
    }

    private void ResetSessionState()
    {
        lock (stateGate)
        {
            recentFrames.Clear();
            trace.Clear();
            latestFrame = null;
            previousFrame = null;
            nextTraceAt = null;
            firstPacketAt = null;
            lastPacketAt = null;
            lastReceiveError = null;
            coordinateJumpCount = 0;
            timestampBackwardCount = 0;
            traceLimitReached = false;
        }

        validPackets = invalidPackets = drivingPackets = currentLapPositivePackets =
            currentRacePositivePackets = racePositionPositivePackets = 0;
        previousRatePacketCount = 0;
        previousRateAt = DateTimeOffset.UtcNow;
        packetsPerSecond = 0;
        sessionStartedAt = DateTimeOffset.UtcNow;
        sessionId = Guid.NewGuid();
        markers.Clear();
    }

    private async Task StopReceiverAsync()
    {
        receiveCancellation?.Cancel();
        if (source is not null) await source.DisposeAsync();
        if (receiveTask is not null)
        {
            try { await receiveTask; } catch (OperationCanceledException) { }
        }

        source = null;
        receiveTask = null;
        receiveCancellation?.Dispose();
        receiveCancellation = null;
        RestoreControlsAfterStop();
        if (Interlocked.Read(ref validPackets) > 0) SetStatus("采集已停止，可保存会话", "#98A9BC");
        else SetStatus("未监听", "#98A9BC");
    }

    private void ShutdownReceiver()
    {
        receiveCancellation?.Cancel();
        source?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void RestoreControlsAfterStop()
    {
        AddressBox.IsEnabled = true;
        PortBox.IsEnabled = true;
        SessionNameBox.IsEnabled = true;
        SessionRoleBox.IsEnabled = true;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void BrowseSessionInto(TextBox target)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "LazyForza 地产验证会话 (*.lfzestateprobe.json)|*.lfzestateprobe.json|JSON 文件 (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }

    private static EstateCoordinateMarker ToAnalysisMarker(ProbeMarkerDocument marker) => new(
        marker.Name, marker.X, marker.Y, marker.Z, marker.YawRadians, marker.SpreadMeters, marker.SampleCount);

    private static ProbeTraceSample ToTraceSample(TelemetryFrame frame) => new(
        frame.ArrivalTime,
        frame.Raw.TimestampMS,
        frame.Raw.Position.X,
        frame.Raw.Position.Y,
        frame.Raw.Position.Z,
        frame.Raw.Yaw,
        frame.Raw.Speed,
        frame.Raw.IsRaceOn,
        frame.Raw.CurrentLap,
        frame.Raw.CurrentRaceTime,
        frame.Raw.LapNumber,
        frame.Raw.RacePosition);

    private static string FormatComparison(
        ProbeSessionDocument reference,
        ProbeSessionDocument candidate,
        EstateCoordinateComparison comparison)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"结论：{CompatibilityName(comparison.Compatibility)}");
        builder.AppendLine(comparison.Explanation);
        builder.AppendLine();
        builder.AppendLine($"参考：{reference.SessionName} / {reference.SessionRole}");
        builder.AppendLine($"比较：{candidate.SessionName} / {candidate.SessionRole}");
        builder.AppendLine($"同名标记点：{comparison.MatchedMarkerCount}");
        if (comparison.Compatibility == EstateCoordinateCompatibility.InsufficientEvidence) return builder.ToString();

        builder.AppendLine($"直接坐标 RMS：{comparison.DirectRmsMeters:F3} m");
        builder.AppendLine($"刚体拟合 RMS：{comparison.FittedRmsMeters:F3} m");
        builder.AppendLine($"最大拟合误差：{comparison.MaximumFittedErrorMeters:F3} m");
        builder.AppendLine($"估计比例：{comparison.EstimatedScaleRatio:F6}");
        builder.AppendLine();
        builder.AppendLine("将待比较会话映射到参考会话：");
        builder.AppendLine($"  水平旋转：{comparison.RotationDegrees:F4}°");
        builder.AppendLine($"  平移 X/Y/Z：{comparison.TranslationX:F3} / {comparison.TranslationY:F3} / {comparison.TranslationZ:F3} m");
        builder.AppendLine();
        builder.AppendLine("各标记点误差（直接 → 拟合）：");
        foreach (var residual in comparison.Residuals)
            builder.AppendLine($"  {residual.Name}: {residual.DirectErrorMeters:F3} → {residual.FittedErrorMeters:F3} m");

        var noisy = reference.Markers.Concat(candidate.Markers)
            .Where(marker => marker.SpreadMeters > 0.5)
            .Select(marker => $"{marker.Name} {marker.SpreadMeters:F2} m")
            .ToArray();
        if (noisy.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"采样离散度偏高：{string.Join("；", noisy)}。建议停车后重新采样。");
        }

        return builder.ToString();
    }

    private static string CompatibilityName(EstateCoordinateCompatibility compatibility) => compatibility switch
    {
        EstateCoordinateCompatibility.DirectMatch => "坐标直接一致",
        EstateCoordinateCompatibility.RigidTransform => "可通过固定刚体变换校准",
        EstateCoordinateCompatibility.NeedsReview => "接近可用，仍需复测",
        EstateCoordinateCompatibility.Incompatible => "当前样本不兼容",
        _ => "证据不足"
    };

    private static string Ratio(long numerator, long denominator) => denominator <= 0
        ? "—"
        : ((double)numerator / denominator).ToString("P1", CultureInfo.CurrentCulture);

    private static double Distance(Vector3F left, Vector3F right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private string SelectedRole() => SessionRoleBox.SelectedItem is ComboBoxItem item
        ? item.Content?.ToString() ?? "其他"
        : "其他";

    private static string SafeFileName(string value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "estate-probe" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '-');
        return safe;
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
    }
}
