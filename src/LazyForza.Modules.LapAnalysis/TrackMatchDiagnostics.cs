using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

public sealed record TrackMatchCandidateDiagnostic(
    string TrackName,
    TrackLayoutKind LayoutKind,
    string? Category,
    double LengthMeters,
    string Stage,
    double? StartDistanceMeters,
    double? MeanDistanceMeters,
    double ProgressMeters,
    double ValidRatio,
    string? EliminationReason);

public sealed record TrackMatchDiagnostics(
    DateTimeOffset UpdatedAt,
    string State,
    int TotalRoutes,
    int CoarseEligibleRoutes,
    int FineCandidateRoutes,
    IReadOnlyList<TrackMatchCandidateDiagnostic> TopCandidates,
    IReadOnlyList<TrackMatchCandidateDiagnostic> EliminatedCandidates)
{
    public static TrackMatchDiagnostics Empty { get; } =
        new(DateTimeOffset.MinValue, "等待比赛", 0, 0, 0, [], []);
}
