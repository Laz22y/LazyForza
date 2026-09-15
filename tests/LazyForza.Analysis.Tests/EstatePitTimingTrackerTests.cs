using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class EstatePitTimingTrackerTests
{
    [TestMethod]
    [DataRow(-7d, 2d, true)]
    [DataRow(4d, 2d, true)]
    [DataRow(-15d, 2d, false)]
    [DataRow(-7d, 12d, false)]
    public void OffsetServicePolygonAndLaneHaveSeparateFiniteFinishSpans(double x, double y, bool expected)
    {
        var tracker = new EstatePitTimingTracker();
        tracker.Configure(Pit());
        tracker.Observe(Position(4, -100, 100), Position(4, -90, 200), out _);
        Assert.AreEqual(expected, tracker.Observe(Position(x, -.01, 1_000, y), Position(x, .01, 1_100, y), out _));
    }

    [TestMethod]
    public void ServiceStopPauseAndLapResetKeepTheSameConsumedVisit()
    {
        var tracker = new EstatePitTimingTracker();
        tracker.Configure(Pit());
        tracker.Observe(Position(4, -100, 100), Position(4, -90, 200), out _);
        tracker.BreakContinuity();
        Assert.IsTrue(tracker.Observe(Position(-7, -.01, 1_000), Position(-7, .01, 1_100), out _));
        tracker.AcceptFinishCrossing();
        tracker.BreakContinuity();
        Assert.IsTrue(tracker.IsActive);
        Assert.IsFalse(tracker.Observe(Position(-7, .01, 21_000), Position(-7, -.01, 21_100), out _));
        Assert.IsFalse(tracker.Observe(Position(-7, -.01, 41_000), Position(-7, .01, 41_100), out _));
        // Leaving the branch and returning for another pit visit arms a new crossing.
        tracker.Observe(Position(4, 90, 42_000), Position(4, 101, 43_000), out _);
        Assert.IsTrue(tracker.Exited);
        tracker.Observe(Position(4, -100, 50_000), Position(4, -90, 51_000), out _);
        Assert.IsTrue(tracker.Observe(Position(-7, -.01, 61_000), Position(-7, .01, 61_100), out _));
    }

    [TestMethod]
    public void ReverseStationaryAndSpaceBetweenLaneAndBoxDoNotCount()
    {
        var tracker = new EstatePitTimingTracker();
        tracker.Configure(Pit() with { ServiceZoneBoundary = [new(-20, 2, -10), new(-10, 2, -10), new(-10, 2, 10), new(-20, 2, 10)] });
        tracker.Observe(Position(4, -100, 100), Position(4, -90, 200), out _);
        Assert.IsFalse(tracker.Observe(Position(-15, 1, 1_000), Position(-15, -1, 1_100), out _));
        Assert.IsFalse(tracker.Observe(Position(-15, 0, 2_000), Position(-15, 0, 2_100), out _));
        Assert.IsFalse(tracker.Observe(Position(-5, -1, 3_000), Position(-5, 1, 3_100), out _));
        Assert.IsTrue(tracker.Observe(Position(-15, -1, 4_000), Position(-15, 1, 4_100), out _));
    }

    [TestMethod]
    public void ConcaveServiceBoxDoesNotFillTheGapBetweenItsTimingSpans()
    {
        var tracker = new EstatePitTimingTracker();
        tracker.Configure(Pit() with { ServiceZoneBoundary = [
            new(-30, 2, -10), new(-10, 2, -10), new(-10, 2, 10), new(-15, 2, 10),
            new(-15, 2, -5), new(-25, 2, -5), new(-25, 2, 10), new(-30, 2, 10)] });
        tracker.Observe(Position(4, -100, 100), Position(4, -90, 200), out _);
        Assert.IsTrue(tracker.Observe(Position(-27, -.1, 1_000), Position(-27, .1, 1_100), out _));
        Assert.IsFalse(tracker.Observe(Position(-20, -.1, 2_000), Position(-20, .1, 2_100), out _));
        Assert.IsTrue(tracker.Observe(Position(-12, -.1, 3_000), Position(-12, .1, 3_100), out _));
    }

    [TestMethod]
    public void LegacyCircularBoxWorksAndChangingTrackClearsConsumedState()
    {
        var tracker = new EstatePitTimingTracker();
        var pit = Pit() with { ServiceZoneBoundary = null, ServiceCenter = new(-10, 2, 0), ServiceRadiusMeters = 3 };
        tracker.Configure(pit);
        Assert.IsTrue(tracker.Observe(Position(-10, -.1, 1_000), Position(-10, .1, 1_100), out _));
        tracker.AcceptFinishCrossing();
        tracker.Configure(pit);
        Assert.IsTrue(tracker.Observe(Position(-10, -.1, 3_000), Position(-10, .1, 3_100), out _));
        tracker.Configure(null);
        Assert.IsFalse(tracker.Observe(Position(-10, -.1, 4_000), Position(-10, .1, 4_100), out _));
    }

    private static EstateTimedPosition Position(double x, double z, long time, double y = 2) => new(x, y, z, .3, time);
    private static EstateTimingGate Gate(double z) => new(new(0, 2, z), new(9, 2, z), 0, 1, 0, 0, 0);
    private static EstatePitDefinition Pit() => new(Gate(-95), Gate(95), [new(4.5, 2, -100), new(4.5, 2, 100)],
        new(-7, 2, 0), 3, 80, 3, 4.5,
        [new(-12.6, 2, 60), new(-1.8, 2, 61), new(-1.8, 2, -77), new(-12.5, 2, -79)], Gate(0));
}
