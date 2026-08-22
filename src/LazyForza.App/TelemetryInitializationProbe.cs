using System.Net;
using System.Net.Sockets;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.App;

internal sealed record TelemetryProbeResult(
    IPEndPoint RemoteEndpoint,
    TelemetryFrame Frame,
    DateTimeOffset ReceivedAt);

internal sealed class TelemetryInitializationProbe
{
    public async Task<TelemetryProbeResult> WaitForTelemetryAsync(
        string listenAddress,
        int port,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(listenAddress, out var address))
            throw new ArgumentException("Listen address must be an IP literal.", nameof(listenAddress));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        using var client = new UdpClient(new IPEndPoint(address, port));
        var parser = new Fh6PacketParser();
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var receivedAt = DateTimeOffset.UtcNow;
            if (parser.TryParse(
                    packet.Buffer,
                    sequence++,
                    receivedAt,
                    TelemetrySourceKind.Live,
                    out var frame,
                    out _))
                return new TelemetryProbeResult(packet.RemoteEndPoint, frame!, receivedAt);
        }

        throw new OperationCanceledException(cancellationToken);
    }
}
