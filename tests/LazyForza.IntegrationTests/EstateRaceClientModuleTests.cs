using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.EstateRace;
using LazyForza.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateRaceClientModuleTests
{
    [TestMethod]
    public void TimingRunsOnlyDuringActiveCompetitiveSessionsAndHonorsFinalFlyingLap()
    {
        var localId = Guid.NewGuid();
        var local = Participant(localId) with { QualifyingFinalLapPending = true };
        var session = EmptySession() with { Participants = [local] };

        foreach (var phase in new[]
                 {
                     RaceSessionPhase.Lobby,
                     RaceSessionPhase.Grid,
                     RaceSessionPhase.OutLap,
                     RaceSessionPhase.FormationLap,
                     RaceSessionPhase.Countdown,
                     RaceSessionPhase.Suspended,
                     RaceSessionPhase.Finished
                 })
            Assert.IsFalse(EstateRaceModule.ShouldEnableRaceTiming(session with { Phase = phase }, localId), phase.ToString());

        Assert.IsTrue(EstateRaceModule.ShouldEnableRaceTiming(session with { Phase = RaceSessionPhase.Race }, localId));
        Assert.IsTrue(EstateRaceModule.ShouldEnableRaceTiming(session with
        {
            Phase = RaceSessionPhase.Qualifying,
            QualifyingTimeExpired = false
        }, localId));
        Assert.IsTrue(EstateRaceModule.ShouldEnableRaceTiming(session with
        {
            Phase = RaceSessionPhase.Qualifying,
            QualifyingTimeExpired = true
        }, localId));
        Assert.IsFalse(EstateRaceModule.ShouldEnableRaceTiming(session with
        {
            Phase = RaceSessionPhase.Qualifying,
            QualifyingTimeExpired = true,
            Participants = [local with { QualifyingFinalLapPending = false }]
        }, localId));

        Assert.IsTrue(EstateRaceModule.ShouldCancelLapOnPause(session with { Phase = RaceSessionPhase.Qualifying }));
        Assert.IsFalse(EstateRaceModule.ShouldCancelLapOnPause(session with { Phase = RaceSessionPhase.Race }));
    }

    [TestMethod]
    public async Task ConnectsWithPasswordProfileAndUploadsNormalizedTelemetryWithoutPersistingPassword()
    {
        var received = Channel.CreateUnbounded<RaceIncomingEnvelope>();
        var participantId = Guid.NewGuid();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var login = await ReceiveAsync(socket, context.RequestAborted);
            await received.Writer.WriteAsync(login, context.RequestAborted);
            var snapshot = new EstateRaceSession(
                1,
                "联机测试",
                RaceSessionPhase.Lobby,
                RaceControlFlag.Green,
                null,
                null,
                null,
                null,
                5,
                null,
                null,
                null,
                null,
                [],
                null,
                [],
                DateTimeOffset.UtcNow);
            var accepted = EstateRaceWireProtocol.Serialize(
                "loginAccepted",
                1,
                new RaceLoginAccepted(participantId, "resume-test-token", snapshot, DateTimeOffset.UtcNow));
            await socket.SendAsync(accepted, WebSocketMessageType.Text, true, context.RequestAborted);
            try
            {
                while (socket.State == WebSocketState.Open)
                    await received.Writer.WriteAsync(await ReceiveAsync(socket, context.RequestAborted), context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-race-client-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var feed = new TestFeed();
            var track = CreateTrack();
            var definition = CreateDefinition(track);
            var completed = new EstateCompletedLapEvent(Guid.NewGuid(), 1, 62.5, [15, 16, 15.5, 16], true, null);
            var module = new EstateRaceModule(() => new EstateRaceTrackContext(
                track,
                definition,
                12.5,
                1,
                2,
                true,
                completed));
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                await module.ConnectAsync(new EstateRaceConnectionProfile(
                    address,
                    "secret-race-password",
                    "测试车手",
                    "#42D7E8",
                    "远山车队"), CancellationToken.None);
                Assert.AreEqual(EstateRaceConnectionState.Connected, module.State.ConnectionState);
                Assert.AreEqual(participantId, module.State.LocalParticipantId);

                var loginEnvelope = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual("login", loginEnvelope.Type);
                var login = loginEnvelope.Payload.Deserialize<RaceLoginRequest>(EstateRaceWireProtocol.JsonOptions);
                Assert.IsNotNull(login);
                Assert.AreEqual("secret-race-password", login.Password);
                Assert.AreEqual("测试车手", login.DisplayName);
                Assert.AreEqual("远山车队", login.TeamName);
                Assert.IsNull(await store.GetAsync(EstateRaceModule.ModuleId, "password", CancellationToken.None));

                feed.Publish(Frame(1, 10_000, 50, 2, 3));
                RaceIncomingEnvelope telemetryEnvelope;
                do
                {
                    telemetryEnvelope = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.AreNotEqual(
                        "lapCompleted",
                        telemetryEnvelope.Type,
                        "Initial login must not upload the last lap that existed before joining this race server.");
                } while (telemetryEnvelope.Type != "telemetry");
                var telemetry = telemetryEnvelope.Payload.Deserialize<RaceTelemetryUpdate>(EstateRaceWireProtocol.JsonOptions);
                Assert.IsNotNull(telemetry);
                Assert.IsTrue(telemetry.IsTelemetryValid);
                Assert.AreEqual(1, telemetry.CompletedLaps);
                Assert.IsTrue(telemetry.TrackProgress is > 0 and < 1);
                Assert.IsTrue(telemetry.MapX is >= 0 and <= 1);
                Assert.IsTrue(telemetry.MapY is >= 0 and <= 1);

                feed.Publish(Frame(2, 0, 10_000, 0, 10_000, DateTimeOffset.UtcNow.AddMilliseconds(250)));
                RaceTelemetryUpdate pausedTelemetry;
                do
                {
                    var pausedEnvelope = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    if (pausedEnvelope.Type != "telemetry") continue;
                    pausedTelemetry = pausedEnvelope.Payload.Deserialize<RaceTelemetryUpdate>(EstateRaceWireProtocol.JsonOptions)!;
                    break;
                } while (true);
                Assert.IsFalse(pausedTelemetry.IsTelemetryValid);
                Assert.IsTrue(pausedTelemetry.IsPausedOrRewinding);
                Assert.AreEqual(telemetry.TrackProgress, pausedTelemetry.TrackProgress, 0.000001);
                Assert.AreEqual(telemetry.MapX, pausedTelemetry.MapX, 0.000001);
                Assert.AreEqual(telemetry.MapY, pausedTelemetry.MapY, 0.000001);
                Assert.AreEqual(string.Empty, module.ActiveProfile?.Password);
                await module.DisconnectAsync();
                Assert.IsNull(module.ActiveProfile);
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task AutomaticallyReconnectsWithSavedResumeTokenAfterSocketDrop()
    {
        var participantId = Guid.NewGuid();
        var connectionCount = 0;
        string? resumedWith = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var number = Interlocked.Increment(ref connectionCount);
            var loginEnvelope = await ReceiveAsync(socket, context.RequestAborted);
            var login = loginEnvelope.Payload.Deserialize<RaceLoginRequest>(EstateRaceWireProtocol.JsonOptions)!;
            if (number > 1) resumedWith = login.ResumeToken;
            await socket.SendAsync(
                EstateRaceWireProtocol.Serialize(
                    "loginAccepted",
                    number,
                    new RaceLoginAccepted(participantId, "resume-reconnect-token", EmptySession(), DateTimeOffset.UtcNow)),
                WebSocketMessageType.Text,
                true,
                context.RequestAborted);
            if (number == 1)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "test drop", context.RequestAborted);
                return;
            }
            try
            {
                while (socket.State == WebSocketState.Open)
                    _ = await ReceiveAsync(socket, context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-race-reconnect-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var feed = new TestFeed();
            var track = CreateTrack();
            var definition = CreateDefinition(track);
            var module = new EstateRaceModule(() => new EstateRaceTrackContext(
                track, definition, 0, 0, 0, true, null));
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                await module.ConnectAsync(new EstateRaceConnectionProfile(
                    address, "secret-race-password", "重连车手", "#42D7E8", null), CancellationToken.None);
                await WaitUntilAsync(
                    () => Volatile.Read(ref connectionCount) >= 2 && module.State.ConnectionState == EstateRaceConnectionState.Connected,
                    TimeSpan.FromSeconds(6));
                Assert.AreEqual("resume-reconnect-token", resumedWith);
                Assert.AreEqual(participantId, module.State.LocalParticipantId);
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
            DeleteDatabase(path);
        }
    }

    [TestMethod]
    public async Task MarksTelemetryGapAndTimestampRegressionAsPausedOrRewinding()
    {
        var received = Channel.CreateUnbounded<RaceIncomingEnvelope>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _ = await ReceiveAsync(socket, context.RequestAborted);
            await socket.SendAsync(
                EstateRaceWireProtocol.Serialize(
                    "loginAccepted",
                    1,
                    new RaceLoginAccepted(Guid.NewGuid(), "resume-validity-token", EmptySession(), DateTimeOffset.UtcNow)),
                WebSocketMessageType.Text,
                true,
                context.RequestAborted);
            try
            {
                while (socket.State == WebSocketState.Open)
                    await received.Writer.WriteAsync(await ReceiveAsync(socket, context.RequestAborted), context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-estate-race-validity-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new LazyForzaStore(path);
            var feed = new TestFeed();
            var track = CreateTrack();
            var definition = CreateDefinition(track);
            var module = new EstateRaceModule(() => new EstateRaceTrackContext(
                track, definition, 12.5, 0, 2, true, null));
            await module.InitializeAsync(new TestContext(feed, store), CancellationToken.None);
            await module.StartAsync(CancellationToken.None);
            try
            {
                await module.ConnectAsync(new EstateRaceConnectionProfile(
                    address, "secret-race-password", "有效性车手", "#42D7E8", null), CancellationToken.None);
                var firstArrival = DateTimeOffset.UtcNow;
                feed.Publish(Frame(1, 10_000, 50, 2, 3, firstArrival));
                feed.Publish(Frame(2, 11_000, 51, 2, 3, firstArrival.AddSeconds(3)));
                feed.Publish(Frame(3, 8_000, 52, 2, 3, firstArrival.AddSeconds(3.2)));

                var updates = new List<RaceTelemetryUpdate>();
                while (updates.Count < 3)
                {
                    var envelope = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    if (envelope.Type == "telemetry")
                        updates.Add(envelope.Payload.Deserialize<RaceTelemetryUpdate>(EstateRaceWireProtocol.JsonOptions)!);
                }
                Assert.IsTrue(updates[0].IsTelemetryValid);
                Assert.IsTrue(updates[1].IsPausedOrRewinding);
                Assert.IsFalse(updates[1].IsTelemetryValid);
                Assert.IsTrue(updates[2].IsPausedOrRewinding);
                Assert.IsFalse(updates[2].IsTelemetryValid);
            }
            finally
            {
                await module.DisposeAsync();
                await feed.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
            DeleteDatabase(path);
        }
    }

    private static TrackTemplate CreateTrack()
    {
        var points = Enumerable.Range(0, 181)
            .Select(index =>
            {
                var angle = index * Math.PI * 2 / 180;
                return new TrackPoint(100 * Math.Cos(angle), 2, 100 * Math.Sin(angle), 0, 0, 0);
            })
            .ToArray();
        return TrackAlgorithms.BuildTemplate("联机测试环道", points) with
        {
            Source = TelemetryDataPartition.TrackSource(TelemetrySourceKind.Live),
            TimingKind = TrackTimingKind.EstateGeometry,
            Category = "地产环道",
            CaptureLapCount = 2
        };
    }

    private static EstateTrackDefinition CreateDefinition(TrackTemplate track) => new(
        track.Id,
        track.Name,
        "test",
        "race-client-test",
        "1",
        new EstateTimingGate(
            new EstateGatePoint(88, 2, 0),
            new EstateGatePoint(112, 2, 0),
            0,
            1,
            0,
            0,
            0),
        EstateTrackAlgorithms.CreateCheckpoints(track, 4),
        null,
        60,
        60,
        1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static EstateRaceSession EmptySession() => new(
        1,
        "联机测试",
        RaceSessionPhase.Lobby,
        RaceControlFlag.Green,
        null,
        null,
        null,
        null,
        5,
        null,
        null,
        null,
        null,
        [],
        null,
        [],
        DateTimeOffset.UtcNow);

    private static EstateRaceParticipant Participant(Guid id) => new(
        id,
        1,
        "测试车手",
        "#42D7E8",
        null,
        RaceParticipantStatus.OnTrack,
        true,
        false,
        0,
        0,
        .5,
        .5,
        .5,
        120,
        30,
        null,
        null,
        null,
        null,
        false,
        false,
        0,
        false,
        0,
        RaceGripCondition.Unknown,
        [],
        [],
        DateTimeOffset.UtcNow);

    private static TelemetryFrame Frame(
        long sequence,
        uint timestamp,
        double x,
        double y,
        double z,
        DateTimeOffset? arrivalTime = null)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = timestamp,
            Position = new Vector3F((float)x, (float)y, (float)z),
            Speed = 25,
            TireCombinedSlip = new WheelValues(0.15f, 0.16f, 0.14f, 0.15f),
            TireSlipRatio = new WheelValues(0.08f, 0.09f, 0.07f, 0.08f)
        };
        return new TelemetryFrame(
            sequence,
            arrivalTime ?? DateTimeOffset.UtcNow,
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(90, 55.9, 100, 0.5, 0, 0, 0, 0.5, default),
            ReadOnlyMemory<byte>.Empty);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var expiresAt = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= expiresAt)
                Assert.Fail("等待地产赛事客户端状态变化超时。");
            await Task.Delay(50);
        }
    }

    private static async Task<RaceIncomingEnvelope> ReceiveAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Client closed.");
            writer.Write(buffer.AsSpan(0, received.Count));
            if (received.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<RaceIncomingEnvelope>(writer.WrittenSpan, EstateRaceWireProtocol.JsonOptions)!;
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }

    private sealed record TestContext(TestFeed Feed, LazyForzaStore Store) : IModuleContext
    {
        public ITelemetryFeed Telemetry => Feed;
        public IHudHost Hud { get; } = new EmptyHud();
        public IModuleSettingsStore Settings => Store;
        public IAnalysisStore AnalysisStore => Store;
        public Action<string> Log => _ => { };
    }

    private sealed class EmptyHud : IHudHost
    {
        public ValueTask AttachAsync(IHudContribution contribution, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DetachAsync(string contributionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SetLayoutAsync(OverlayLayout layout, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class TestFeed : ITelemetryFeed
    {
        private readonly Channel<TelemetryFrame> channel = Channel.CreateUnbounded<TelemetryFrame>();
        public TelemetryFrame? Latest { get; private set; }
        public TelemetryDiagnostics Diagnostics => new("test", 0, TelemetryStreamState.Live, 0, 0, 0, 0, 0, 0, 0, Latest?.ArrivalTime, null);
        public ValueTask<ITelemetrySubscription> SubscribeAsync(string consumerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ITelemetrySubscription>(new Subscription(channel.Reader));
        public void Publish(TelemetryFrame frame)
        {
            Latest = frame;
            channel.Writer.TryWrite(frame);
        }
        public ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
        private sealed record Subscription(ChannelReader<TelemetryFrame> Frames) : ITelemetrySubscription
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
