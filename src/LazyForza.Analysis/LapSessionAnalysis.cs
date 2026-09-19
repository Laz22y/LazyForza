using LazyForza.Domain;

namespace LazyForza.Analysis;

public sealed record LapSessionAnalysis(
    LapSessionKey Key,
    LapSessionInfo? Info,
    IReadOnlyList<LapSummary> Laps,
    double RecordedSeconds,
    RaceReview Review)
{
    public DateTimeOffset StartedAt => Laps[0].StartedAt;
    public DateTimeOffset LastLapAt => Laps[^1].StartedAt;
}

public static class LapSessionAnalyzer
{
    // Never join sessions by time proximity. Missing identities remain individual records.
    public static IReadOnlyList<LapSessionAnalysis> Group(IReadOnlyList<LapSummary> laps) => laps
        .DistinctBy(lap => lap.Id)
        .GroupBy(LapSessionKey.FromLap)
        .Select(group =>
        {
            var ordered = group.OrderBy(lap => lap.StartedAt).ThenBy(lap => lap.Id).ToArray();
            return new LapSessionAnalysis(group.Key, ordered.FirstOrDefault(lap => lap.SessionInfo is not null)?.SessionInfo,
                ordered, ordered.Where(lap => double.IsFinite(lap.TotalSeconds) && lap.TotalSeconds > 0)
                    .Sum(lap => lap.TotalSeconds), RaceReviewAnalyzer.Analyze(ordered));
        })
        .OrderByDescending(session => session.LastLapAt)
        .ThenBy(session => session.Key.SessionId)
        .ToArray();
}
