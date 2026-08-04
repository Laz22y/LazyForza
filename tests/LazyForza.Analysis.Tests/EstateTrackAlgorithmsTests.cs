using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class EstateTrackAlgorithmsTests
{
    [TestMethod]
    public void FitsTwoOppositeLowNoiseFinishLineTraces()
    {
        var first = Trace(-6, 6, 0.04);
        var second = Trace(6, -6, -0.03);

        var result = EstateTrackAlgorithms.FitStartFinishGate(first, second);

        Assert.IsTrue(result.IsAccepted, result.Explanation);
        Assert.IsNotNull(result.Gate);
        Assert.AreEqual(12, EstateTrackAlgorithms.GateWidth(result.Gate), 0.35);
        Assert.IsTrue(result.FitRmsMeters < 0.1);
        Assert.IsTrue(result.TraceOffsetMeters < 0.1);
        Assert.IsTrue(result.TraceAngleDifferenceDegrees < 0.1);
        Assert.IsTrue(result.Gate.EndpointMarginMeters >= EstateTrackAlgorithms.MinimumFinishEndpointMarginMeters);
    }

    [TestMethod]
    public void RejectsTracesThatDoNotDescribeTheSamePaintedLine()
    {
        var first = Trace(-6, 6, 0);
        var second = Trace(6, -6, 0.8);

        var result = EstateTrackAlgorithms.FitStartFinishGate(first, second);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(result.TraceOffsetMeters > EstateTrackAlgorithms.MaximumTraceOffsetMeters);
    }

    [TestMethod]
    public void DirectedFiniteGateInterpolatesForwardCrossingTimestamp()
    {
        var gate = DirectedGate();
        var previous = new EstateTimedPosition(0, 0, -2, 20, 1_000);
        var current = new EstateTimedPosition(0, 0, 3, 20, 1_100);

        var detected = EstateTrackAlgorithms.TryDetectForwardCrossing(gate, previous, current, out var crossing);

        Assert.IsTrue(detected);
        Assert.AreEqual(1_040, crossing.TimestampMilliseconds);
        Assert.AreEqual(0, crossing.Z, 0.000_001);
    }

    [TestMethod]
    public void ReverseAndOutsidePassesDoNotTriggerFinishGate()
    {
        var gate = DirectedGate();
        Assert.IsFalse(EstateTrackAlgorithms.TryDetectForwardCrossing(
            gate,
            new EstateTimedPosition(0, 0, 2, 20, 100),
            new EstateTimedPosition(0, 0, -2, 20, 200),
            out _));
        Assert.IsFalse(EstateTrackAlgorithms.TryDetectForwardCrossing(
            gate,
            new EstateTimedPosition(9, 0, -2, 20, 100),
            new EstateTimedPosition(9, 0, 2, 20, 200),
            out _));
    }

    [TestMethod]
    public void SampleInsideDeadbandDoesNotHideForwardCrossing()
    {
        var gate = DirectedGate();
        var previous = new EstateTimedPosition(0, 0, -0.005, 20, 1_000);
        var current = new EstateTimedPosition(0, 0, 0.25, 20, 1_020);

        var detected = EstateTrackAlgorithms.TryDetectForwardCrossing(
            gate,
            previous,
            current,
            out var crossing);

        Assert.IsTrue(detected);
        Assert.AreEqual(0, crossing.Z, 0.000_001);
        Assert.IsFalse(EstateTrackAlgorithms.TryDetectForwardCrossing(
            gate,
            current,
            previous,
            out _), "A reverse pass inside the tolerance must remain rejected.");
    }

    [TestMethod]
    public void TimestampUnwrapperContinuesAcrossUintWrap()
    {
        var unwrapper = new EstateTimestampUnwrapper();

        var before = unwrapper.Unwrap(uint.MaxValue - 5);
        var after = unwrapper.Unwrap(8);

        Assert.AreEqual(14, after - before);
    }

    [TestMethod]
    public void DirectionCaptureRequiresDistanceOnBothSidesOfFinishLine()
    {
        var gate = DirectedGate() with { ForwardX = 0, ForwardZ = 0 };
        var startsOnLine = Enumerable.Range(0, 21)
            .Select(index => new EstateGatePoint(0, 0, index * 0.5))
            .ToArray();
        var completePass = Enumerable.Range(0, 41)
            .Select(index => new EstateGatePoint(0, 0, -10 + index * 0.5))
            .ToArray();

        Assert.IsFalse(EstateTrackAlgorithms.TryApplyForwardDirection(
            gate,
            startsOnLine,
            out _,
            out var rejected));
        StringAssert.Contains(rejected, "不要从终点线上直接开始采样");

        Assert.IsTrue(EstateTrackAlgorithms.TryApplyForwardDirection(
            gate,
            completePass,
            out var directed,
            out var accepted), accepted);
        Assert.IsTrue(directed.ForwardZ > 0.9);
    }

    [TestMethod]
    public void CreatesOrderedForwardCheckpointsInsideCircuit()
    {
        var route = Enumerable.Range(0, 101)
            .Select(index =>
            {
                var angle = index * Math.PI * 2 / 100;
                return new TrackPoint(100 * Math.Cos(angle), 4, 100 * Math.Sin(angle), index * 6.28,
                    -Math.Sin(angle), Math.Cos(angle));
            })
            .ToArray();
        var track = TrackAlgorithms.BuildTemplate("estate", route) with { TimingKind = TrackTimingKind.EstateGeometry };

        var checkpoints = EstateTrackAlgorithms.CreateCheckpoints(track, 8);

        Assert.HasCount(8, checkpoints);
        Assert.IsTrue(checkpoints.Select(checkpoint => checkpoint.RouteProgressMeters).SequenceEqual(
            checkpoints.Select(checkpoint => checkpoint.RouteProgressMeters).Order()));
        Assert.IsTrue(checkpoints.All(checkpoint => checkpoint.Gate.HasDirection));
    }

    [TestMethod]
    public void CreatesDedicatedFinishGateWherePitLaneCrossesTimingPlane()
    {
        var centerLine = new[]
        {
            new EstateGatePoint(12, 0, -8),
            new EstateGatePoint(12.5, 0, -2),
            new EstateGatePoint(13, 0, 3),
            new EstateGatePoint(13.5, 0, 9)
        };

        var created = EstateTrackAlgorithms.TryCreatePitStartFinishGate(
            DirectedGate(), centerLine, 3.5, out var pitGate);

        Assert.IsTrue(created);
        Assert.AreEqual(7, EstateTrackAlgorithms.GateWidth(pitGate), 0.001);
        Assert.IsTrue(pitGate.ForwardZ > 0.9);
        var previous = new EstateTimedPosition(12.7, 0, -1, 15, 1_000);
        var current = new EstateTimedPosition(12.9, 0, 1, 15, 1_100);
        Assert.IsFalse(EstateTrackAlgorithms.TryDetectForwardCrossing(
            DirectedGate(), previous, current, out _), "The main finite gate must not cover the separate pit lane.");
        Assert.IsTrue(EstateTrackAlgorithms.TryDetectForwardCrossing(
            pitGate, previous, current, out _), "The dedicated pit gate must count the legal pit-lane pass.");
    }

    [TestMethod]
    public void RejectsPitLaneThatDoesNotCrossFinishPlaneInRaceDirection()
    {
        var reverseLane = new[]
        {
            new EstateGatePoint(12, 0, 5),
            new EstateGatePoint(12, 0, -5)
        };

        Assert.IsFalse(EstateTrackAlgorithms.TryCreatePitStartFinishGate(
            DirectedGate(), reverseLane, 3.5, out _));
    }

    private static EstateTimingGate DirectedGate() => new(
        new EstateGatePoint(-5, 0, 0),
        new EstateGatePoint(5, 0, 0),
        0,
        1,
        0.05,
        0.03,
        0.1);

    private static EstateGatePoint[] Trace(double from, double to, double zOffset) =>
        Enumerable.Range(0, 31)
            .Select(index =>
            {
                var amount = index / 30d;
                var x = from + (to - from) * amount;
                var noise = Math.Sin(index * 1.7) * 0.015;
                return new EstateGatePoint(x, 2 + x * 0.005, zOffset + noise);
            })
            .ToArray();
}
