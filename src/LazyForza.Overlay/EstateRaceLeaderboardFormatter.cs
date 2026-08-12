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
        EstateRaceParticipant? localParticipant,
        bool qualifying,
        bool race,
        int leaderCompletedLaps)
    {
        if (participant.Status == RaceParticipantStatus.Disqualified) return "DSQ";
        if (participant.Status == RaceParticipantStatus.DidNotFinish) return "DNF";
        if (!participant.IsConnected) return "OFFLINE";
        if (participant.IsInPitLane || participant.IsInServiceZone) return "IN PIT";

        var local = participant.Id == localParticipant?.Id;
        if (qualifying)
        {
            if (participant.BestLapSeconds is not double bestLap) return "NO TIME";
            if (localParticipant is null)
                return participant.Position == 1
                    ? FormatLapTime(bestLap)
                    : FormatDelta(participant.GapToLeaderSeconds);
            if (local) return FormatLapTime(bestLap);
            return localParticipant?.BestLapSeconds is double localBestLap
                ? FormatDelta(bestLap - localBestLap)
                : "—";
        }

        if (local) return "REFERENCE";
        if (localParticipant is null)
            return FormatLeaderComparison(participant, leaderCompletedLaps);

        var participantGap = GapToLeader(participant);
        var localGap = GapToLeader(localParticipant);
        if (participantGap is double otherGap && localGap is double referenceGap)
            return FormatDelta(otherGap - referenceGap);

        var lapDelta = localParticipant.CompletedLaps - participant.CompletedLaps;
        if (lapDelta != 0)
            return $"{lapDelta:+0;-0} {(Math.Abs(lapDelta) == 1 ? "LAP" : "LAPS")}";
        return "—";
    }

    private static double? GapToLeader(EstateRaceParticipant participant) =>
        participant.Position == 1
            ? 0
            : participant.GapToLeaderSeconds is double gap && double.IsFinite(gap)
                ? gap
                : null;

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

}
