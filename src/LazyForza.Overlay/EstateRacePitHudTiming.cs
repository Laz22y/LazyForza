using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal static class EstateRacePitHudTiming
{
    private const double MaximumProjectionSeconds = 1;

    public static int ActiveParticipantCount(IEnumerable<EstateRaceParticipant> participants) =>
        participants.Count(participant =>
            participant.IsConnected && (participant.IsInPitLane || participant.IsInServiceZone));

    public static double ProjectElapsedSeconds(
        double reportedSeconds,
        DateTimeOffset reportedAt,
        DateTimeOffset estimatedServerNow,
        bool isRunning)
    {
        var normalized = double.IsFinite(reportedSeconds) ? Math.Max(0, reportedSeconds) : 0;
        if (!isRunning || estimatedServerNow <= reportedAt) return normalized;
        var elapsed = Math.Clamp(
            (estimatedServerNow - reportedAt).TotalSeconds,
            0,
            MaximumProjectionSeconds);
        return normalized + elapsed;
    }
}
