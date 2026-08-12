namespace LazyForza.Domain;

public static class LazyForzaDefaults
{
    public const string TelemetryListenAddress = "127.0.0.1";
    public const int TelemetryPort = 2299;

    public static OverlayLayout CreateOverlayLayout() => new(
        Left: 622.5,
        Top: 688,
        LapHudLeft: 622.5,
        LapHudTop: 688,
        LapHudScale: 0.6,
        LapHudAttachedToDashboard: true,
        DriftHudLeft: 1245,
        DriftHudTop: 669,
        DriftHudScale: 0.6,
        DashboardWidgets: DashboardWidgetLayoutSettings.CreateDefault(),
        EstateRaceWidgets: EstateRaceHudLayoutSettings.Default,
        EstateRaceHudLeft: 0,
        EstateRaceHudTop: 0,
        EstateRaceHudWidth: 2048,
        EstateRaceHudHeight: 1152);
}
