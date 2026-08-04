using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateRaceGeometryTests
{
    [TestMethod]
    public void EstateRaceModuleIsAlwaysAvailableByDefault()
    {
        var module = new EstateRaceModule(() => null);

        Assert.IsTrue(module.Descriptor.DefaultEnabled);
    }

    [TestMethod]
    public void ProjectsPositionAndNormalizesTrackMapCoordinates()
    {
        var track = TrackAlgorithms.BuildTemplate("projection", Enumerable.Range(0, 51)
            .Select(index => new TrackPoint(index * 2, 3, 10, 0, 0, 0))
            .ToArray());
        var projected = EstateRaceGeometry.Project(track, new Vector3F(50, 3, 12));
        Assert.AreEqual(0.5, projected.Progress, 0.03);
        Assert.AreEqual(2, Math.Abs(projected.LateralOffsetMeters), 0.05);
        Assert.AreEqual(0.5, projected.MapX, 0.03);
        var outline = EstateRaceGeometry.NormalizeOutline(track);
        Assert.IsTrue(outline.Count >= 2);
        Assert.AreEqual(0, outline[0].X, 0.001);
        Assert.AreEqual(1, outline[^1].X, 0.001);
    }

    [TestMethod]
    public void DetectsRecordedPitCorridorAndServicePolygonWithoutClaimingTireWear()
    {
        var entry = Gate(0, 0, 1, 0);
        var exit = Gate(30, 0, 1, 0);
        var pit = new EstatePitDefinition(
            entry,
            exit,
            [new EstateGatePoint(0, 2, 0), new EstateGatePoint(30, 2, 0)],
            new EstateGatePoint(15, 2, 0),
            4,
            80,
            3,
            3.5,
            [
                new EstateGatePoint(12, 2, -2),
                new EstateGatePoint(18, 2, -2),
                new EstateGatePoint(18, 2, 2),
                new EstateGatePoint(12, 2, 2)
            ]);
        Assert.IsTrue(EstateRaceGeometry.IsInPitLane(pit, new Vector3F(10, 2, 2)));
        Assert.IsFalse(EstateRaceGeometry.IsInPitLane(pit, new Vector3F(10, 2, 6)));
        Assert.IsTrue(EstateRaceGeometry.IsInServiceZone(pit, new Vector3F(15, 2, 0)));
        Assert.IsFalse(EstateRaceGeometry.IsInServiceZone(pit, new Vector3F(22, 2, 0)));
    }

    [TestMethod]
    public void CreditsOneServiceOnlyAfterContinuousStationaryGameTimeInRecordedZone()
    {
        var pit = new EstatePitDefinition(
            Gate(0, 0, 1, 0),
            Gate(30, 0, 1, 0),
            [new EstateGatePoint(0, 2, 0), new EstateGatePoint(30, 2, 0)],
            new EstateGatePoint(15, 2, 0),
            4,
            80,
            3,
            3.5,
            [
                new EstateGatePoint(12, 2, -2),
                new EstateGatePoint(18, 2, -2),
                new EstateGatePoint(18, 2, 2),
                new EstateGatePoint(12, 2, 2)
            ]);
        var tracker = new EstatePitServiceTracker();

        _ = tracker.Observe(Frame(1_000, 15, 0), pit, true);
        _ = tracker.Observe(Frame(2_000, 15, 0), pit, true);
        _ = tracker.Observe(Frame(3_000, 15, 0), pit, true);
        var completed = tracker.Observe(Frame(4_000, 15, 0), pit, true);
        Assert.IsTrue(completed.RequirementMet);
        Assert.AreEqual(3, completed.ElapsedSeconds, 0.001);
        Assert.AreEqual(1, completed.CompletedServices);

        var stillStopped = tracker.Observe(Frame(5_000, 15, 0), pit, true);
        Assert.AreEqual(1, stillStopped.CompletedServices);
        var leftZone = tracker.Observe(Frame(6_000, 24, 0), pit, true);
        Assert.IsFalse(leftZone.RequirementMet);
        Assert.AreEqual(0, leftZone.ElapsedSeconds, 0.001);
        Assert.AreEqual(1, leftZone.CompletedServices);

        _ = tracker.Observe(Frame(7_000, 15, 0), pit, true);
        _ = tracker.Observe(Frame(8_000, 15, 0), pit, true);
        var interrupted = tracker.Observe(Frame(9_000, 15, 0), pit, false);
        Assert.AreEqual(1, interrupted.ElapsedSeconds, 0.001);
        Assert.AreEqual(1, interrupted.CompletedServices);
        _ = tracker.Observe(Frame(10_000, 15, 0), pit, true);
        var resumed = tracker.Observe(Frame(11_000, 15, 0), pit, true);
        Assert.AreEqual(2, resumed.ElapsedSeconds, 0.001);
    }

    [TestMethod]
    public void EstateRaceSectorColorStartsPurpleThenUsesSessionAndPersonalBenchmarks()
    {
        Assert.AreEqual(SectorColorState.Purple,
            EstateRaceLapColorRules.Resolve(18.5, null, null));
        Assert.AreEqual(SectorColorState.Purple,
            EstateRaceLapColorRules.Resolve(18.4, 18.4, 18.6));
        Assert.AreEqual(SectorColorState.Green,
            EstateRaceLapColorRules.Resolve(18.6, 18.4, 18.6));
        Assert.AreEqual(SectorColorState.Yellow,
            EstateRaceLapColorRules.Resolve(18.8, 18.4, 18.6));
    }

    [DataTestMethod]
    [DataRow("127.0.0.1:24876", "ws://127.0.0.1:24876/ws")]
    [DataRow("http://race.example.test:8080", "ws://race.example.test:8080/ws")]
    [DataRow("https://race.example.test", "wss://race.example.test/ws")]
    [DataRow("wss://race.example.test/custom", "wss://race.example.test/custom")]
    public void NormalizesUserFacingServerAddress(string input, string expected) =>
        Assert.AreEqual(expected, EstateRaceModule.ServerWebSocketUri(input).ToString());

    [TestMethod]
    public void NormalizesEveryRaceHudWidgetIndependently()
    {
        var value = new EstateRaceHudLayout(
            new(true, -1, 2, 4, 4),
            new(false, 0.5, 0.6, 0.8, 0.55),
            new(true, double.NaN, 0.7, 0.2, double.NaN),
            new(true, 0.3, double.PositiveInfinity, 1.1, 0));
        var normalized = EstateRaceHudLayoutSettings.Normalize(value);
        Assert.AreEqual(0, normalized.Get(EstateRaceHudWidgetKind.Leaderboard).Left);
        Assert.AreEqual(1, normalized.Get(EstateRaceHudWidgetKind.Leaderboard).Top);
        Assert.AreEqual(1.75, normalized.Get(EstateRaceHudWidgetKind.Leaderboard).Scale);
        Assert.AreEqual(1, normalized.Get(EstateRaceHudWidgetKind.Leaderboard).Opacity);
        Assert.IsFalse(normalized.Get(EstateRaceHudWidgetKind.TrackMap).IsVisible);
        Assert.AreEqual(0.55, normalized.Get(EstateRaceHudWidgetKind.TrackMap).Opacity);
        Assert.AreEqual(0.5, normalized.Get(EstateRaceHudWidgetKind.GripStatus).Scale);
        Assert.AreEqual(1, normalized.Get(EstateRaceHudWidgetKind.GripStatus).Opacity);
        Assert.AreEqual(0, normalized.Get(EstateRaceHudWidgetKind.Banner).Top);
        Assert.AreEqual(0.15, normalized.Get(EstateRaceHudWidgetKind.Banner).Opacity);
    }

    private static EstateTimingGate Gate(double x, double z, double forwardX, double forwardZ) => new(
        new EstateGatePoint(x, 2, z - 3),
        new EstateGatePoint(x, 2, z + 3),
        forwardX,
        forwardZ,
        0,
        0,
        0);

    private static TelemetryFrame Frame(uint timestamp, double x, double speedKph)
    {
        var raw = new Fh6RawTelemetry
        {
            TimestampMS = timestamp,
            Position = new Vector3F((float)x, 2, 0)
        };
        return new TelemetryFrame(
            timestamp,
            DateTimeOffset.UtcNow,
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(speedKph, 0, 0, 0, 0, 0, 0, 0, default),
            ReadOnlyMemory<byte>.Empty);
    }
}
