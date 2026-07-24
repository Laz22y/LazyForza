namespace LazyForza.Domain;

public static class LazyForzaDefaults
{
    public const string TelemetryListenAddress = "127.0.0.1";
    public const int TelemetryPort = 2299;

    public static OverlayLayout CreateOverlayLayout() => new();
}
