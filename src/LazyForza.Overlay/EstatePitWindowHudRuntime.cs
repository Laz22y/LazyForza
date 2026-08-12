using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed class EstatePitWindowHudRuntime
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(6);
    private Guid? participantId;
    private int? lastCompletedLaps;
    private int? lastShownLap;
    private PitWindowHudSnapshot active = PitWindowHudSnapshot.Empty;

    public PitWindowHudSnapshot Update(
        EstateRaceSession session,
        Guid? localParticipantId,
        EstatePitStrategyPrediction? prediction,
        DateTimeOffset now,
        bool preview = false)
    {
        if (preview)
            return new PitWindowHudSnapshot(true, 7, 9, 2, false, 0.38, now + HoldDuration);

        var local = localParticipantId is Guid localId
            ? session.Participants.FirstOrDefault(item => item.Id == localId)
            : null;
        if (session.Phase != RaceSessionPhase.Race || local is null ||
            local.Status is RaceParticipantStatus.Finished or RaceParticipantStatus.DidNotFinish or
                RaceParticipantStatus.Disqualified or RaceParticipantStatus.Disconnected)
        {
            Reset();
            return PitWindowHudSnapshot.Empty;
        }

        if (participantId != local.Id || lastCompletedLaps is not int previousCompletedLaps ||
            local.CompletedLaps < previousCompletedLaps)
        {
            participantId = local.Id;
            lastCompletedLaps = local.CompletedLaps;
            lastShownLap = null;
            active = PitWindowHudSnapshot.Empty;
            return active;
        }

        var crossedFinishLine = local.CompletedLaps > previousCompletedLaps;
        lastCompletedLaps = local.CompletedLaps;
        if (local.IsInPitLane || local.IsInServiceZone)
        {
            active = PitWindowHudSnapshot.Empty;
            return active;
        }

        var currentLap = local.CompletedLaps + 1;
        if (crossedFinishLine && lastShownLap != currentLap)
        {
            lastShownLap = currentLap;
            active = CreateSnapshot(prediction, currentLap, now);
        }

        if (!active.IsVisible || now >= active.VisibleUntil)
            active = PitWindowHudSnapshot.Empty;
        return active;
    }

    public void Reset()
    {
        participantId = null;
        lastCompletedLaps = null;
        lastShownLap = null;
        active = PitWindowHudSnapshot.Empty;
    }

    private static PitWindowHudSnapshot CreateSnapshot(
        EstatePitStrategyPrediction? prediction,
        int currentLap,
        DateTimeOffset now)
    {
        if (prediction is null ||
            prediction.Decision is not (EstatePitStrategyDecision.PitWindow or EstatePitStrategyDecision.PitThisLap) ||
            prediction.PitWindowStartLap is not int startLap ||
            prediction.PitWindowEndLap is not int endLap)
            return PitWindowHudSnapshot.Empty;

        startLap = Math.Max(1, startLap);
        endLap = Math.Max(startLap, endLap);
        if (currentLap > endLap) return PitWindowHudSnapshot.Empty;
        var lapsUntilWindow = Math.Max(0, startLap - currentLap);
        if (lapsUntilWindow > 2) return PitWindowHudSnapshot.Empty;

        var degradation = prediction.DegradationPerLapSeconds is double value &&
                          double.IsFinite(value) && value >= 0
            ? value
            : (double?)null;
        return new PitWindowHudSnapshot(
            true,
            startLap,
            endLap,
            lapsUntilWindow,
            currentLap >= startLap,
            degradation,
            now + HoldDuration);
    }
}

internal sealed record PitWindowHudSnapshot(
    bool IsVisible,
    int StartLap,
    int EndLap,
    int LapsUntilWindow,
    bool WindowOpen,
    double? DegradationPerLapSeconds,
    DateTimeOffset VisibleUntil)
{
    public static PitWindowHudSnapshot Empty { get; } = new(
        false,
        0,
        0,
        0,
        false,
        null,
        DateTimeOffset.MinValue);
}
