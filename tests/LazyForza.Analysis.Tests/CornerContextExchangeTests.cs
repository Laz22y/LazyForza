using System.Text.Json;
using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class CornerContextExchangeTests
{
    [TestMethod]
    public async Task OptionalContextSurvivesBothLapExchangeFormatsAndLegacyJsonStillLoads()
    {
        var exchange = Path.Combine(Path.GetTempPath(), $"corner-{Guid.NewGuid():N}.lfzlap");
        var recording = Path.ChangeExtension(exchange, ".lfztelemetry");
        try
        {
            var samples = Enumerable.Range(0, 25).Select(i => new LapSample(i * 5, i * .1, 50, 5000, 3, .8, 0, 0, i * 5, 0, 0)).ToArray();
            var vehicle = new VehicleProfileFingerprint(123, 5, 850, 2, 8, 8000, "learning", "learning");
            var lap = new LapRecord(Guid.NewGuid(), Guid.NewGuid(), 1, TrackAlgorithms.SectorSchemaVersion,
                Guid.NewGuid(), vehicle, DateTimeOffset.UtcNow, 2.4, true, null, [], samples) { TrackRevision = "track-v1:test", SessionInfo = new LapSessionInfo(Guid.NewGuid(), LapSessionKind.EstateRace, "Race"), Annotation = new("My lap", "Late braking", true, true) };
            await LapAnalysisExchangeFile.WriteAsync(exchange, lap.TrackId, "track", 1, lap.SectorSchemaVersion, null, [lap], CancellationToken.None);
            var loaded = (await LapAnalysisExchangeFile.ReadAsync(exchange, CancellationToken.None)).Laps.Single();
            Assert.AreEqual(lap.TrackRevision, loaded.TrackRevision);
            Assert.AreEqual(lap.SessionInfo, loaded.SessionInfo);
            Assert.AreEqual(lap.Annotation, loaded.Annotation);
            Assert.AreEqual(vehicle, loaded.Vehicle);
            await SingleLapTelemetryRecordingFile.WriteAsync(recording, "track", lap, CancellationToken.None);
            var replay = await SingleLapTelemetryRecordingFile.TryReadAsync(recording, CancellationToken.None);
            Assert.AreEqual(lap.TrackRevision, replay!.Lap.TrackRevision);
            Assert.AreEqual(lap.SessionInfo, replay.Lap.SessionInfo);
            Assert.AreEqual(lap.Annotation, replay.Lap.Annotation);
            Assert.AreEqual(vehicle, replay.Lap.Vehicle);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(lap))!.AsObject();
            legacy.Remove("TrackRevision");
            legacy.Remove("SessionInfo");
            legacy.Remove("Annotation");
            Assert.AreEqual(new LapAnnotation(), JsonSerializer.Deserialize<LapRecord>(legacy.ToJsonString())!.Annotation);
            Assert.IsNull(JsonSerializer.Deserialize<LapRecord>(legacy.ToJsonString())!.SessionInfo);
            Assert.IsNull(JsonSerializer.Deserialize<LapRecord>(legacy.ToJsonString())!.TrackRevision);
            Assert.AreEqual(lap.TrackRevision, LapSummary.FromRecord(lap).WithSamples(samples).TrackRevision);
        }
        finally
        {
            if (File.Exists(exchange)) File.Delete(exchange);
            if (File.Exists(recording)) File.Delete(recording);
        }
    }
}
