using System.Collections.Concurrent;
using System.Threading.Channels;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Telemetry;

public sealed class TelemetryHub : ITelemetryFeed
{
    private readonly ITelemetrySource source;
    private readonly TelemetryOptions options;
    private readonly StreamStatistics statistics = new();
    private readonly ConcurrentDictionary<Guid, Channel<TelemetryFrame>> subscribers = new();
    private readonly object subscriberSync = new();
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private Channel<TelemetryFrame>[] subscriberSnapshot = [];
    private CancellationTokenSource? sourceCancellation;
    private Task? sourceTask;
    private TelemetryFrame? latest;
    private string? lastError;
    private bool disposed;

    public TelemetryHub(ITelemetrySource source, TelemetryOptions options)
    {
        this.source = source;
        this.options = options;
    }

    public TelemetryFrame? Latest => Volatile.Read(ref latest);

    public TelemetryDiagnostics Diagnostics
    {
        get
        {
            var lastPacket = statistics.LastPacketAt;
            var state = SourceState(lastPacket);
            return new TelemetryDiagnostics(
                source.Description,
                options.Port,
                state,
                statistics.ValidPackets,
                statistics.InvalidPackets,
                statistics.EstimatedDroppedPackets,
                statistics.DuplicatePackets,
                statistics.OutOfOrderPackets,
                statistics.TimestampWraps,
                statistics.PacketsPerSecond,
                lastPacket,
                lastError);
        }
    }

    public async ValueTask<ITelemetrySubscription> SubscribeAsync(string consumerId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        var channel = Channel.CreateBounded<TelemetryFrame>(new BoundedChannelOptions(Math.Max(1, options.SubscriberCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var id = Guid.NewGuid();
        lock (subscriberSync)
        {
            subscribers[id] = channel;
            RefreshSubscriberSnapshot();
        }

        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sourceTask is null)
            {
                sourceCancellation = new CancellationTokenSource();
                sourceTask = Task.Run(() => RunSourceAsync(sourceCancellation.Token), CancellationToken.None);
            }
        }
        finally
        {
            lifecycleLock.Release();
        }

        if (Latest is { } current)
        {
            channel.Writer.TryWrite(current);
        }

        return new Subscription(this, id, channel.Reader);
    }

    private async Task RunSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await source.RunAsync(PublishAsync, OnInvalid, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            foreach (var subscriber in Volatile.Read(ref subscriberSnapshot))
            {
                subscriber.Writer.TryComplete(ex);
            }
        }
    }

    private ValueTask PublishAsync(TelemetryFrame frame)
    {
        Volatile.Write(ref latest, frame);
        statistics.OnPacket(frame);
        foreach (var subscriber in Volatile.Read(ref subscriberSnapshot))
        {
            subscriber.Writer.TryWrite(frame);
        }

        return ValueTask.CompletedTask;
    }

    private void OnInvalid(string error)
    {
        statistics.OnInvalid();
        lastError = error;
    }

    private TelemetryStreamState SourceState(DateTimeOffset? lastPacket)
    {
        if (lastError is not null && sourceTask?.IsFaulted == true)
        {
            return TelemetryStreamState.Faulted;
        }

        if (sourceTask is null)
        {
            return TelemetryStreamState.Disconnected;
        }

        if (lastPacket is null)
        {
            return TelemetryStreamState.Connecting;
        }

        if (DateTimeOffset.UtcNow - lastPacket > options.EffectiveStaleAfter)
        {
            return TelemetryStreamState.Stale;
        }

        return source.Kind == TelemetrySourceKind.Live ? TelemetryStreamState.Live : TelemetryStreamState.Replay;
    }

    private async ValueTask RemoveAsync(Guid id)
    {
        Channel<TelemetryFrame>? channel;
        lock (subscriberSync)
        {
            subscribers.TryRemove(id, out channel);
            RefreshSubscriberSnapshot();
        }
        if (channel is not null)
        {
            channel.Writer.TryComplete();
        }

        if (!subscribers.IsEmpty)
        {
            return;
        }

        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!subscribers.IsEmpty || sourceTask is null)
            {
                return;
            }

            sourceCancellation?.Cancel();
            try
            {
                await sourceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            sourceCancellation?.Dispose();
            sourceCancellation = null;
            sourceTask = null;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private void RefreshSubscriberSnapshot()
    {
        Volatile.Write(ref subscriberSnapshot, subscribers.Values.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var id in subscribers.Keys)
        {
            await RemoveAsync(id).ConfigureAwait(false);
        }

        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            sourceCancellation?.Cancel();
            if (sourceTask is not null)
            {
                try { await sourceTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            lifecycleLock.Release();
        }

        await source.DisposeAsync().ConfigureAwait(false);
        sourceCancellation?.Dispose();
        lifecycleLock.Dispose();
    }

    private sealed class Subscription(TelemetryHub owner, Guid id, ChannelReader<TelemetryFrame> frames) : ITelemetrySubscription
    {
        private int disposed;
        public ChannelReader<TelemetryFrame> Frames { get; } = frames;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await owner.RemoveAsync(id).ConfigureAwait(false);
            }
        }
    }
}
