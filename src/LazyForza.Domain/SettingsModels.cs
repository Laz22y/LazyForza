namespace LazyForza.Domain;

public sealed record OverlayLayout(
    double Left = 579,
    double Top = 669,
    double Width = 1338.3333333333335,
    double Height = 753.3333333333334,
    double Scale = 0.6,
    double Opacity = 1,
    string MonitorId = "primary",
    bool ClickThrough = true,
    bool IsLocked = true,
    bool ReduceMotion = false,
    bool DashboardMotionEnabled = true,
    double DashboardMotionIntensity = 0.5,
    double DashboardIdleWaitSeconds = 2,
    double DashboardVisibilityFadeSeconds = 0.8,
    double LapCompletedHoldSeconds = 1,
    double LapNoMatchConfirmationSeconds = 8,
    double LapNoMatchFadeSeconds = 0.5,
    double LiveHudStaleSeconds = 0.8);

public static class OverlayScaleSettings
{
    public const double Minimum = 0.20;
    public const double Maximum = 1.50;
    public const double Step = 0.01;
    public const double Default = 0.60;

    public static double Normalize(double scale)
    {
        if (!double.IsFinite(scale)) return Default;
        var clamped = Math.Clamp(scale, Minimum, Maximum);
        return Math.Clamp(
            Math.Round(clamped / Step, MidpointRounding.AwayFromZero) * Step,
            Minimum,
            Maximum);
    }

    public static double ScaledDimension(double baseDimension, double scale)
    {
        var safeBaseDimension = double.IsFinite(baseDimension)
            ? Math.Max(1, baseDimension)
            : 1;
        return Math.Max(1, safeBaseDimension * Normalize(scale));
    }
}

public sealed record TelemetryOptions(
    string ListenAddress = "127.0.0.1",
    int Port = 2299,
    TimeSpan? StaleAfter = null,
    int SubscriberCapacity = 1)
{
    public TimeSpan EffectiveStaleAfter => StaleAfter ?? TimeSpan.FromSeconds(1.5);
}
