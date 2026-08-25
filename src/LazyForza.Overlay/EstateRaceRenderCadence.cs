namespace LazyForza.Overlay;

internal static class EstateRaceRenderCadence
{
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan AnimationInterval = TimeSpan.FromMilliseconds(1000d / 30d);
    public static readonly TimeSpan IdleClockInterval = TimeSpan.FromMilliseconds(500);

    public static TimeSpan SelectInterval(bool reduceMotion, bool animationActive) =>
        !reduceMotion && animationActive
            ? AnimationInterval
            : SnapshotInterval;

    public static bool ShouldInvalidate(
        bool snapshotChanged,
        bool animationActive,
        bool hasSession,
        TimeSpan sinceLastInvalidation) =>
        snapshotChanged ||
        animationActive ||
        hasSession && sinceLastInvalidation >= IdleClockInterval;
}
