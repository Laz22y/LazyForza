namespace LazyForza.App;

internal sealed class MainWindowPageRefreshState
{
    private static readonly TimeSpan OverviewStorageRefreshInterval = TimeSpan.FromSeconds(2);
    private DateTimeOffset nextOverviewStorageRefreshAt = DateTimeOffset.MinValue;

    public int OverviewLapCount { get; private set; }
    public int OverviewTrackCount { get; private set; }

    public bool ShouldRefreshOverviewStorage(DateTimeOffset now) =>
        now >= nextOverviewStorageRefreshAt;

    public void UpdateOverviewStorage(int lapCount, int trackCount, DateTimeOffset refreshedAt)
    {
        OverviewLapCount = lapCount;
        OverviewTrackCount = trackCount;
        nextOverviewStorageRefreshAt = refreshedAt + OverviewStorageRefreshInterval;
    }

    public void InvalidateOverviewStorage() =>
        nextOverviewStorageRefreshAt = DateTimeOffset.MinValue;
}
