using LazyForza.Domain;
using LazyForza.Storage;
using System.Threading.Channels;

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
    private Channel<LapPersistenceCommand>? queue;
    private Task? workerTask;

    public LapPersistenceWorker(
        LazyForzaStore store,
        Action<string> log,
        Action<Exception> reportFailure)
    {
        this.store = store;
        this.log = log;
        this.reportFailure = reportFailure;
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
                log($"Lap persistence failed: {exception}");
                reportFailure(exception);
            }
        }
    }

    private void Execute(LapPersistenceCommand command)
    {
        if (command.Lap is { } lap)
        {
            store.SaveLap(lap);
            log($"Persisted lap {lap.Id}: total={lap.TotalSeconds:0.000}, samples={lap.Samples.Count}.");
            return;
        }

        if (command.DeleteLapId is Guid lapId)
        {
            store.DeleteLap(lapId);
            log($"Deleted lap {lapId}.");
            return;
        }

        if (command.DeleteTrackLapsId is not Guid trackId) return;
        store.DeleteTrackLaps(trackId, command.PerformanceClasses, command.PreserveLapIds);
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
    public static LapPersistenceCommand Save(LapRecord lap) => new(lap, null, null, null, null);
    public static LapPersistenceCommand Delete(Guid lapId) => new(null, lapId, null, null, null);
    public static LapPersistenceCommand DeleteTrack(
        Guid trackId,
        int[]? performanceClasses,
        Guid[] preserveLapIds) =>
        new(null, null, trackId, performanceClasses, preserveLapIds);
}
