using System.Collections.Concurrent;
using System.Threading.Channels;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.Telemetry;

public sealed class TelemetryHub : ITelemetryFeed, ILiveTelemetryConfiguration
{
    private ITelemetrySource source;
    private TelemetryOptions options;
    private StreamStatistics statistics = new();
    private readonly ConcurrentDictionary<Guid, Channel<TelemetryFrame>> subscribers = new();
    private readonly object subscriberSync = new();
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private Channel<TelemetryFrame>[] subscriberSnapshot = [];
    private CancellationTokenSource? sourceCancellation;
    private Task? sourceTask;
    private TelemetryFrame? latest;
    private string? lastError;
    private bool sourceFailed;
    private bool disposed;

    public TelemetryHub(ITelemetrySource source, TelemetryOptions options)
    {
        this.source = source;
        this.options = options;
    }

    public TelemetryFrame? Latest => Volatile.Read(ref latest);

    public async ValueTask ChangeListenerAsync(string address, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!System.Net.IPAddress.TryParse(address, out var parsedAddress) || port is < 1 or > 65535)
            throw new ArgumentException("Invalid UDP endpoint.");
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (source is not UdpTelemetrySource) throw new InvalidOperationException("The current source is not live UDP.");
            var nextOptions = options with { ListenAddress = parsedAddress.ToString(), Port = port };
            if (nextOptions == options && !sourceFailed) return;
            var previousOptions = options;
            var candidate = new UdpTelemetrySource(nextOptions);
            var releasedPrevious = false;
            try
            {
                // Changing the bind address on the same port may require releasing our own socket first.
                if (port == options.Port)
                {
                    await StopListenerAsync().ConfigureAwait(false);
                    releasedPrevious = true;
                }
                candidate.PrepareListener(); // A busy new port leaves the existing feed untouched.
                if (!releasedPrevious) await StopListenerAsync().ConfigureAwait(false);
                source = candidate;
                options = nextOptions;
                statistics = new StreamStatistics();
                Volatile.Write(ref latest, null);
                lastError = null; sourceFailed = false;
                StartListener();
            }
            catch
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                if (releasedPrevious)
                {
                    source = new UdpTelemetrySource(previousOptions);
                    StartListener();
                }
                throw;
            }
        }
        finally { lifecycleLock.Release(); }
    }

    private void StartListener()
    {
        lastError = null; sourceFailed = false;
        sourceCancellation = new CancellationTokenSource();
        var token = sourceCancellation.Token;
        sourceTask = Task.Run(() => RunSourceAsync(token), CancellationToken.None);
    }

    private async Task StopListenerAsync()
    {
        sourceCancellation?.Cancel();
        if (sourceTask is not null) await sourceTask.ConfigureAwait(false);
        await source.DisposeAsync().ConfigureAwait(false);
        sourceCancellation?.Dispose(); sourceCancellation = null; sourceTask = null;
    }

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
                StartListener();
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
            sourceFailed = true;
            // Keep subscribers available so a corrected live endpoint can resume the same modules.
            if (source.Kind != TelemetrySourceKind.Live)
                foreach (var subscriber in Volatile.Read(ref subscriberSnapshot)) subscriber.Writer.TryComplete(ex);
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
        if (sourceFailed)
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

            await StopListenerAsync().ConfigureAwait(false);
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
