using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class DiagnosticCaptureService : IAsyncDisposable
{
    private const int MaximumLiveSamples = 3_600;
    private const int MaximumEvents = 500;
    private const int MaximumSnapshots = 8;
    private const int SnapshotSampleCount = 900;
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ITelemetryFeed telemetry;
    private readonly string dataRoot;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly Queue<DiagnosticTelemetrySample> samples = new();
    private readonly Queue<DiagnosticEventEntry> events = new();
    private readonly Queue<DiagnosticAnomalySnapshot> snapshots = new();
    private CancellationTokenSource? cancellation;
    private ITelemetrySubscription? subscription;
    private Task? consumeTask;
    private DateTimeOffset? lastStoredAt;
    private TelemetryFrame? previousFrame;
    private TrackMatchDiagnostics latestTrackMatch = TrackMatchDiagnostics.Empty;
    private bool disposed;

    public DiagnosticCaptureService(
        ITelemetryFeed telemetry,
        string dataRoot,
        Action<string> log)
    {
        this.telemetry = telemetry;
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.log = log;
    }

    public int BufferedSamples
    {
        get { lock (gate) return samples.Count; }
    }

    public int EventCount
    {
        get { lock (gate) return events.Count; }
    }

    public int AnomalyCount
    {
        get { lock (gate) return snapshots.Count; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (consumeTask is not null) return;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscription = await telemetry.SubscribeAsync(
            "diagnostic-ring-buffer",
            cancellation.Token);
        consumeTask = Task.Run(
            () => ConsumeAsync(subscription.Frames, cancellation.Token),
            CancellationToken.None);
    }

    public void UpdateTrackMatch(TrackMatchDiagnostics diagnostics)
    {
        Volatile.Write(ref latestTrackMatch, diagnostics);
    }

    public void RecordSignal(DiagnosticSignal signal)
    {
        var entry = new DiagnosticEventEntry(
            signal.OccurredAt,
            signal.Code,
            Sanitize(signal.Summary),
            signal.IsAnomaly,
            SanitizeData(signal.Data));
        lock (gate)
        {
            EnqueueBounded(events, entry, MaximumEvents);
            if (signal.IsAnomaly)
            {
                var trackMatch = Volatile.Read(ref latestTrackMatch);
                EnqueueBounded(
                    snapshots,
                    new DiagnosticAnomalySnapshot(
                        Guid.NewGuid(),
                        signal.OccurredAt,
                        signal.Code,
                        entry.Summary,
                        samples.TakeLast(SnapshotSampleCount).ToArray(),
                        events.TakeLast(80).ToArray(),
                        ToTrackMatchSnapshot(trackMatch)),
                    MaximumSnapshots);
            }
        }
    }

    public void RecordLog(string source, string message)
    {
        RecordSignal(new DiagnosticSignal(
            $"log.{source}",
            message,
            false,
            DateTimeOffset.UtcNow));
    }

    public string Export(
        string targetPath,
        int schemaVersion,
        string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticTelemetrySample[] sampleSnapshot;
        DiagnosticEventEntry[] eventSnapshot;
        DiagnosticAnomalySnapshot[] anomalySnapshot;
        lock (gate)
        {
            sampleSnapshot = samples.ToArray();
            eventSnapshot = events.ToArray();
            anomalySnapshot = snapshots.ToArray();
        }

        var trackMatch = ToTrackMatchSnapshot(Volatile.Read(ref latestTrackMatch));
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["telemetry.json"] = JsonSerializer.SerializeToUtf8Bytes(sampleSnapshot, JsonOptions),
            ["events.json"] = JsonSerializer.SerializeToUtf8Bytes(eventSnapshot, JsonOptions),
            ["anomalies.json"] = JsonSerializer.SerializeToUtf8Bytes(anomalySnapshot, JsonOptions),
            ["track-match.json"] = JsonSerializer.SerializeToUtf8Bytes(trackMatch, JsonOptions)
        };
        var hashes = payloads.ToDictionary(
            pair => pair.Key,
            pair => Convert.ToHexString(SHA256.HashData(pair.Value)),
            StringComparer.Ordinal);
        var manifest = new DiagnosticPackageManifest(
            1,
            DateTimeOffset.UtcNow,
            applicationVersion,
            schemaVersion,
            Environment.OSVersion.VersionString,
            "用户名、用户目录、数据目录及原始 UDP 字节未写入；遥测仅保留问题回放所需字段。",
            sampleSnapshot.Length,
            eventSnapshot.Length,
            anomalySnapshot.Length,
            hashes);

        var fullTargetPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)
                                  ?? throw new InvalidOperationException("诊断包路径无效。"));
        var temporaryPath = fullTargetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                WriteJsonEntry(archive, "manifest.json", manifest);
                foreach (var (name, bytes) in payloads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(bytes);
                }
            }

            File.Move(temporaryPath, fullTargetPath, true);
            log($"Diagnostic package exported: {RedactPath(fullTargetPath)}");
            return fullTargetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task ConsumeAsync(
        System.Threading.Channels.ChannelReader<TelemetryFrame> frames,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Observe(frame);
        }
    }

    private void Observe(TelemetryFrame frame)
    {
        var previous = previousFrame;
        if (previous is not null)
        {
            var arrivalGap = frame.ArrivalTime - previous.ArrivalTime;
            if (arrivalGap >= TimeSpan.FromSeconds(2))
            {
                RecordSignal(new DiagnosticSignal(
                    "udp.long-gap",
                    $"UDP 数据中断 {arrivalGap.TotalSeconds:0.00} 秒后恢复。",
                    true,
                    frame.ArrivalTime,
                    new Dictionary<string, string>
                    {
                        ["gapMilliseconds"] = arrivalGap.TotalMilliseconds.ToString("0"),
                        ["previousTimestamp"] = previous.Raw.TimestampMS.ToString(),
                        ["currentTimestamp"] = frame.Raw.TimestampMS.ToString()
                    }));
            }

            if (previous.Raw.IsRaceOn == 1 && frame.Raw.IsRaceOn == 1)
            {
                var timestampJump = TimestampJump(previous.Raw.TimestampMS, frame.Raw.TimestampMS);
                if (timestampJump is not null)
                {
                    RecordSignal(new DiagnosticSignal(
                        "udp.timestamp-jump",
                        timestampJump,
                        true,
                        frame.ArrivalTime));
                }

                if (previous.Raw.CarOrdinal > 0 &&
                    frame.Raw.CarOrdinal > 0 &&
                    (previous.Raw.CarOrdinal != frame.Raw.CarOrdinal ||
                     previous.Raw.CarPerformanceIndex != frame.Raw.CarPerformanceIndex))
                {
                    RecordSignal(new DiagnosticSignal(
                        "vehicle.configuration-switch",
                        "比赛遥测中的车辆或性能指数发生突变。",
                        true,
                        frame.ArrivalTime,
                        new Dictionary<string, string>
                        {
                            ["previous"] =
                                $"{previous.Raw.CarOrdinal}/{previous.Raw.CarClass}/{previous.Raw.CarPerformanceIndex}",
                            ["current"] =
                                $"{frame.Raw.CarOrdinal}/{frame.Raw.CarClass}/{frame.Raw.CarPerformanceIndex}"
                        }));
                }
            }
        }

        previousFrame = frame;
        if (lastStoredAt is DateTimeOffset last &&
            frame.ArrivalTime - last < MinimumSampleInterval)
            return;
        lastStoredAt = frame.ArrivalTime;
        var raw = frame.Raw;
        var sample = new DiagnosticTelemetrySample(
            frame.ArrivalTime,
            frame.Sequence,
            frame.Source.ToString(),
            raw.IsRaceOn,
            raw.TimestampMS,
            raw.CarOrdinal,
            raw.CarClass,
            raw.CarPerformanceIndex,
            raw.LapNumber,
            raw.RacePosition,
            raw.CurrentRaceTime,
            raw.CurrentLap,
            raw.LastLap,
            raw.BestLap,
            raw.Position.X,
            raw.Position.Y,
            raw.Position.Z,
            raw.Yaw,
            raw.Speed,
            raw.CurrentEngineRpm,
            raw.Gear,
            raw.Accel,
            raw.Brake);
        lock (gate)
        {
            EnqueueBounded(samples, sample, MaximumLiveSamples);
        }
    }

    private static string? TimestampJump(uint previous, uint current)
    {
        if (previous == 0 || current == 0 || current == previous) return null;
        if (current < previous)
        {
            if (previous > 0xF0000000u && current < 0x0FFFFFFFu) return null;
            return $"FH6 时间戳从 {previous} 回退到 {current}。";
        }

        var delta = current - previous;
        return delta > 1_000
            ? $"FH6 时间戳向前跳变 {delta} ms。"
            : null;
    }

    private TrackMatchPackageSnapshot ToTrackMatchSnapshot(TrackMatchDiagnostics diagnostics) =>
        new(
            diagnostics.UpdatedAt,
            Sanitize(diagnostics.State),
            diagnostics.TotalRoutes,
            diagnostics.CoarseEligibleRoutes,
            diagnostics.FineCandidateRoutes,
            diagnostics.TopCandidates.Select(ToCandidateSnapshot).ToArray(),
            diagnostics.EliminatedCandidates.Select(ToCandidateSnapshot).ToArray());

    private TrackMatchCandidatePackageSnapshot ToCandidateSnapshot(
        TrackMatchCandidateDiagnostic candidate) =>
        new(
            Sanitize(candidate.TrackName),
            candidate.LayoutKind.ToString(),
            Sanitize(candidate.Category),
            candidate.LengthMeters,
            Sanitize(candidate.Stage),
            candidate.StartDistanceMeters,
            candidate.MeanDistanceMeters,
            candidate.ProgressMeters,
            candidate.ValidRatio,
            Sanitize(candidate.EliminationReason));

    private IReadOnlyDictionary<string, string>? SanitizeData(
        IReadOnlyDictionary<string, string>? data) =>
        data?.ToDictionary(
            pair => pair.Key,
            pair => Sanitize(pair.Value) ?? string.Empty,
            StringComparer.Ordinal);

    private string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value
            .Replace(dataRoot, "<DATA_ROOT>", StringComparison.OrdinalIgnoreCase)
            .Replace(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "<USER_PROFILE>",
                StringComparison.OrdinalIgnoreCase)
            .Replace(Environment.UserName, "<USER>", StringComparison.OrdinalIgnoreCase);
    }

    private string RedactPath(string path) =>
        Sanitize(path) ?? "<REDACTED_PATH>";

    private static void EnqueueBounded<T>(Queue<T> queue, T value, int maximum)
    {
        queue.Enqueue(value);
        while (queue.Count > maximum) queue.Dequeue();
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cancellation?.Cancel();
        if (subscription is not null) await subscription.DisposeAsync();
        if (consumeTask is not null)
        {
            try { await consumeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
    }

    private sealed record DiagnosticPackageManifest(
        int FormatVersion,
        DateTimeOffset CreatedAt,
        string ApplicationVersion,
        int SchemaVersion,
        string OperatingSystem,
        string Redaction,
        int TelemetrySamples,
        int Events,
        int AnomalySnapshots,
        IReadOnlyDictionary<string, string> Files);

    private sealed record DiagnosticTelemetrySample(
        DateTimeOffset At,
        long Sequence,
        string Source,
        int IsRaceOn,
        uint TimestampMs,
        int CarOrdinal,
        int CarClass,
        int PerformanceIndex,
        ushort LapNumber,
        byte RacePosition,
        float CurrentRaceTime,
        float CurrentLap,
        float LastLap,
        float BestLap,
        float X,
        float Y,
        float Z,
        float Yaw,
        float SpeedMps,
        float Rpm,
        byte Gear,
        byte Accel,
        byte Brake);

    private sealed record DiagnosticEventEntry(
        DateTimeOffset At,
        string Code,
        string? Summary,
        bool IsAnomaly,
        IReadOnlyDictionary<string, string>? Data);

    private sealed record DiagnosticAnomalySnapshot(
        Guid Id,
        DateTimeOffset At,
        string Code,
        string? Summary,
        IReadOnlyList<DiagnosticTelemetrySample> Telemetry,
        IReadOnlyList<DiagnosticEventEntry> Events,
        TrackMatchPackageSnapshot TrackMatch);

    private sealed record TrackMatchPackageSnapshot(
        DateTimeOffset UpdatedAt,
        string? State,
        int TotalRoutes,
        int CoarseEligibleRoutes,
        int FineCandidateRoutes,
        IReadOnlyList<TrackMatchCandidatePackageSnapshot> TopCandidates,
        IReadOnlyList<TrackMatchCandidatePackageSnapshot> EliminatedCandidates);

    private sealed record TrackMatchCandidatePackageSnapshot(
        string? TrackName,
        string LayoutKind,
        string? Category,
        double LengthMeters,
        string? Stage,
        double? StartDistanceMeters,
        double? MeanDistanceMeters,
        double ProgressMeters,
        double ValidRatio,
        string? EliminationReason);
}
