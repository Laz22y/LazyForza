using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed class EstateRaceLeaderboardRefreshCache(TimeSpan? refreshInterval = null)
{
    private readonly TimeSpan interval = refreshInterval ?? TimeSpan.FromSeconds(3);
    private readonly Dictionary<Guid, string> comparisons = [];
    private DateTimeOffset nextRefreshAt;
    private Guid? referenceId;

    public string Format(
        EstateRaceParticipant participant,
        EstateRaceParticipant? localParticipant,
        bool timedLap,
        bool race,
        IReadOnlyList<EstateRaceParticipant> participants,
        DateTimeOffset now,
        bool showPitStatus = true)
    {
        if (!race || IsImmediateStatus(participant))
            return EstateRaceLeaderboardFormatter.Format(
                participant,
                localParticipant,
                timedLap,
                race,
                participants.FirstOrDefault()?.CompletedLaps ?? 0,
                showPitStatus);

        if (now >= nextRefreshAt ||
            referenceId != localParticipant?.Id ||
            comparisons.Keys.Any(id => participants.All(item => item.Id != id)) ||
            participants.Any(item => !comparisons.ContainsKey(item.Id)))
        {
            comparisons.Clear();
            var leaderLaps = participants.FirstOrDefault()?.CompletedLaps ?? 0;
            foreach (var item in participants)
                comparisons[item.Id] = EstateRaceLeaderboardFormatter.Format(
                    item,
                    localParticipant,
                    qualifying: false,
                    race: true,
                    leaderLaps);
            referenceId = localParticipant?.Id;
            nextRefreshAt = now + interval;
        }
        return comparisons.GetValueOrDefault(participant.Id, "—");
    }

    private static bool IsImmediateStatus(EstateRaceParticipant participant) =>
        !participant.IsConnected ||
        participant.IsInPitLane ||
        participant.IsInServiceZone ||
        participant.Status is RaceParticipantStatus.Finished or
            RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified;
}
