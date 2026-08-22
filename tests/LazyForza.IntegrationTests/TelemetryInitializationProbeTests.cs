using System.Net;
using System.Net.Sockets;
using LazyForza.App;
using LazyForza.Telemetry;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class TelemetryInitializationProbeTests
{
    [TestMethod]
    public async Task IgnoresUnrelatedDatagramsAndCompletesOnValidFh6Packet()
    {
        var port = ReserveUdpPort();
        var probe = new TelemetryInitializationProbe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var waiting = probe.WaitForTelemetryAsync("127.0.0.1", port, timeout.Token);
        await Task.Delay(80, timeout.Token);
        using var sender = new UdpClient();
        await sender.SendAsync(
            new ReadOnlyMemory<byte>([1, 2, 3]),
            new IPEndPoint(IPAddress.Loopback, port),
            timeout.Token);
        await sender.SendAsync(
            new ReadOnlyMemory<byte>(Fh6PacketBuilder.BuildDemoPacket(12)),
            new IPEndPoint(IPAddress.Loopback, port),
            timeout.Token);

        var result = await waiting;

        Assert.AreEqual(324, result.Frame.RawPacket.Length);
        Assert.AreEqual(LazyForza.Domain.TelemetrySourceKind.Live, result.Frame.Source);
    }

    private static int ReserveUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}
