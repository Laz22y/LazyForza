using System.Net;
using System.Net.Sockets;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Telemetry;

namespace LazyForza.Telemetry.Tests;

[TestClass]
public sealed class ListenerReconfigurationTests
{
    [TestMethod]
    public async Task ChangingPortKeepsSubscriptionAndReleasesOldSocket()
    {
        var options = new TelemetryOptions("127.0.0.1", FreePort());
        await using var hub = new TelemetryHub(new UdpTelemetrySource(options), options);
        await using var subscription = await hub.SubscribeAsync("test", CancellationToken.None);
        await Receive(hub, subscription, options.Port);
        var next = FreePort();
        await hub.ChangeListenerAsync("127.0.0.1", next, CancellationToken.None);
        Assert.AreEqual(next, hub.Diagnostics.ListenPort);
        Assert.IsNull(hub.Latest);
        using (Bind(options.Port)) { }
        await Receive(hub, subscription, next);
        await subscription.DisposeAsync();
        using (Bind(next)) { }
    }

    [TestMethod]
    public async Task OccupiedNewPortLeavesExistingConnectionAvailable()
    {
        var options = new TelemetryOptions("127.0.0.1", FreePort());
        await using var hub = new TelemetryHub(new UdpTelemetrySource(options), options);
        await using var subscription = await hub.SubscribeAsync("test", CancellationToken.None);
        await Receive(hub, subscription, options.Port);
        using var occupied = Bind(0);
        var next = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        await Assert.ThrowsExceptionAsync<SocketException>(() => hub.ChangeListenerAsync("127.0.0.1", next, CancellationToken.None).AsTask());
        Assert.AreEqual(options.Port, hub.Diagnostics.ListenPort);
        await Receive(hub, subscription, options.Port);
        occupied.Dispose();
        await hub.ChangeListenerAsync("127.0.0.1", next, CancellationToken.None);
        await Receive(hub, subscription, next);
    }

    [TestMethod]
    public async Task InvalidSamePortAddressRollsBackAndWildcardChangeWorks()
    {
        var options = new TelemetryOptions("127.0.0.1", FreePort());
        await using var hub = new TelemetryHub(new UdpTelemetrySource(options), options);
        await using var subscription = await hub.SubscribeAsync("test", CancellationToken.None);
        await Receive(hub, subscription, options.Port);
        await Assert.ThrowsExceptionAsync<SocketException>(() => hub.ChangeListenerAsync("192.0.2.123", options.Port, CancellationToken.None).AsTask());
        Assert.AreEqual("127.0.0.1:" + options.Port, hub.Diagnostics.ListenAddress);
        await Receive(hub, subscription, options.Port);
        await hub.ChangeListenerAsync("0.0.0.0", options.Port, CancellationToken.None);
        await Receive(hub, subscription, options.Port);
        Assert.AreEqual("0.0.0.0:" + options.Port, hub.Diagnostics.ListenAddress);
    }

    [TestMethod]
    public async Task InitialBindFailureCanRecoverWithTheSameSubscribers()
    {
        using var occupied = Bind(0);
        var port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        var options = new TelemetryOptions("127.0.0.1", port);
        await using var hub = new TelemetryHub(new UdpTelemetrySource(options), options);
        await using var subscription = await hub.SubscribeAsync("test", CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (hub.Diagnostics.State != TelemetryStreamState.Faulted) await Task.Delay(10, timeout.Token);
        Assert.IsFalse(subscription.Frames.Completion.IsCompleted);
        occupied.Dispose();
        await hub.ChangeListenerAsync("127.0.0.1", port, timeout.Token);
        await Receive(hub, subscription, port);
    }

    private static UdpClient Bind(int port)
    {
        var client = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
        client.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
        return client;
    }

    private static int FreePort()
    {
        using var socket = Bind(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task Receive(TelemetryHub hub, ITelemetrySubscription subscription, int port)
    {
        while (subscription.Frames.TryRead(out _)) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var sender = new UdpClient();
        var packet = Fh6PacketBuilder.BuildDemoPacket(42);
        while (true)
        {
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
            await Task.Delay(20, timeout.Token);
            if (subscription.Frames.TryRead(out var frame))
            {
                Assert.AreEqual(TelemetrySourceKind.Live, frame.Source);
                Assert.AreEqual(TelemetryStreamState.Live, hub.Diagnostics.State);
                return;
            }
        }
    }
}
