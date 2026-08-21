using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateShortcutDetectorTests
{
    [TestMethod]
    public void HairpinChordProducesDistanceGainAndMissedGateEvidence()
    {
        var track = CreateHairpinTrack();
        var detector = new EstateShortcutDetector();
        RaceShortcutEvidence? evidence = null;
        var trace = new List<string>();
        long sequence = 1;
        long monotonic = 1_000;

        detector.Observe(Frame(sequence++, 0, 70, 20), track, null, true, monotonic);
        for (var index = 1; index <= 20; index++)
        {
            monotonic += 100;
            var observation = detector.Observe(
                Frame(sequence++, index * 2, 70, 20),
                track,
                null,
                true,
                monotonic);
            trace.Add($"{index}:{observation.Projection.ProgressMeters:0.0}/" +
                      $"{observation.Projection.DistanceMeters:0.0}/" +
                      $"{observation.Projection.IsAmbiguous}");
            evidence ??= observation.Evidence;
        }

        Assert.IsNotNull(evidence,
            $"跨过发卡弯内部的弦线应产生客户端捷径证据。关键弯 {detector.ProtectedArcCount}，" +
            $"投影：{string.Join(",", trace)}");
        Assert.IsTrue(evidence.GainMeters > 20);
        Assert.IsTrue(evidence.MissedCriticalGates > 0);
        Assert.IsTrue((evidence.Flags & (int)EstateShortcutEvidenceFlags.DistanceGain) != 0);
        Assert.IsTrue((evidence.Flags & (int)EstateShortcutEvidenceFlags.MissedCriticalGate) != 0);
        Assert.IsTrue(evidence.Confidence >= 0.72);
    }

    [TestMethod]
    public void FollowingTheRecordedHairpinDoesNotCreateShortcutEvidence()
    {
        var track = CreateHairpinTrack();
        var detector = new EstateShortcutDetector();
        var route = new List<(double X, double Z)>();
        for (var z = 70; z <= 100; z += 5) route.Add((0, z));
        for (var x = 5; x <= 40; x += 5) route.Add((x, 100));
        for (var z = 95; z >= 70; z -= 5) route.Add((40, z));

        long monotonic = 1_000;
        RaceShortcutEvidence? evidence = null;
        for (var index = 0; index < route.Count; index++)
        {
            if (index > 0) monotonic += 250;
            var observation = detector.Observe(
                Frame(index + 1, route[index].X, route[index].Z, 20),
                track,
                null,
                true,
                monotonic);
            evidence ??= observation.Evidence;
        }

        Assert.IsNull(evidence, "沿已录入路线完整通过弯道不能产生捷径证据。 ");
    }

    [TestMethod]
    public void TelemetryJumpAndRecordedPitBranchDoNotCreateShortcutEvidence()
    {
        var track = CreateHairpinTrack();
        var detector = new EstateShortcutDetector();
        detector.Observe(Frame(1, 0, 70, 1), track, null, true, 1_000);
        var jump = detector.Observe(Frame(2, 40, 70, 1), track, null, true, 1_100);
        Assert.IsNull(jump.Evidence, "与车速不相符的位置跳变只能中断证据窗口。 ");

        detector.Reset();
        var pit = CreateChordPit();
        RaceShortcutEvidence? evidence = null;
        detector.Observe(Frame(3, 0, 70, 20), track, pit, true, 2_000);
        for (var index = 1; index <= 20; index++)
        {
            var observation = detector.Observe(
                Frame(index + 3, index * 2, 70, 20),
                track,
                pit,
                true,
                2_000 + index * 100);
            evidence ??= observation.Evidence;
        }
        Assert.IsNull(evidence, "已录入的维修区合法分支不能作为切弯证据。 ");
    }

    private static TrackTemplate CreateHairpinTrack()
    {
        var points = new List<TrackPoint>();
        for (var z = 0; z <= 100; z += 5) points.Add(Point(0, z));
        for (var x = 5; x <= 40; x += 5) points.Add(Point(x, 100));
        for (var z = 95; z >= 0; z -= 5) points.Add(Point(40, z));
        for (var x = 35; x >= 0; x -= 5) points.Add(Point(x, 0));
        return TrackAlgorithms.BuildTemplate("发卡弯测试", points) with
        {
            TimingKind = TrackTimingKind.EstateGeometry,
            Category = "地产环道",
            CaptureLapCount = 2
        };
    }

    private static TrackPoint Point(double x, double z) => new(x, 2, z, 0, 0, 0);

    private static EstatePitDefinition CreateChordPit()
    {
        var gate = new EstateTimingGate(
            new EstateGatePoint(0, 2, 65),
            new EstateGatePoint(0, 2, 75),
            1,
            0,
            0,
            0,
            0);
        return new EstatePitDefinition(
            gate,
            gate with
            {
                Left = new EstateGatePoint(40, 2, 65),
                Right = new EstateGatePoint(40, 2, 75)
            },
            Enumerable.Range(0, 21)
                .Select(index => new EstateGatePoint(index * 2, 2, 70))
                .ToArray(),
            new EstateGatePoint(20, 2, 70),
            3,
            80,
            5,
            4);
    }

    private static TelemetryFrame Frame(long sequence, double x, double z, double speedMetersPerSecond)
    {
        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = 1,
            TimestampMS = (uint)(sequence * 100),
            Position = new Vector3F((float)x, 2, (float)z),
            Speed = (float)speedMetersPerSecond
        };
        return new TelemetryFrame(
            sequence,
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence * 100),
            TelemetrySourceKind.Live,
            raw,
            new NormalizedTelemetry(
                speedMetersPerSecond * 3.6,
                speedMetersPerSecond * 2.237,
                0,
                0,
                0,
                0,
                0,
                0,
                default),
            ReadOnlyMemory<byte>.Empty);
    }
}
