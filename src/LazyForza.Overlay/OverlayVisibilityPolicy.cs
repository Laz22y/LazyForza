using LazyForza.Domain;
using LazyForza.Modules.Dashboard;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.Overlay;

/// <summary>Centralizes HUD visibility so menu/pause and free-roam behavior is testable without a window.</summary>
public static class OverlayVisibilityPolicy
{
    public static bool ShouldShowDashboard(
        DashboardHudState? state,
        DateTimeOffset now,
        double liveStaleSeconds = 0.8) =>
        state is { IsDriving: true } &&
        (state.Source != TelemetrySourceKind.Live ||
         now - state.UpdatedAt <= TimeSpan.FromSeconds(Math.Clamp(liveStaleSeconds, 0.05, 10)));

    public static bool ShouldShowLap(
        LapHudState? state,
        DateTimeOffset now,
        double liveStaleSeconds = 0.8) =>
        state is { IsCompetitionActive: true } &&
        (state.Source != TelemetrySourceKind.Live ||
         now - state.UpdatedAt <= TimeSpan.FromSeconds(Math.Clamp(liveStaleSeconds, 0.05, 10)));
}
