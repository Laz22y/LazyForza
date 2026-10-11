using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.Telemetry.Tests;

[TestClass]
public sealed class TelemetryForwardingTests
{
    [TestMethod]
    public async Task LiveUdpPreservesEveryByteAndCanRetargetAndStopWithoutModules()
    {
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new TelemetryOptions("127.0.0.1", FreePort());
        var source = new UdpTelemetrySource(options); source.PrepareListener();
        await using var hub = new TelemetryHub(source, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await hub.ChangeForwardingAsync(new(true, "127.0.0.1", Port(first)), timeout.Token);
        using var sender = new UdpClient();
        var input = new IPEndPoint(IPAddress.Loopback, options.Port);
        // Invalid packets do not reach the forwarding queue.
        await sender.SendAsync(new byte[123], input, timeout.Token);
        var packet = Fh6PacketBuilder.BuildDemoPacket(41); packet[323] = 0xA7;
        await sender.SendAsync(packet, input, timeout.Token);
        CollectionAssert.AreEqual(packet, (await first.ReceiveAsync(timeout.Token)).Buffer);
        Assert.AreEqual(1L, hub.Diagnostics.InvalidPackets);

        // Removing the last business consumer must not stop forwarding.
        var subscription = await hub.SubscribeAsync("temporary", timeout.Token);
        await subscription.DisposeAsync();
        await hub.ChangeForwardingAsync(new(true, "127.0.0.1", Port(second)), timeout.Token);
        packet = Fh6PacketBuilder.BuildDemoPacket(42); packet[323] = 0xF1;
        await sender.SendAsync(packet, input, timeout.Token);
        CollectionAssert.AreEqual(packet, (await second.ReceiveAsync(timeout.Token)).Buffer);
        Assert.AreEqual(0, first.Available);
        await hub.ChangeForwardingAsync(new(), timeout.Token);
        Assert.IsFalse(hub.ForwardingStatus.Enabled);
        Assert.AreEqual(TelemetryStreamState.Disconnected, hub.Diagnostics.State);
        using var released = new UdpClient(new IPEndPoint(IPAddress.Loopback, options.Port));
    }

    [TestMethod]
    public async Task OnlyLiveCompletePacketsAreForwarded()
    {
        var received = new ConcurrentQueue<byte[]>();
        await using var forwarder = new TelemetryForwarder((bytes, _, _) =>
        { received.Enqueue(bytes.ToArray()); return ValueTask.FromResult(bytes.Length); });
        await forwarder.ConfigureAsync(new(true), 2299);
        var frame = Frame(1);
        forwarder.Publish(frame with { Source = TelemetrySourceKind.Simulator });
        forwarder.Publish(frame with { Source = TelemetrySourceKind.Replay });
        forwarder.Publish(frame with { RawPacket = new byte[323] });
        forwarder.Publish(frame);
        await Until(() => forwarder.Status.Sent == 1);
        await forwarder.DisposeAsync();
        Assert.AreEqual(1, received.Count);
        CollectionAssert.AreEqual(frame.RawPacket.ToArray(), received.Single());
    }

    [TestMethod]
    public async Task StalledDestinationKeepsQueueBoundedAndRetainsNewestPackets()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<byte>();
        await using var forwarder = new TelemetryForwarder(async (bytes, _, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            received.Enqueue(bytes.Span[323]); return bytes.Length;
        });
        await forwarder.ConfigureAsync(new(true), 2299);
        forwarder.Publish(Frame(0)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 1; i <= 200; i++) forwarder.Publish(Frame((byte)i));
        Assert.AreEqual(136L, forwarder.Status.Dropped);
        Assert.AreEqual(0L, forwarder.Status.Sent);
        release.SetResult(); await Until(() => forwarder.Status.Sent == 65);
        CollectionAssert.AreEqual(new byte[] { 0 }.Concat(Enumerable.Range(137, 64).Select(i => (byte)i)).ToArray(), received.ToArray());
    }

