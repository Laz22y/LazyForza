using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

public static class EstateRaceLeaderboardFormatter
{
    public static string FormatFinished(
        EstateRaceParticipant participant,
        EstateRaceParticipant? leader,
        int leaderCompletedLaps)
    {
        if (participant.Position == 1) return "WINNER";
        if (participant.Status == RaceParticipantStatus.Disqualified) return "DSQ";
        if (participant.Status == RaceParticipantStatus.DidNotFinish) return "DNF";

        var adjustedGap = participant.AdjustedRaceTotalSeconds is double participantTime &&
                          double.IsFinite(participantTime) &&
                          leader?.AdjustedRaceTotalSeconds is double leaderTime &&
                          double.IsFinite(leaderTime)
            ? participantTime - leaderTime
            : participant.GapToLeaderSeconds;
        if (adjustedGap is double gap && double.IsFinite(gap))
            return FormatDelta(Math.Max(0, gap));

        var lapDeficit = Math.Max(0, leaderCompletedLaps - participant.CompletedLaps);
        return lapDeficit > 0
            ? $"+{lapDeficit} {(lapDeficit == 1 ? "LAP" : "LAPS")}"
            : "—";
    }

    public static string FormatLeaderComparison(
        EstateRaceParticipant participant,
        EstateRaceParticipant? leader,
        int leaderCompletedLaps)
    {
        if (participant.Position == 1) return "LEADER";
        if (participant.GapToLeaderSeconds is double gap) return FormatDelta(gap);
        var lapDeficit = leader is null
            ? Math.Max(0, leaderCompletedLaps - participant.CompletedLaps)
            : Math.Max(0, WholeLapDelta(leader, participant));
        return lapDeficit > 0
            ? $"+{lapDeficit} {(lapDeficit == 1 ? "LAP" : "LAPS")}"
            : "—";
    }

    public static string Format(
        EstateRaceParticipant participant,
        EstateRaceParticipant? localParticipant,
        bool qualifying,
        bool race,
        int leaderCompletedLaps,
        bool showPitStatus = true,
        EstateRaceParticipant? leaderParticipant = null)
    {
        if (participant.Status == RaceParticipantStatus.Disqualified) return "DSQ";
        if (participant.Status == RaceParticipantStatus.DidNotFinish) return "DNF";
        if (!participant.IsConnected) return "OFFLINE";
        if (showPitStatus && (participant.IsInPitLane || participant.IsInServiceZone)) return "IN PIT";

        var local = participant.Id == localParticipant?.Id;
        if (qualifying)
        {
            if (participant.BestLapSeconds is not double bestLap) return "NO TIME";
            if (localParticipant?.BestLapSeconds is double localBestLap)
                return local
                    ? FormatLapTime(bestLap)
                    : FormatDelta(bestLap - localBestLap);

            var leader = leaderParticipant?.BestLapSeconds is double
                ? leaderParticipant
                : participant.Position == 1
                    ? participant
                    : null;
            if (leader?.BestLapSeconds is double leaderBestLap)
                return participant.Id == leader.Id
                    ? FormatLapTime(bestLap)
                    : FormatDelta(bestLap - leaderBestLap);

            return participant.Position == 1
                ? FormatLapTime(bestLap)
                : FormatDelta(participant.GapToLeaderSeconds);
        }

        if (local) return "REFERENCE";
        if (localParticipant is null)
            return FormatLeaderComparison(participant, leaderParticipant, leaderCompletedLaps);

        if (participant.RaceDeltaSecondsByReference is { } directDeltas)
        {
            if (directDeltas.TryGetValue(localParticipant.Id, out var directDelta) &&
                double.IsFinite(directDelta))
                return FormatDelta(directDelta);
        }
        else
        {
            // Compatibility with servers that predate direct pairwise race Delta.
            var participantGap = GapToLeader(participant);
            var localGap = GapToLeader(localParticipant);
            if (participantGap is double otherGap && localGap is double referenceGap)
                return FormatDelta(otherGap - referenceGap);
        }

        var lapDelta = WholeLapDelta(localParticipant, participant);
        if (lapDelta != 0)
            return $"{lapDelta:+0;-0} {(Math.Abs(lapDelta) == 1 ? "LAP" : "LAPS")}";
        return "—";
    }

    private static int WholeLapDelta(
        EstateRaceParticipant reference,
        EstateRaceParticipant participant)
    {
        var difference = RaceDistance(reference) - RaceDistance(participant);
        var fullLaps = (int)Math.Floor(Math.Abs(difference) + 1e-9);
        return Math.Sign(difference) * fullLaps;
    }

    private static double RaceDistance(EstateRaceParticipant participant) =>
        Math.Max(0, participant.CompletedLaps) +
        Math.Clamp(double.IsFinite(participant.TrackProgress) ? participant.TrackProgress : 0, 0, 1);

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
