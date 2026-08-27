using System.Net;
using System.Net.Sockets;

namespace LazyForza.Fh5MapProbe;

public sealed class Fh5MapCaptureSession : IAsyncDisposable
{
    private readonly object stateGate = new();
    private readonly Fh5CaptureSettings settings;
    private readonly CapturePackageWriter writer;
    private readonly UdpClient client;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task receiveTask;
    private readonly Queue<(DateTimeOffset ReceivedAt, Fh5DataOutFrame Frame)> recentFrames = new();
    private readonly List<Fh5CoordinateMarker> markers = [];
    private readonly Dictionary<int, long> packetLengths = [];
    private readonly Dictionary<string, long> rejectionReasons = [];
    private long totalPackets;
    private long validPackets;
    private long invalidPackets;
    private long activeDrivingPackets;
    private long sequence;
    private Fh5DataOutFrame? latestFrame;
    private DateTimeOffset? latestPacketAt;
    private double? minimumX;
    private double? maximumX;
    private double? minimumY;
    private double? maximumY;
    private double? minimumZ;
    private double? maximumZ;
    private double maximumSpeedDeltaMps;
    private string? lastError;
    private bool stopping;
    private bool completed;

    public Fh5MapCaptureSession(Fh5CaptureSettings settings)
    {
        this.settings = settings;
        if (!IPAddress.TryParse(settings.ListenAddress, out var address))
            throw new ArgumentException("监听地址不是有效 IP。", nameof(settings));
        if (settings.ListenPort is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(settings), "端口必须在 1–65535 之间。");
        writer = new CapturePackageWriter(settings.OutputPath);
        try
        {
            client = new UdpClient(new IPEndPoint(address, settings.ListenPort));
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        receiveTask = ReceiveAsync(cancellation.Token);
    }

    public string RecoveryDirectory => writer.RecoveryDirectory;

    public Fh5CaptureSnapshot Snapshot()
    {
        lock (stateGate)
        {
            return new Fh5CaptureSnapshot(
                !stopping && !completed,
                settings.StartedAt,
                totalPackets,
                validPackets,
                invalidPackets,
                activeDrivingPackets,
                new Dictionary<int, long>(packetLengths),
                latestFrame,
                latestPacketAt,
                Bounds(),
                maximumSpeedDeltaMps,
                lastError,
                markers.ToArray(),
                settings.OutputPath);
        }
    }

    public Fh5CoordinateMarker CaptureMarker(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("请输入标记名称。");
        lock (stateGate)
        {
            if (stopping || completed) throw new InvalidOperationException("当前没有正在运行的采集。");
            if (latestPacketAt is not DateTimeOffset latest)
                throw new InvalidOperationException("尚未收到有效 FH5 数据。");
            var samples = recentFrames
                .Where(item => item.Frame.IsRaceOn == 1 && latest - item.ReceivedAt <= TimeSpan.FromSeconds(1))
                .Select(item => item.Frame)
                .ToArray();
            if (samples.Length < 5)
                throw new InvalidOperationException("最近一秒的有效驾驶数据不足，请进入驾驶状态后重试。");
            var x = Median(samples.Select(frame => (double)frame.PositionX));
            var y = Median(samples.Select(frame => (double)frame.PositionY));
            var z = Median(samples.Select(frame => (double)frame.PositionZ));
            var spread = samples.Max(frame => Distance(frame, x, y, z));
            var marker = new Fh5CoordinateMarker(
                Guid.NewGuid(),
                name.Trim(),
                latest,
                x,
                y,
                z,
                spread,
                samples.Length,
                samples.Average(frame => (double)frame.SpeedMps));
            markers.Add(marker);
            return marker;
        }
    }

    public void RemoveMarker(Guid markerId)
    {
        lock (stateGate)
        {
            markers.RemoveAll(marker => marker.Id == markerId);
        }
    }

    public async Task StopAndSaveAsync(string notes, CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            if (completed) return;
            if (stopping) throw new InvalidOperationException("采集正在停止。");
            stopping = true;
        }
        cancellation.Cancel();
        client.Dispose();
        try
        {
            await receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Fh5CaptureManifest manifest;
        Fh5CoordinateMarker[] markerSnapshot;
        lock (stateGate)
        {
            markerSnapshot = markers.ToArray();
            manifest = new Fh5CaptureManifest(
                CapturePackageWriter.SchemaVersion,
                "0.1.0",
                "Forza Horizon 5",
                settings.Region.ToString(),
                settings.RegionName,
                settings.SessionLabel,
                notes.Trim(),
                settings.StartedAt,
                DateTimeOffset.UtcNow,
                settings.ListenAddress,
                settings.ListenPort,
                totalPackets,
                validPackets,
                invalidPackets,
                activeDrivingPackets,
                new Dictionary<int, long>(packetLengths),
                new Dictionary<string, long>(rejectionReasons),
                Bounds(),
                maximumSpeedDeltaMps,
                markerSnapshot.Length,
                "raw-packets.bin: ASCII LF5RAW01 header, then repeated Int64 UTC ticks + Int32 length + packet bytes",
                "frames.csv: one row for every successfully parsed packet, invariant numeric formatting",
                "Little-endian FH5 Horizon Dash; 12-byte Horizon extension at 232–243; PositionX/Y/Z at 244/248/252; 323 or 324 bytes");
        }
        await writer.CompleteAsync(manifest, markerSnapshot, cancellationToken).ConfigureAwait(false);
        lock (stateGate) completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!completed && !stopping)
        {
            cancellation.Cancel();
            client.Dispose();
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        writer.Dispose();
        cancellation.Dispose();
        client.Dispose();
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var receivedAt = DateTimeOffset.UtcNow;
            writer.WriteRawPacket(receivedAt, result.Buffer);
            var parsed = Fh5DataOutParser.TryParse(result.Buffer, out var frame, out var error);
            lock (stateGate)
            {
                totalPackets++;
                latestPacketAt = receivedAt;
                packetLengths[result.Buffer.Length] = packetLengths.GetValueOrDefault(result.Buffer.Length) + 1;
                if (!parsed || frame is null)
                {
                    invalidPackets++;
                    lastError = error;
                    var reason = error ?? "未知解析错误";
                    rejectionReasons[reason] = rejectionReasons.GetValueOrDefault(reason) + 1;
                    continue;
                }

                validPackets++;
                latestFrame = frame;
                lastError = null;
                maximumSpeedDeltaMps = Math.Max(maximumSpeedDeltaMps, frame.SpeedDeltaMps);
                if (frame.IsRaceOn == 1)
                {
                    activeDrivingPackets++;
                    UpdateBounds(frame);
                }
                recentFrames.Enqueue((receivedAt, frame));
                while (recentFrames.TryPeek(out var oldest) &&
                       receivedAt - oldest.ReceivedAt > TimeSpan.FromSeconds(2))
                    recentFrames.Dequeue();
            }
            writer.WriteFrame(Interlocked.Increment(ref sequence), receivedAt, frame);
        }
    }

