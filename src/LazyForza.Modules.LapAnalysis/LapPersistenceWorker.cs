using LazyForza.Domain;
using LazyForza.Storage;
using System.Threading.Channels;
using System.Collections.Concurrent;

namespace LazyForza.Modules.LapAnalysis;

/// <summary>
/// Serializes lap mutations away from the telemetry consumer so slow storage
/// cannot stall UDP frame processing.
/// </summary>
internal sealed class LapPersistenceWorker
{
    private readonly LazyForzaStore store;
    private readonly Action<string> log;
    private readonly Action<Exception> reportFailure;
    private readonly Action recordedLapSaved;
    private Channel<LapPersistenceCommand>? queue;
    private Task? workerTask;
    private Exception? lastFailure;
    private readonly ConcurrentDictionary<Guid, RecordedLapCapture> checkpoints = new();
    public string? FailureMessage => Volatile.Read(ref lastFailure)?.Message;

    public LapPersistenceWorker(
        LazyForzaStore store,
        Action<string> log,
        Action<Exception> reportFailure,
        Action recordedLapSaved)
    {
        this.store = store;
        this.log = log;
        this.reportFailure = reportFailure;
        this.recordedLapSaved = recordedLapSaved;
    }

    public void Start()
    {
        if (queue is not null) return;
        queue = Channel.CreateUnbounded<LapPersistenceCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        workerTask = Task.Run(() => RunAsync(queue.Reader), CancellationToken.None);
    }

    public void Enqueue(LapPersistenceCommand command)
    {
        if (command is { Capture: { } checkpoint, Lap: null } && queue is not null)
        {
            lock (checkpoints)
            {
                var markerNeeded = checkpoints.TryAdd(checkpoint.Id, checkpoint);
                if (!markerNeeded) { checkpoints[checkpoint.Id] = checkpoint; return; }
            }
            command = default(LapPersistenceCommand) with { CheckpointId = checkpoint.Id };
        }
        if (queue?.Writer.TryWrite(command) == true) return;
        Execute(command);
    }

    public async Task StopAsync()
    {
        queue?.Writer.TryComplete();
        if (workerTask is not null) await workerTask.ConfigureAwait(false);
        queue = null;
        workerTask = null;
    }

    public Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(default(LapPersistenceCommand) with { Completion = completion });
        return completion.Task;
    }

    private async Task RunAsync(ChannelReader<LapPersistenceCommand> reader)
    {
        await foreach (var command in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Execute(command);
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                log($"Lap persistence failed: {exception}");
                reportFailure(exception);
            }
        }
    }

    private void Execute(LapPersistenceCommand command)
    {
        if (command.CheckpointId is Guid checkpointId)
        {
            RecordedLapCapture? latest;
            lock (checkpoints) checkpoints.TryRemove(checkpointId, out latest);
            if (latest is not null) store.SavePendingLapCapture(latest);
            return;
        }
        if (command.Completion is { } completion)
        {
            if (lastFailure is { } failure) completion.TrySetException(failure);
            else completion.TrySetResult();
            return;
        }
        if (command is { Lap: { } recordedLap, Capture: { } checkpoint })
        {
            store.SaveRecordedLap(checkpoint, command.NewTrack ?? checkpoint.ReferenceTrack!, command.NewSectors!, recordedLap);
            recordedLapSaved();
            return;
        }
        if (command.Capture is { } capture) store.SavePendingLapCapture(capture);
        if (command.NewTrack is { } newTrack) store.SaveTrack(newTrack, command.NewSectors!);
        if (command.DiscardCaptureId is Guid discarded) store.DeletePendingLapCapture(discarded);
        if (command.Lap is { } lap)
        {
            if (command.Capture is null || store.LoadLapsByIds([lap.Id]).Count == 0) store.SaveLap(lap);
            if (command.Capture is not null) store.DeletePendingLapCapture(command.Capture.Id);
            log($"Persisted lap {lap.Id}: total={lap.TotalSeconds:0.000}, samples={lap.Samples.Count}.");
            return;
        }

        if (command.DeleteLapId is Guid lapId)
        {
            store.DeleteLap(lapId);
            recordedLapSaved();
            log($"Deleted lap {lapId}.");
            return;
        }

        if (command.DeleteTrackLapsId is not Guid trackId) return;
        store.DeleteTrackLaps(trackId, command.PerformanceClasses, command.PreserveLapIds);
        recordedLapSaved();
        var scope = command.PerformanceClasses is { Length: > 0 }
            ? $"classes [{string.Join(',', command.PerformanceClasses)}]"
            : "all classes";
        log(command.PreserveLapIds is { Length: > 0 }
            ? $"Deleted saved laps for track {trackId}, scope={scope}, preserving class bests [{string.Join(',', command.PreserveLapIds)}]."
            : $"Deleted saved laps for track {trackId}, scope={scope}, including class bests.");
    }
}

internal readonly record struct LapPersistenceCommand(
    LapRecord? Lap,
    Guid? DeleteLapId,
    Guid? DeleteTrackLapsId,
    int[]? PerformanceClasses,
    Guid[]? PreserveLapIds)
{
    public TaskCompletionSource? Completion { get; init; }
    public RecordedLapCapture? Capture { get; init; }
    public TrackTemplate? NewTrack { get; init; }
    public IReadOnlyList<SectorDefinition>? NewSectors { get; init; }
    public Guid? DiscardCaptureId { get; init; }
    public Guid? CheckpointId { get; init; }
    public static LapPersistenceCommand Save(LapRecord lap) => new(lap, null, null, null, null);
    public static LapPersistenceCommand Delete(Guid lapId) => new(null, lapId, null, null, null);
    public static LapPersistenceCommand DeleteTrack(
        Guid trackId,
        int[]? performanceClasses,
        Guid[] preserveLapIds) =>
        new(null, null, trackId, performanceClasses, preserveLapIds);
}
