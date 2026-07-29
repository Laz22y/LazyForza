using LazyForza.Domain;
using System.IO;
using LazyForza.Modules.Abstractions;
using LazyForza.Storage;
using LazyForza.Telemetry;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class TelemetryRecorderController : IAsyncDisposable
{
    private readonly ITelemetryFeed telemetry;
    private readonly DataDirectoryService directories;
    private readonly LazyForzaStore store;
    private readonly TelemetrySourceKind sourceKind;
    private readonly LapAnalysisModule lapAnalysis;
    private readonly Action<string> log;
    private readonly RecordingCatalog catalog;
    private readonly RecordingCapacityManager capacity;
    private readonly SemaphoreSlim manualGate = new(1, 1);
    private readonly SemaphoreSlim automaticGate = new(1, 1);
    private readonly Queue<TelemetryFrame> preRoll = new();
    private ITelemetrySubscription? manualSubscription;
    private TelemetryRecordingWriter? manualWriter;
    private CancellationTokenSource? manualCancellation;
    private Task? manualTask;
    private ITelemetrySubscription? monitorSubscription;
    private CancellationTokenSource? monitorCancellation;
    private Task? monitorTask;
    private TelemetryRecordingWriter? automaticWriter;
    private string? automaticPartialPath;
    private string? automaticFinalPath;
    private DateTimeOffset automaticStartedAt;
    private Guid? automaticSessionId;
    private long automaticFrames;
    private long automaticBaseBytes;
    private CancellationTokenSource? postRollCancellation;
    private Guid? blockedSessionId;
    private AutomaticRecordingOptions options;

    public TelemetryRecorderController(
        ITelemetryFeed telemetry,
        DataDirectoryService directories,
        LazyForzaStore store,
        TelemetrySourceKind sourceKind,
        LapAnalysisModule lapAnalysis,
        Action<string> log)
    {
        this.telemetry = telemetry;
        this.directories = directories;
        this.store = store;
        this.sourceKind = sourceKind;
        this.lapAnalysis = lapAnalysis;
        this.log = log;
        catalog = new RecordingCatalog(directories.RecordingsPath);
        capacity = new RecordingCapacityManager(directories.RecordingsPath, catalog);
        options = AutomaticRecordingOptions.Load(store);
    }

    public bool IsRecording => IsManualRecording || IsAutomaticRecording;
    public bool IsManualRecording => manualTask is not null;
    public bool IsAutomaticRecording => automaticWriter is not null;
    public AutomaticRecordingOptions AutomaticOptions => options;
    public IReadOnlyList<RecordingCatalogEntry> Recordings => catalog.List();
    public long RecordingBytes => capacity.TotalBytes();
    public string AutomaticStatus { get; private set; } = "自动比赛录制未启用。";
    public string? CurrentPath { get; private set; }
    public long FramesWritten { get; private set; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (sourceKind != TelemetrySourceKind.Live)
        {
            AutomaticStatus = "Simulator / Replay 不参与自动比赛录制。";
            return;
        }
        monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        monitorSubscription = await telemetry.SubscribeAsync("automatic-competition-recorder", cancellationToken);
        monitorTask = Task.Run(
            () => MonitorCompetitionsAsync(monitorCancellation.Token),
            CancellationToken.None);
        AutomaticStatus = options.Enabled ? "等待比赛开始。" : "自动比赛录制未启用。";
    }

    public async ValueTask SetAutomaticOptionsAsync(
        AutomaticRecordingOptions next,
        CancellationToken cancellationToken)
    {
        options = next with
        {
            MaximumBytes = Math.Clamp(next.MaximumBytes, 1024L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            MinimumFreeBytes = Math.Clamp(next.MinimumFreeBytes, 1024L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            PreRollSeconds = 15,
            PostRollSeconds = 10
        };
        options.Save(store);
        if (!options.Enabled)
        {
            await FinalizeAutomaticAsync("用户关闭自动录制", cancellationToken);
            AutomaticStatus = "自动比赛录制未启用。";
        }
        else if (sourceKind == TelemetrySourceKind.Live)
        {
            AutomaticStatus = "等待比赛开始。";
        }
    }

    public void SetPinned(string recordingPath, bool pinned) =>
        catalog.SetPinned(recordingPath, pinned);

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await manualGate.WaitAsync(cancellationToken);
        try
        {
            if (manualTask is not null) return;
            directories.EnsureCreated();
            CurrentPath = Path.Combine(directories.RecordingsPath, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.lfztelemetry");
            manualSubscription = await telemetry.SubscribeAsync("raw-recorder", cancellationToken);
            manualWriter = await TelemetryRecordingWriter.CreateAsync(CurrentPath,
                new RecordingMetadata("LazyForza", 1, telemetry.Latest?.Source ?? TelemetrySourceKind.Live, DateTimeOffset.UtcNow, "Raw 324-byte Data Out packets"), cancellationToken);
            manualCancellation = new CancellationTokenSource();
            FramesWritten = 0;
            manualTask = Task.Run(() => RecordManualAsync(manualCancellation.Token), CancellationToken.None);
        }
        finally
        {
            manualGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await manualGate.WaitAsync(cancellationToken);
        try
        {
            if (manualTask is null) return;
            manualCancellation?.Cancel();
            if (manualSubscription is not null) await manualSubscription.DisposeAsync();
            try { await manualTask; } catch (OperationCanceledException) { }
            if (manualWriter is not null) await manualWriter.DisposeAsync();
            manualCancellation?.Dispose();
            manualSubscription = null;
            manualWriter = null;
            manualCancellation = null;
            manualTask = null;
        }
        finally
        {
            manualGate.Release();
        }
    }

    private async Task RecordManualAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in manualSubscription!.Frames.ReadAllAsync(cancellationToken))
        {
            await manualWriter!.WriteAsync(frame, cancellationToken);
            FramesWritten++;
        }
    }

    private async Task MonitorCompetitionsAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in monitorSubscription!.Frames.ReadAllAsync(cancellationToken))
        {
            BufferPreRoll(frame);
            var inCompetition = lapAnalysis.HasCurrentCompetitionSession;
            var startedNow = false;
            if (!options.Enabled)
            {
                if (IsAutomaticRecording)
                    await FinalizeAutomaticAsync("自动录制已关闭", cancellationToken);
                continue;
            }

            if (inCompetition && automaticSessionId != lapAnalysis.CurrentSessionId)
            {
                postRollCancellation?.Cancel();
                if (IsAutomaticRecording)
                    await FinalizeAutomaticAsync("新比赛开始", cancellationToken);
                if (blockedSessionId != lapAnalysis.CurrentSessionId)
                    startedNow = await StartAutomaticAsync(frame, cancellationToken);
            }
            if (IsAutomaticRecording && !startedNow)
            {
                await automaticGate.WaitAsync(cancellationToken);
                try
                {
                    if (automaticWriter is not null)
                    {
                        await automaticWriter.WriteAsync(frame, cancellationToken);
                        automaticFrames++;
                    }
                }
                finally
                {
                    automaticGate.Release();
                }

                if (automaticFrames % 300 == 0 &&
                    (automaticBaseBytes + (automaticWriter?.BytesWritten ?? 0) >= options.MaximumBytes ||
                     capacity.AvailableBytes() < options.MinimumFreeBytes))
                {
                    blockedSessionId = automaticSessionId;
                    await FinalizeAutomaticAsync("达到容量或磁盘保留空间限制", cancellationToken);
                    AutomaticStatus = "本场自动录制已因容量限制停止。";
                    continue;
                }
            }

            if (!inCompetition && IsAutomaticRecording && postRollCancellation is null)
                SchedulePostRoll();
            else if (inCompetition && postRollCancellation is not null)
            {
                postRollCancellation.Cancel();
                postRollCancellation.Dispose();
                postRollCancellation = null;
                AutomaticStatus = "正在自动录制比赛。";
            }
        }
    }

    private void BufferPreRoll(TelemetryFrame frame)
    {
        preRoll.Enqueue(frame);
        var minimum = frame.ArrivalTime - TimeSpan.FromSeconds(options.PreRollSeconds);
        while (preRoll.Count > 0 && preRoll.Peek().ArrivalTime < minimum)
            preRoll.Dequeue();
    }

    private async Task<bool> StartAutomaticAsync(
        TelemetryFrame frame,
        CancellationToken cancellationToken)
    {
        if (IsAutomaticRecording || sourceKind != TelemetrySourceKind.Live) return false;
        var result = capacity.Prepare(options);
        if (!result.CanStart)
        {
            blockedSessionId = lapAnalysis.CurrentSessionId;
            AutomaticStatus = result.Message;
            log($"Automatic recording skipped: {result.Message}");
            return false;
        }

        directories.EnsureCreated();
        var stem = $"auto-{DateTime.Now:yyyyMMdd-HHmmss}-{lapAnalysis.CurrentSessionId:N}";
        automaticFinalPath = Path.Combine(directories.RecordingsPath, stem + ".lfztelemetry");
        automaticPartialPath = automaticFinalPath + ".partial";
        automaticWriter = await TelemetryRecordingWriter.CreateAsync(
            automaticPartialPath,
            new RecordingMetadata(
                "LazyForza",
                1,
                TelemetrySourceKind.Live,
                DateTimeOffset.UtcNow,
                $"Automatic competition recording; session={lapAnalysis.CurrentSessionId}"),
            cancellationToken);
        automaticStartedAt = preRoll.Count > 0 ? preRoll.Peek().ArrivalTime : frame.ArrivalTime;
        automaticSessionId = lapAnalysis.CurrentSessionId;
        automaticFrames = 0;
        automaticBaseBytes = result.UsedBytes;
        foreach (var buffered in preRoll)
        {
            await automaticWriter.WriteAsync(buffered, cancellationToken);
            automaticFrames++;
        }
        AutomaticStatus = $"正在自动录制比赛；已包含赛前 {options.PreRollSeconds} 秒缓冲。";
        CurrentPath = automaticFinalPath;
        log($"Automatic competition recording started: {automaticPartialPath}");
        return true;
    }

    private void SchedulePostRoll()
    {
        postRollCancellation = new CancellationTokenSource();
        var token = postRollCancellation.Token;
        AutomaticStatus = $"比赛结束，继续记录 {options.PostRollSeconds} 秒。";
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PostRollSeconds), token);
                await FinalizeAutomaticAsync("比赛结束", CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async ValueTask FinalizeAutomaticAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await automaticGate.WaitAsync(cancellationToken);
        try
        {
            if (automaticWriter is null) return;
            await automaticWriter.DisposeAsync();
            automaticWriter = null;
            if (automaticPartialPath is null || automaticFinalPath is null) return;
            File.Move(automaticPartialPath, automaticFinalPath, overwrite: false);
            var duration = Math.Max(
                0,
                ((telemetry.Latest?.ArrivalTime ?? DateTimeOffset.UtcNow) - automaticStartedAt).TotalSeconds);
            var currentSessionLaps = lapAnalysis.CurrentSessionLaps;
            var personalBest = currentSessionLaps.Any(lap =>
                lap.IsValid &&
                lapAnalysis.VisibleLaps
                    .Where(candidate => candidate.IsValid &&
                                        candidate.Vehicle.CarClass == lap.Vehicle.CarClass)
                    .OrderBy(candidate => candidate.TotalSeconds)
                    .FirstOrDefault()?.Id == lap.Id);
            var recognitionAnomaly =
                lapAnalysis.CompetitionPageSnapshot?.MatchRejectionEligible == true ||
                lapAnalysis.MatchDiagnostics.State.Contains("未找到", StringComparison.Ordinal) ||
                lapAnalysis.MatchDiagnostics.State.Contains("纠正", StringComparison.Ordinal);
            var protectedUntil = personalBest
                ? DateTimeOffset.UtcNow.AddDays(30)
                : recognitionAnomaly
                    ? DateTimeOffset.UtcNow.AddDays(14)
                    : (DateTimeOffset?)null;
            var protectionReason = personalBest
                ? "个人最佳圈，自动保护 30 天"
                : recognitionAnomaly
                    ? "赛道识别异常，自动保护 14 天"
                    : null;
            if (automaticSessionId is Guid completedSessionId)
            {
                store.AttachRawRecording(
                    completedSessionId,
                    TelemetrySourceKind.Live.ToString(),
                    automaticStartedAt,
                    automaticFinalPath);
            }
            catalog.Save(new RecordingCatalogEntry(
                automaticFinalPath,
                automaticStartedAt,
                automaticSessionId,
                lapAnalysis.CurrentTrack?.Name,
                automaticFrames,
                duration,
                true,
                false,
                protectionReason,
                protectedUntil));
            log($"Automatic competition recording finalized: {automaticFinalPath}; reason={reason}; frames={automaticFrames}.");
            AutomaticStatus = $"自动录制已保存：{Path.GetFileName(automaticFinalPath)}";
        }
        finally
        {
            automaticPartialPath = null;
            automaticFinalPath = null;
            automaticSessionId = null;
            automaticFrames = 0;
            postRollCancellation?.Dispose();
            postRollCancellation = null;
            automaticGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        monitorCancellation?.Cancel();
        if (monitorSubscription is not null) await monitorSubscription.DisposeAsync();
        if (monitorTask is not null)
        {
            try { await monitorTask; } catch (OperationCanceledException) { }
        }
        await FinalizeAutomaticAsync("程序退出", CancellationToken.None);
        monitorCancellation?.Dispose();
        postRollCancellation?.Cancel();
        postRollCancellation?.Dispose();
        manualGate.Dispose();
        automaticGate.Dispose();
    }
}

internal sealed class RollingLog : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly StreamWriter writer;

    public RollingLog(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "lazyforza.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            var previous = Path.Combine(directory, "lazyforza.previous.log");
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Write(string message)
    {
        lock (gate) writer.WriteLine($"{DateTimeOffset.Now:O} [INFO] {message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}");
    }

    public void Dispose()
    {
        lock (gate) writer.Dispose();
    }
}
