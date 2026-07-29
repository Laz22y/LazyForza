using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using LazyForza.App;
using LazyForza.Domain;
using LazyForza.Modules.Abstractions;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class DiagnosticPackageTests
{
    [TestMethod]
    public async Task RingBufferCapturesAnomaliesAndExportsRedactedVerifiedPackage()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"LazyForza-Diagnostic-Test-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"lazyforza-diagnostic-{Guid.NewGuid():N}.lfzdiag");
        var feed = new TestTelemetryFeed();
        await using var capture = new DiagnosticCaptureService(feed, dataRoot, _ => { });
        await capture.StartAsync(CancellationToken.None);
        var at = DateTimeOffset.UtcNow;
        feed.Publish(Frame(1, at, 100, 1001, 800));
        feed.Publish(Frame(2, at.AddSeconds(3), 4_500, 2002, 900));

        Assert.IsTrue(SpinWait.SpinUntil(
            () => capture.AnomalyCount >= 3,
            TimeSpan.FromSeconds(2)));
        capture.RecordSignal(new DiagnosticSignal(
            "lap.test",
            $"Sensitive path: {dataRoot}",
            true,
            at.AddSeconds(4)));
        capture.UpdateTrackMatch(new LazyForza.Modules.LapAnalysis.TrackMatchDiagnostics(
            at.AddSeconds(4),
            "精匹配中",
            85,
            4,
            3,
            [
                new LazyForza.Modules.LapAnalysis.TrackMatchCandidateDiagnostic(
                    Guid.NewGuid(),
                    "歌利亚",
                    TrackLayoutKind.PointToPoint,
                    "公路竞速",
                    54_000,
                    "精匹配",
                    4,
                    7.5,
                    900,
                    0.96,
                    null)
            ],
            []));

        try
        {
            capture.Export(packagePath, 8, "1.2.1");
            using var archive = ZipFile.OpenRead(packagePath);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "manifest.json",
                    "telemetry.json",
                    "events.json",
                    "anomalies.json",
                    "track-match.json"
                },
                names);

            var manifest = ReadText(archive, "manifest.json");
            var events = ReadText(archive, "events.json");
            var anomalies = ReadText(archive, "anomalies.json");
            var trackMatch = ReadText(archive, "track-match.json");
            StringAssert.Contains(manifest, "\"schemaVersion\": 8");
            using (var eventsJson = JsonDocument.Parse(events))
            {
                Assert.IsTrue(eventsJson.RootElement.EnumerateArray().Any(item =>
                    item.GetProperty("summary").GetString()?.Contains(
                        "<DATA_ROOT>",
                        StringComparison.Ordinal) == true));
            }
            Assert.IsFalse(events.Contains(dataRoot, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(events.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(anomalies, "udp.long-gap");
            StringAssert.Contains(anomalies, "udp.timestamp-jump");
            StringAssert.Contains(anomalies, "vehicle.configuration-switch");
            using (var trackMatchJson = JsonDocument.Parse(trackMatch))
            {
                Assert.AreEqual(
                    "歌利亚",
                    trackMatchJson.RootElement
                        .GetProperty("topCandidates")[0]
                        .GetProperty("trackName")
                        .GetString());
            }

            using var manifestJson = JsonDocument.Parse(manifest);
            var files = manifestJson.RootElement.GetProperty("files");
            foreach (var entry in archive.Entries.Where(entry => entry.FullName != "manifest.json"))
            {
                using var stream = entry.Open();
                var actual = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(stream));
                Assert.AreEqual(
                    files.GetProperty(entry.FullName).GetString(),
                    actual);
            }
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
    }

    private static string ReadText(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static TelemetryFrame Frame(
        long sequence,
        DateTimeOffset arrival,
        uint timestamp,
        int carOrdinal,
        int performanceIndex)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = timestamp,
            CarOrdinal = carOrdinal,
            CarClass = 4,
            CarPerformanceIndex = performanceIndex,
            CurrentRaceTime = sequence,
            CurrentLap = sequence,
            Position = new Vector3F(sequence, 0, sequence),
            Speed = 30,
            CurrentEngineRpm = 5_000,
            Gear = 4,
            Accel = 200
        };
        return new TelemetryFrame(
            sequence,
            arrival,
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(108, 67, 200, 0.78, 0, 0, 0, 0.6, default),
            ReadOnlyMemory<byte>.Empty);
    }

    private sealed class TestTelemetryFeed : ITelemetryFeed
    {
        private readonly Channel<TelemetryFrame> channel = Channel.CreateUnbounded<TelemetryFrame>();

        public TelemetryFrame? Latest { get; private set; }

        public TelemetryDiagnostics Diagnostics => new(
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
            Latest?.ArrivalTime,
            null);

        public ValueTask<ITelemetrySubscription> SubscribeAsync(
            string consumerId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ITelemetrySubscription>(
                new TestSubscription(channel.Reader));

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

        private sealed class TestSubscription(
            ChannelReader<TelemetryFrame> frames) : ITelemetrySubscription
        {
            public ChannelReader<TelemetryFrame> Frames { get; } = frames;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