    private void UpdateBounds(Fh5DataOutFrame frame)
    {
        minimumX = minimumX is double minX ? Math.Min(minX, frame.PositionX) : frame.PositionX;
        maximumX = maximumX is double maxX ? Math.Max(maxX, frame.PositionX) : frame.PositionX;
        minimumY = minimumY is double minY ? Math.Min(minY, frame.PositionY) : frame.PositionY;
        maximumY = maximumY is double maxY ? Math.Max(maxY, frame.PositionY) : frame.PositionY;
        minimumZ = minimumZ is double minZ ? Math.Min(minZ, frame.PositionZ) : frame.PositionZ;
        maximumZ = maximumZ is double maxZ ? Math.Max(maxZ, frame.PositionZ) : frame.PositionZ;
    }

    private Fh5CoordinateBounds? Bounds() =>
        minimumX is double minX && maximumX is double maxX &&
        minimumY is double minY && maximumY is double maxY &&
        minimumZ is double minZ && maximumZ is double maxZ
            ? new Fh5CoordinateBounds(minX, maxX, minY, maxY, minZ, maxZ)
            : null;

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static double Distance(Fh5DataOutFrame frame, double x, double y, double z)
    {
        var dx = frame.PositionX - x;
        var dy = frame.PositionY - y;
        var dz = frame.PositionZ - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
