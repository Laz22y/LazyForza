using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class MainWindowPageRefreshStateTests
{
    [TestMethod]
    public void OverviewStorageCountsAreCachedBetweenUiRefreshTicks()
    {
        var state = new MainWindowPageRefreshState();
        var now = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(state.ShouldRefreshOverviewStorage(now));

        state.UpdateOverviewStorage(42, 7, now);

        Assert.AreEqual(42, state.OverviewLapCount);
        Assert.AreEqual(7, state.OverviewTrackCount);
        Assert.IsFalse(state.ShouldRefreshOverviewStorage(now + TimeSpan.FromMilliseconds(500)));
        Assert.IsFalse(state.ShouldRefreshOverviewStorage(now + TimeSpan.FromMilliseconds(1_999)));
        Assert.IsTrue(state.ShouldRefreshOverviewStorage(now + TimeSpan.FromSeconds(2)));

        state.InvalidateOverviewStorage();

        Assert.IsTrue(state.ShouldRefreshOverviewStorage(now));
    }
}
