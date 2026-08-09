using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

public static class EstateRaceLeaderboardFormatter
{
    public static string FormatLeaderComparison(EstateRaceParticipant participant, int leaderCompletedLaps)
    {
        if (participant.Position == 1) return "LEADER";
        if (participant.GapToLeaderSeconds is double gap) return FormatDelta(gap);
        return $"+{Math.Max(1, leaderCompletedLaps - participant.CompletedLaps)} LAP";
    }

    public static string Format(
        EstateRaceParticipant participant,
        bool qualifying,
        bool race,
        bool local,
        double? gapBehindSeconds,
        int leaderCompletedLaps)
    {
        if (participant.Status == RaceParticipantStatus.Disqualified) return "DSQ";
        if (participant.Status == RaceParticipantStatus.DidNotFinish) return "DNF";
        if (!participant.IsConnected) return "OFFLINE";
        if (participant.IsInPitLane || participant.IsInServiceZone) return "IN PIT";

        if (qualifying)
        {
            if (participant.BestLapSeconds is not double bestLap) return "NO TIME";
            return local ? FormatLapTime(bestLap) : FormatDelta(participant.GapToLeaderSeconds);
        }

        if (race && local)
            return $"{FormatAheadInterval(participant.IntervalSeconds)} / {FormatBehindInterval(gapBehindSeconds)}";

        if (participant.GapToLeaderSeconds is double gap)
            return FormatDelta(gap);
        return participant.Position == 1
            ? FormatDelta(0)
            : $"+{Math.Max(1, leaderCompletedLaps - participant.CompletedLaps)} LAP";
    }

    private static string FormatLapTime(double seconds)
    {
        var minutes = (int)(seconds / 60);
        var remainder = seconds - minutes * 60;
        return $"{minutes}:{remainder:00.000}";
    }

    private static string FormatDelta(double? seconds)
    {
        if (seconds is not double value || !double.IsFinite(value)) return "—";
        return $"{value:+0.000;-0.000;±0.000}";
    }

    private static string FormatAheadInterval(double? seconds) =>
        seconds is double value && double.IsFinite(value) && value >= 0
            ? $"−{value:0.000}"
            : "—";

    private static string FormatBehindInterval(double? seconds) =>
        seconds is double value && double.IsFinite(value) && value >= 0
            ? $"+{value:0.000}"
            : "—";
}
