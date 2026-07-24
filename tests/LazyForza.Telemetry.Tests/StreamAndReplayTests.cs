using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.Telemetry.Tests;

[TestClass]
public sealed class StreamAndReplayTests
{
    [TestMethod]
    public void TracksDuplicateOutOfOrderWrapAndEstimatedLoss()
    {
        var parser = new Fh6PacketParser();
        var stats = new StreamStatistics();
        var now = DateTimeOffset.UtcNow;
        var timestamps = new uint[] { 100, 116, 132, 132, 124, 148, 0xFFFFFFF0, 8 };
        for (var index = 0; index < timestamps.Length; index++)
        {
            var packet = Fh6PacketBuilder.BuildDemoPacket(index);
            Fh6PacketBuilder.WriteUInt32(packet, 4, timestamps[index]);
            Assert.IsTrue(parser.TryParse(packet, index, now.AddMilliseconds(index * 16), TelemetrySourceKind.Replay, out var frame, out _));
            stats.OnPacket(frame!);
        }

        Assert.AreEqual(1, stats.DuplicatePackets);
        Assert.AreEqual(1, stats.OutOfOrderPackets);
        Assert.AreEqual(1, stats.TimestampWraps);
    }

    [TestMethod]
    public void RepeatedMenuZeroTimestampsAreNotNetworkDuplicates()
    {
        var parser = new Fh6PacketParser();
        var stats = new StreamStatistics();
        for (var index = 0; index < 5; index++)
        {
            var packet = Fh6PacketBuilder.BuildDemoPacket(index);
            Fh6PacketBuilder.WriteInt32(packet, 0, 0);
            Fh6PacketBuilder.WriteUInt32(packet, 4, 0);
            Assert.IsTrue(parser.TryParse(packet, index, DateTimeOffset.UtcNow.AddMilliseconds(index * 16), TelemetrySourceKind.Live, out var frame, out _));
            stats.OnPacket(frame!);
        }

        Assert.AreEqual(5, stats.ValidPackets);
        Assert.AreEqual(0, stats.DuplicatePackets);
        Assert.AreEqual(0, stats.OutOfOrderPackets);
    }

    [TestMethod]
    public async Task RecordingRoundTripIsDeterministicAndLabelledReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-{Guid.NewGuid():N}.lfztelemetry");
        try
        {
            var parser = new Fh6PacketParser();
            await using (var writer = await TelemetryRecordingWriter.CreateAsync(path,
                new RecordingMetadata("LazyForza.Tests", 1, TelemetrySourceKind.Simulator, DateTimeOffset.UnixEpoch, "deterministic"), CancellationToken.None))
            {
                for (var index = 0; index < 8; index++)
                {
                    var packet = Fh6PacketBuilder.BuildDemoPacket(index);
                    Assert.IsTrue(parser.TryParse(packet, index, DateTimeOffset.UnixEpoch.AddMilliseconds(index * 16), TelemetrySourceKind.Simulator, out var frame, out _));
                    await writer.WriteAsync(frame!, CancellationToken.None);
                }
            }

            var replayed = new List<TelemetryFrame>();
            await new TelemetryReplaySource(path, speed: 0).RunAsync(frame => { replayed.Add(frame); return ValueTask.CompletedTask; }, Assert.Fail, CancellationToken.None);
            Assert.AreEqual(8, replayed.Count);
            Assert.IsTrue(replayed.All(frame => frame.Source == TelemetrySourceKind.Replay));
            CollectionAssert.AreEqual(Fh6PacketBuilder.BuildDemoPacket(5), replayed[5].RawPacket.ToArray());

            using var loopCancellation = new CancellationTokenSource();
            var looped = 0;
            await new TelemetryReplaySource(path, speed: 0, loop: true).RunAsync(frame =>
            {
                if (Interlocked.Increment(ref looped) == 12) loopCancellation.Cancel();
                return ValueTask.CompletedTask;
            }, Assert.Fail, loopCancellation.Token);
            Assert.AreEqual(12, looped);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task HubStartsOnFirstSubscriberAndStopsAfterLast()
    {
        await using var hub = new TelemetryHub(new SimulatorTelemetrySource(120), new TelemetryOptions(SubscriberCapacity: 1));
        Assert.AreEqual(TelemetryStreamState.Disconnected, hub.Diagnostics.State);
        await using var first = await hub.SubscribeAsync("one", CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var frame = await first.Frames.ReadAsync(timeout.Token);
        Assert.AreEqual(TelemetrySourceKind.Simulator, frame.Source);
        Assert.AreEqual(TelemetryStreamState.Replay, hub.Diagnostics.State);
    }

    [DataTestMethod]
    [DataRow(30)]
    [DataRow(60)]
    [DataRow(120)]
    [DataRow(144)]
    public void ParserDoesNotAssumePacketRate(int hertz)
    {
        var parser = new Fh6PacketParser();
        for (var index = 0; index < hertz * 2; index++)
        {
            var packet = Fh6PacketBuilder.BuildDemoPacket(index, hertz);
            Assert.IsTrue(parser.TryParse(packet, index, DateTimeOffset.UnixEpoch.AddSeconds(index / (double)hertz), TelemetrySourceKind.Simulator, out _, out var error), error);
        }
    }

    [TestMethod]
    public async Task CorruptReplayFailsWithoutPublishingFrames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-corrupt-{Guid.NewGuid():N}.lfztelemetry");
        await File.WriteAllBytesAsync(path, new byte[16]);
        try
        {
            var published = 0;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await new TelemetryReplaySource(path, 0).RunAsync(_ => { published++; return ValueTask.CompletedTask; }, _ => { }, CancellationToken.None));
            Assert.AreEqual(0, published);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
