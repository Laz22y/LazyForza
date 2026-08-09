using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

public static class EstateRaceHudVisibilityPolicy
{
    public static bool ShouldShowPitLimiter(EstatePitServiceState pit) =>
        pit.SpeedLimitKph > 0 && (pit.IsApproachingPit || pit.IsInPitLane);

    public static bool ShouldShowPenaltyStatus(
        EstateRaceSession session,
        EstateRaceParticipant? participant,
        DateTimeOffset estimatedServerNow)
    {
        if (participant is null ||
            session.Phase is not (RaceSessionPhase.Race or RaceSessionPhase.Finished))
            return false;

        var reminderVisible = participant.DriveThroughReminderAt is DateTimeOffset reminderAt &&
                              estimatedServerNow >= reminderAt &&
                              estimatedServerNow - reminderAt <= TimeSpan.FromSeconds(5);
        return ((participant.PendingTimePenaltySeconds > 0 || participant.IsServingTimePenalty) &&
                participant.IsInPitLane) ||
               participant.IsServingDriveThrough ||
               reminderVisible ||
               participant.PenaltyServiceCompleted;
    }
}
