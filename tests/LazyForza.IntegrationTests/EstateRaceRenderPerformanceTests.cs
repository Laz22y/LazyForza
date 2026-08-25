using System.Windows;
using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateRaceRenderPerformanceTests
{
    [TestMethod]
    public void NormalSnapshotsStayAtNetworkCadenceWhileAnimationsUseThirtyFps()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(100),
            EstateRaceRenderCadence.SelectInterval(reduceMotion: false, animationActive: false));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(1000d / 30d),
            EstateRaceRenderCadence.SelectInterval(reduceMotion: false, animationActive: true));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(100),
            EstateRaceRenderCadence.SelectInterval(reduceMotion: true, animationActive: true));
    }

    [TestMethod]
    public void UnchangedSessionOnlyRefreshesItsClockAtIdleCadence()
    {
        Assert.IsFalse(EstateRaceRenderCadence.ShouldInvalidate(
            snapshotChanged: false,
            animationActive: false,
            hasSession: true,
            sinceLastInvalidation: TimeSpan.FromMilliseconds(499)));
        Assert.IsTrue(EstateRaceRenderCadence.ShouldInvalidate(
            snapshotChanged: false,
            animationActive: false,
            hasSession: true,
            sinceLastInvalidation: TimeSpan.FromMilliseconds(500)));
        Assert.IsFalse(EstateRaceRenderCadence.ShouldInvalidate(
            snapshotChanged: false,
            animationActive: false,
            hasSession: false,
            sinceLastInvalidation: TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public void MapGeometryIsReusedUntilItsSourceOrViewportChanges()
    {
        IReadOnlyList<EstateRaceMapPoint> points =
        [
            new EstateRaceMapPoint(0, 0),
            new EstateRaceMapPoint(0.5, 0.75),
            new EstateRaceMapPoint(1, 1)
        ];
        var cache = new EstateRaceMapGeometryCache();
        var viewport = new Rect(10, 20, 300, 180);

        var first = cache.Track(points, viewport);
        var second = cache.Track(points, viewport);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, cache.BuildCount);

        var resized = cache.Track(points, new Rect(10, 20, 320, 180));

        Assert.AreNotSame(first, resized);
        Assert.AreEqual(2, cache.BuildCount);
    }

    [TestMethod]
    public void RepeatedHudColorsReuseFrozenBrushes()
    {
        var first = OverlayBrushCache.Get(0x38, 0xD5, 0xE8, 0.82);
        var second = OverlayBrushCache.Get(0x38, 0xD5, 0xE8, 0.82);

        Assert.AreSame(first, second);
        Assert.IsTrue(first.IsFrozen);
        Assert.AreEqual(Math.Round(0.82 * byte.MaxValue) / byte.MaxValue, first.Opacity, 1e-9);
    }
}
