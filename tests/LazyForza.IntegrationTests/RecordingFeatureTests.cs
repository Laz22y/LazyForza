using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;
using LazyForza.Modules.LapAnalysis;
using LazyForza.Storage;
using LazyForza.Telemetry;
using System.Threading.Channels;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class RecordingFeatureTests
{
    [TestMethod]
    public async Task RecordingWorkbenchStreamsAndDownsamplesDrivingFrames()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-workbench-{Guid.NewGuid():N}.lfztelemetry");
        try
        {
            var parser = new Fh6PacketParser();
            await using (var writer = await TelemetryRecordingWriter.CreateAsync(
                             path,
                             new RecordingMetadata(
                                 "LazyForza.Tests",
                                 1,
                                 TelemetrySourceKind.Live,
                                 DateTimeOffset.UnixEpoch,
                                 "workbench"),
                             CancellationToken.None))
            {
                for (var index = 0; index < 180; index++)
                {
                    var packet = Fh6PacketBuilder.BuildDemoPacket(index, 60);
                    Assert.IsTrue(parser.TryParse(
                        packet,
                        index,
                        DateTimeOffset.UnixEpoch.AddSeconds(index / 60d),
                        TelemetrySourceKind.Live,
                        out var frame,
                        out var error), error);
                    await writer.WriteAsync(frame!, CancellationToken.None);
                }
                Assert.IsTrue(writer.BytesWritten > 180 * Fh6PacketParser.PacketLength);
            }

            var replay = await TelemetryRecordingAnalysis.LoadAsync(path, CancellationToken.None);
            Assert.AreEqual(180, replay.FrameCount);
            Assert.AreEqual("workbench", replay.Metadata.Note);
            Assert.IsTrue(replay.Lap.Samples.Count is >= 45 and <= 65);
            Assert.IsTrue(replay.Lap.TotalSeconds > 2.8);
            Assert.IsTrue(replay.Lap.Samples[^1].S > 50);
            Assert.IsTrue(replay.Lap.Samples.All(sample => sample.Dynamics is not null));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RecordingWorkbenchImportsSingleLapTelemetryExportWithoutResampling()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-workbench-single-lap-{Guid.NewGuid():N}.lfztelemetry");
        try
        {
            var lap = new LapRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                3,
                Guid.NewGuid(),
                new VehicleProfileFingerprint(6001, 6, 917, 2, 8, 8500, "g", "c"),
                DateTimeOffset.UnixEpoch,
                48.25,
                true,
                null,
                [new LapSegment(0, 48.25, true)],
                [
                    new LapSample(0, 0, 35, 4_000, 4, 1, 0, 0, 0, 0, 0),
                    new LapSample(400, 20, 42, 5_000, 5, 0.7, 0.2, 0, 120, 2, 60),
                    new LapSample(900, 48.25, 37, 4_300, 4, 0.5, 0.4, 0, 0, 0, 0)
                ]);
            await SingleLapTelemetryRecordingFile.WriteAsync(
                path,
                "回放导入测试赛道",
                lap,
                CancellationToken.None);

            var replay = await TelemetryRecordingAnalysis.LoadAsync(
                path,
                CancellationToken.None);

            Assert.AreEqual(TelemetryRecordingContentKind.SingleLap, replay.Metadata.ContentKind);
            Assert.AreEqual("回放导入测试赛道", replay.TrackName);
            Assert.AreEqual(lap.Id, replay.Lap.Id);
            Assert.AreEqual(lap.Samples.Count, replay.FrameCount);
            CollectionAssert.AreEqual(
                lap.Samples.Select(sample => sample.S).ToArray(),
                replay.Lap.Samples.Select(sample => sample.S).ToArray());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ConservativeCapacityModeNeverDeletesExistingRecordings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var catalog = new RecordingCatalog(root);
            CreateRecordings(catalog, root, 3);
            var paths = catalog.List().Select(entry => entry.RecordingPath).ToArray();
            var manager = new RecordingCapacityManager(root, catalog);

            var result = manager.Prepare(new AutomaticRecordingOptions(
                true,
                10,
                false,
                1,
                15,
                10));

            Assert.IsFalse(result.CanStart);
            Assert.IsTrue(paths.All(File.Exists));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RotationKeepsRecentFiveAndPinnedRecording()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var catalog = new RecordingCatalog(root);
            CreateRecordings(catalog, root, 8);
            var before = catalog.List().OrderBy(entry => entry.CreatedAt).ToArray();
            catalog.SetPinned(before[0].RecordingPath, true);
            var recentFive = before.OrderByDescending(entry => entry.CreatedAt)
                .Take(5)
                .Select(entry => entry.RecordingPath)
                .ToArray();
            var manager = new RecordingCapacityManager(root, catalog);

            var result = manager.Prepare(new AutomaticRecordingOptions(
                true,
                135,
                true,
                1,
                15,
                10));

            Assert.IsTrue(result.CanStart, result.Message);
            Assert.IsTrue(File.Exists(before[0].RecordingPath), "Pinned recording must survive rotation.");
            Assert.IsTrue(recentFive.All(File.Exists), "The five newest automatic recordings must survive rotation.");
            Assert.IsFalse(File.Exists(before[1].RecordingPath));
            Assert.IsFalse(File.Exists(before[2].RecordingPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AutomaticRecorderIncludesPreRollAndFinalizesWhenDisabled()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var directories = new DataDirectoryService(root);
            directories.EnsureCreated();
            using var store = new LazyForzaStore(directories.DatabasePath);
            var options = new AutomaticRecordingOptions(
                true,
                1024L * 1024 * 1024,
                false,
                1024L * 1024 * 1024,
                15,
                10);
            options.Save(store);
            await using var feed = new TestTelemetryFeed();
            var module = new LapAnalysisModule(store, TelemetrySourceKind.Live);
            await using var recorder = new TelemetryRecorderController(
                feed,
                directories,
                store,
                TelemetrySourceKind.Live,
                module,
                _ => { });
            await recorder.InitializeAsync(CancellationToken.None);

            var parser = new Fh6PacketParser();
            for (var index = 0; index < 8; index++)
            {
                var packet = Fh6PacketBuilder.BuildDemoPacket(index, 20);
                Fh6PacketBuilder.WriteInt32(packet, 0, 0);
                Assert.IsTrue(parser.TryParse(
                    packet,
                    index,
                    DateTimeOffset.UnixEpoch.AddMilliseconds(index * 50),
                    TelemetrySourceKind.Live,
                    out var menuFrame,
                    out _));
                module.Observe(menuFrame!);
                feed.Publish(menuFrame!);
            }
            for (var index = 8; index < 28; index++)
            {
                var packet = Fh6PacketBuilder.BuildDemoPacket(index, 20);
                Assert.IsTrue(parser.TryParse(
                    packet,
                    index,
                    DateTimeOffset.UnixEpoch.AddMilliseconds(index * 50),
                    TelemetrySourceKind.Live,
                    out var frame,
                    out _));
                module.Observe(frame!);
                feed.Publish(frame!);
            }

            await WaitUntilAsync(() => recorder.IsAutomaticRecording, TimeSpan.FromSeconds(3));
            await Task.Delay(200);
            await recorder.SetAutomaticOptionsAsync(options with { Enabled = false }, CancellationToken.None);

            var recording = recorder.Recordings.Single();
            var frameCount = 0;
            await TelemetryRecordingReader.ReadAsync(
                recording.RecordingPath,
                _ =>
                {
                    frameCount++;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);
            Assert.IsTrue(frameCount >= 20, $"Expected pre-roll plus competition frames, got {frameCount}.");
            Assert.IsTrue(recording.IsAutomatic);
            Assert.IsFalse(File.Exists(recording.RecordingPath + ".partial"));
            Assert.AreEqual(
                recording.RecordingPath,
                store.GetSessionRawRecordingPath(recording.SessionId!.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateRecordings(RecordingCatalog catalog, string root, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(root, $"auto-{index:00}.lfztelemetry");
            File.WriteAllBytes(path, new byte[20]);
            catalog.Save(new RecordingCatalogEntry(
                path,
                DateTimeOffset.UnixEpoch.AddDays(index),
                Guid.NewGuid(),
                $"Track {index}",
                1,
                1,
                true,
                false,
                null,
                null));
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-recording-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                Assert.Fail("Timed out waiting for the automatic recorder.");
            await Task.Delay(20);
        }
    }

    private sealed class TestTelemetryFeed : ITelemetryFeed
    {
        private readonly Channel<TelemetryFrame> frames = Channel.CreateUnbounded<TelemetryFrame>();

        public TelemetryFrame? Latest { get; private set; }
        public TelemetryDiagnostics Diagnostics { get; } = new(
            "test",
            0,
            TelemetryStreamState.Live,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            null);

        public void Publish(TelemetryFrame frame)
        {
            Latest = frame;
            frames.Writer.TryWrite(frame);
        }

        public ValueTask<ITelemetrySubscription> SubscribeAsync(
            string consumerId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ITelemetrySubscription>(new TestSubscription(frames.Reader));

        public ValueTask DisposeAsync()
        {
            frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed class TestSubscription(ChannelReader<TelemetryFrame> frames) : ITelemetrySubscription
        {
            public ChannelReader<TelemetryFrame> Frames { get; } = frames;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
