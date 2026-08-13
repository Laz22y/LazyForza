using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

public static class EstateRaceHudVisibilityPolicy
{
    public static bool ShouldShowBanner(
        EstateRaceSession session,
        EstateRaceBanner? banner,
        DateTimeOffset estimatedServerNow) =>
        banner is not null &&
        !(session.Phase == RaceSessionPhase.Finished && banner.Kind == RaceBannerKind.Winner) &&
        (banner.ExpiresAt is null || banner.ExpiresAt > estimatedServerNow);

    public static bool ShouldShowPracticeProgram(
        EstateRaceHudState state,
        DateTimeOffset? now = null) =>
        state.IsConnected && !state.IsObserver &&
        state.Session?.Phase == RaceSessionPhase.Practice &&
        state.PracticeTests?.Items.Any(item => item.IsVisibleOnHud(now ?? DateTimeOffset.UtcNow)) == true;

    public static bool ShouldShowPitLimiter(EstatePitServiceState pit) =>
        pit.SpeedLimitKph > 0 && (pit.IsApproachingPit || pit.IsInPitLane);

    public static bool ShouldShowPenaltyStatus(
        EstateRaceSession session,
        EstateRaceParticipant? participant,
        DateTimeOffset estimatedServerNow)
    {
        if (participant is null || session.Phase != RaceSessionPhase.Race ||
            participant.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
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
