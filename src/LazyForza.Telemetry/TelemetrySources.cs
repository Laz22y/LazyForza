using System.Net;
using System.Net.Sockets;
using LazyForza.Domain;

namespace LazyForza.Telemetry;

public interface ITelemetrySource : IAsyncDisposable
{
    TelemetrySourceKind Kind { get; }
    string Description { get; }
    Task RunAsync(Func<TelemetryFrame, ValueTask> publish, Action<string> onInvalid, CancellationToken cancellationToken);
}

public sealed class SimulatorTelemetrySource(int hertz = 60) : ITelemetrySource
{
    private readonly Fh6PacketParser parser = new();

    public TelemetrySourceKind Kind => TelemetrySourceKind.Simulator;
    public string Description => $"Demo/Replay deterministic simulator ({hertz} Hz)";

    public async Task RunAsync(Func<TelemetryFrame, ValueTask> publish, Action<string> onInvalid, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / hertz));
        long sequence = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var packet = Fh6PacketBuilder.BuildDemoPacket(sequence, hertz);
            if (parser.TryParse(packet, sequence, DateTimeOffset.UtcNow, Kind, out var frame, out var error))
            {
                await publish(frame!).ConfigureAwait(false);
            }
            else
            {
                onInvalid(error ?? "Unknown simulator parse error.");
            }

            sequence++;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class UdpTelemetrySource(TelemetryOptions options) : ITelemetrySource
{
    private readonly Fh6PacketParser parser = new();
    private UdpClient? client;

    public TelemetrySourceKind Kind => TelemetrySourceKind.Live;
    public string Description => $"{options.ListenAddress}:{options.Port}";

    public async Task RunAsync(Func<TelemetryFrame, ValueTask> publish, Action<string> onInvalid, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(options.ListenAddress, out var address))
        {
            throw new ArgumentException("ListenAddress must be an IP literal.", nameof(options));
        }

        client = new UdpClient(new IPEndPoint(address, options.Port));
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (parser.TryParse(result.Buffer, sequence++, DateTimeOffset.UtcNow, Kind, out var frame, out var error))
            {
                await publish(frame!).ConfigureAwait(false);
            }
            else
            {
                onInvalid(error ?? "Invalid FH6 packet.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        client = null;
        return ValueTask.CompletedTask;
    }
}
