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

            var streamed = new List<TelemetryFrame>();
            var metadata = await TelemetryRecordingReader.ReadAsync(
                path,
                frame =>
                {
                    streamed.Add(frame);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);
            Assert.AreEqual("LazyForza.Tests", metadata.Product);
            Assert.AreEqual("deterministic", metadata.Note);
            Assert.HasCount(8, streamed);
            Assert.IsTrue(streamed.All(frame => frame.Source == TelemetrySourceKind.Replay));

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

    [TestMethod]
    public async Task SingleLapRecordingRoundTripsAndRejectsRawReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazyforza-single-lap-{Guid.NewGuid():N}.lfztelemetry");
        var corruptPath = Path.Combine(Path.GetTempPath(), $"lazyforza-single-lap-corrupt-{Guid.NewGuid():N}.lfztelemetry");
        try
        {
            var lap = SingleLap();
            await SingleLapTelemetryRecordingFile.WriteAsync(
                path,
                "测试官方赛事",
                lap,
                CancellationToken.None);

            var loaded = await SingleLapTelemetryRecordingFile.TryReadAsync(
                path,
                CancellationToken.None);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(TelemetryRecordingContentKind.SingleLap, loaded.Metadata.ContentKind);
            Assert.AreEqual("测试官方赛事", loaded.TrackName);
            Assert.AreEqual(lap.Id, loaded.Lap.Id);
            Assert.AreEqual(lap.Vehicle, loaded.Lap.Vehicle);
            Assert.HasCount(lap.Samples.Count, loaded.Lap.Samples);
            Assert.AreEqual(lap.Samples[1].Dynamics, loaded.Lap.Samples[1].Dynamics);

            var rawReplayError = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await TelemetryRecordingReader.ReadAsync(
                    path,
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));
            StringAssert.Contains(rawReplayError.Message, "Replay Workbench");

            File.Copy(path, corruptPath);
            var corruptBytes = await File.ReadAllBytesAsync(corruptPath);
            corruptBytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(corruptPath, corruptBytes);
            var checksumError = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await SingleLapTelemetryRecordingFile.TryReadAsync(
                    corruptPath,
                    CancellationToken.None));
            StringAssert.Contains(checksumError.Message, "checksum");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(corruptPath)) File.Delete(corruptPath);
        }
    }

    private static LapRecord SingleLap()
    {
        var dynamics = new LapDynamics(
            -0.18,
            new WheelValues(0.1f, 0.2f, 0.3f, 0.4f),
            new WheelValues(0.05f, 0.06f, 0.07f, 0.08f),
            new WheelValues(0.12f, 0.22f, 0.32f, 0.42f));
        return new LapRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            3,
            Guid.NewGuid(),
            new VehicleProfileFingerprint(6001, 6, 917, 2, 8, 8500, "g", "c"),
            DateTimeOffset.UnixEpoch,
            52.345,
            true,
            null,
            [new LapSegment(0, 52.345, true)],
            [
                new LapSample(0, 0, 40, 4_500, 4, 1, 0, 0, 10, 2, 20),
                new LapSample(500, 20, 45, 5_200, 5, 0.8, 0.1, -0.2, 100, 3, 80, dynamics),
                new LapSample(1_000, 52.345, 38, 4_200, 4, 0.5, 0.4, 0.1, 10, 2, 20, dynamics)
            ]);
    }
}
