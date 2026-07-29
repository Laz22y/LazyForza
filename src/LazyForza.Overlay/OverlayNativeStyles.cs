namespace LazyForza.Overlay;

public static class OverlayNativeStyles
{
    public const long WsExTransparent = 0x00000020L;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExNoActivate = 0x08000000L;

    public static long Apply(long existing) =>
        existing | WsExTransparent | WsExToolWindow | WsExNoActivate;
}

public sealed class FrameRateLimiter(double maximumFramesPerSecond = 60)
{
    private readonly double minimumSeconds = 1 / maximumFramesPerSecond;
    private double previousSeconds = double.NegativeInfinity;

    public bool ShouldRender(double elapsedSeconds)
    {
        if (elapsedSeconds - previousSeconds + 1e-9 < minimumSeconds) return false;
        previousSeconds = elapsedSeconds;
        return true;
    }
}
