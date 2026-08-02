using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

public enum TrackMatchState
{
    Unknown,
    Candidate,
    Confirmed
}

public enum TrackLearningPhase
{
    WaitingForCompetition,
    WaitingForStartLine,
    CapturingReferenceLap,
    MatchingTrack,
    ComparingLaps
}

public sealed record LapHudState(
    DateTimeOffset UpdatedAt,
    TelemetrySourceKind Source,
    bool IsCompetitionActive,
    TrackLearningPhase Phase,
    string Status,
    string Instruction,
    TrackMatchState MatchState,
    double MatchConfidence,
    string TrackName,
    int CurrentSector,
    IReadOnlyList<SectorComparison> Sectors,
    double LearningProgress,
    int CompletedLaps,
    bool CurrentLapValid,
    bool ShowingPreviousLap = false)
{
    public Guid CompetitionSessionId { get; init; }
    public double CurrentLapSeconds { get; init; }
    public double? CumulativeHistoricalDeltaSeconds { get; init; }
    public bool MatchRejectionEligible { get; init; }
    public bool IsPointToPoint { get; init; }
}

internal static class LapHudDisplayTiming
{
    public static readonly TimeSpan CumulativeHistoricalDeltaDuration = TimeSpan.FromSeconds(2);

    public static TimeSpan CompletedLapHoldDuration(OverlayLayout layout) => TimeSpan.FromSeconds(
        Math.Clamp(layout.LapCompletedHoldSeconds, 0, 15));
}
