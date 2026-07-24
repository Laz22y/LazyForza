namespace LazyForza.Domain;

/// <summary>
/// Conservative context classification using only official Data Out fields.
/// FH6 does not expose an explicit event/track identifier, so competition detection remains a documented heuristic.
/// </summary>
public static class TelemetryContextClassifier
{
    public static bool IsDriving(Fh6RawTelemetry telemetry) => telemetry.IsRaceOn == 1;

    public static bool IsCompetition(Fh6RawTelemetry telemetry) =>
        IsDriving(telemetry) &&
        telemetry.RacePosition is > 0 and <= 64 &&
        telemetry.CurrentRaceTime > 0.05f;

}