    [TestMethod]
    public async Task DisablingCancelsAnInFlightSend()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var forwarder = new TelemetryForwarder(async (_, _, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; });
        await forwarder.ConfigureAsync(new(true), 2299);
        forwarder.Publish(Frame(1)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await forwarder.ConfigureAsync(new(), 2299).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(forwarder.Status.Enabled);
    }

    [TestMethod]
    public async Task SocketFailureDoesNotEndWorkerAndLaterPacketsRecover()
    {
        var attempts = 0;
        await using var forwarder = new TelemetryForwarder((bytes, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new SocketException((int)SocketError.NetworkUnreachable);
            return ValueTask.FromResult(bytes.Length);
        });
        await forwarder.ConfigureAsync(new(true), 2299); forwarder.Publish(Frame(1));
        await Until(() => forwarder.Status.Errors == 1);
        Assert.AreEqual(SocketError.NetworkUnreachable, forwarder.Status.LastError);
        forwarder.Publish(Frame(2)); await Until(() => forwarder.Status.Sent == 1);
        Assert.IsNull(forwarder.Status.LastError);
        Assert.AreEqual(1L, forwarder.Status.Errors);
    }

    [TestMethod]
    public async Task InvalidTargetsAndListenerLoopsLeaveExistingConfigurationActive()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new TelemetryOptions("0.0.0.0", FreePort());
        await using var hub = new TelemetryHub(new UdpTelemetrySource(options), options);
        var valid = new TelemetryForwardingOptions(true, "127.0.0.1", Port(sink));
        await hub.ChangeForwardingAsync(valid, CancellationToken.None);
        foreach (var invalid in new[]
        {
            valid with { Address = "127.0.0.1", Port = options.Port },
            valid with { Address = "::ffff:127.0.0.1", Port = options.Port },
            valid with { Address = "0.0.0.0" }, valid with { Address = "::" },
            valid with { Address = "255.255.255.255" }, valid with { Address = "239.1.2.3" },
            valid with { Address = "ff02::1" }, valid with { Address = "invalid" }, valid with { Port = 0 }
        })
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => hub.ChangeForwardingAsync(invalid, CancellationToken.None).AsTask());
            Assert.AreEqual(valid, hub.ForwardingOptions);
        }
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => hub.ChangeListenerAsync("0.0.0.0", valid.Port, CancellationToken.None).AsTask());
        Assert.AreEqual(options.Port, hub.Diagnostics.ListenPort);
    }

    [TestMethod]
    public async Task Ipv6TargetPreservesPacket()
    {
        if (!Socket.OSSupportsIPv6) Assert.Inconclusive("IPv6 unavailable on this host.");
        using var sink = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        await using var forwarder = new TelemetryForwarder();
        await forwarder.ConfigureAsync(new(true, "::1", Port(sink)), 2299);
        var frame = Frame(254); forwarder.Publish(frame);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(frame.RawPacket.ToArray(), (await sink.ReceiveAsync(timeout.Token)).Buffer);
    }

    [TestMethod]
    public void DiscouragedPortsAreValidWhileInvalidEndpointsRemainInvalid()
    {
        foreach (var port in new[] { 1, 5199, 5200, 5250, 5300, 5301, 65535 })
        {
            Assert.IsTrue(TelemetryNetworkPolicy.TryParseListener("127.0.0.1", port.ToString(), out _, out var parsed));
            Assert.AreEqual(port, parsed);
            Assert.AreEqual(port is >= 5200 and <= 5300, TelemetryNetworkPolicy.IsDiscouragedPort(parsed));
        }
        foreach (var port in new[] { "0", "65536", "-1", "text" })
            Assert.IsFalse(TelemetryNetworkPolicy.TryParseListener("127.0.0.1", port, out _, out _));
        Assert.IsFalse(TelemetryNetworkPolicy.TryParseListener("invalid", "2299", out _, out _));
    }

    private static TelemetryFrame Frame(byte marker)
    {
        var packet = Fh6PacketBuilder.BuildDemoPacket(marker); packet[323] = marker;
        Assert.IsTrue(new Fh6PacketParser().TryParse(packet, marker, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out var frame, out _));
        return frame!;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static int Port(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    private static int FreePort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return Port(socket); }
}
