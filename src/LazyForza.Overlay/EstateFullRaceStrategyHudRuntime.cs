using LazyForza.Modules.EstateRace;

namespace LazyForza.Overlay;

internal sealed class EstateFullRaceStrategyHudRuntime
{
    internal static readonly TimeSpan RevealDelay = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(8);
    private double? formationLapStartedAtMonotonicSeconds;
    private RaceSessionPhase? previousPhase;
    private string? sessionKey;

    public FullRaceStrategyHudSnapshot Update(
        EstateRaceSession session,
        Guid? localParticipantId,
        EstatePitStrategyPrediction? prediction,
        DateTimeOffset estimatedServerNow,
        bool preview = false) =>
        Update(
            session,
            localParticipantId,
            prediction,
            estimatedServerNow,
            estimatedServerNow.UtcTicks / (double)TimeSpan.TicksPerSecond,
            preview);

    public FullRaceStrategyHudSnapshot Update(
        EstateRaceSession session,
        Guid? localParticipantId,
        EstatePitStrategyPrediction? prediction,
        DateTimeOffset estimatedServerNow,
        double monotonicNowSeconds,
        bool preview = false)
    {
        if (preview)
            return CreatePreview(session, prediction);

        // Keep the phase clock independently from the participant row. During
        // login synchronization or a short reconnect the server can briefly
        // omit/mark the local participant before the phase itself changes.
        UpdateFormationLapClock(session, estimatedServerNow, monotonicNowSeconds);

        if (localParticipantId is not Guid localId)
            return FullRaceStrategyHudSnapshot.Empty;

        var local = session.Participants.FirstOrDefault(item => item.Id == localId);
        if (local is null || local.Status is RaceParticipantStatus.Finished or
            RaceParticipantStatus.DidNotFinish or RaceParticipantStatus.Disqualified or
            RaceParticipantStatus.Disconnected)
            return FullRaceStrategyHudSnapshot.Empty;

        if (formationLapStartedAtMonotonicSeconds is not double startsAt ||
            session.Phase is not (RaceSessionPhase.FormationLap or
                RaceSessionPhase.Countdown or RaceSessionPhase.Race))
            return FullRaceStrategyHudSnapshot.Empty;

        var elapsedSeconds = Math.Max(0, monotonicNowSeconds - startsAt);
        if (elapsedSeconds < RevealDelay.TotalSeconds ||
            elapsedSeconds >= (RevealDelay + HoldDuration).TotalSeconds)
            return FullRaceStrategyHudSnapshot.Empty;

        return CreateSnapshot(session, local, prediction);
    }

    private void UpdateFormationLapClock(
        EstateRaceSession session,
        DateTimeOffset estimatedServerNow,
        double monotonicNowSeconds)
    {
        // Track package metadata can finish synchronizing after login, so it
        // must not be part of this key or the reveal clock would restart.
        var nextSessionKey = $"{session.TrackId}\u001f{session.SessionName}\u001f{session.TotalRaceLaps}";
        if (!string.Equals(sessionKey, nextSessionKey, StringComparison.Ordinal))
        {
            formationLapStartedAtMonotonicSeconds = null;
            previousPhase = null;
            sessionKey = nextSessionKey;
        }

        if (session.Phase == RaceSessionPhase.FormationLap &&
            previousPhase != RaceSessionPhase.FormationLap)
        {
            var observedAge = session.Banner is
            {
                Kind: RaceBannerKind.Information,
                Title: "暖胎圈"
            } formationBanner && formationBanner.CreatedAt <= estimatedServerNow
                ? estimatedServerNow - formationBanner.CreatedAt
                : TimeSpan.Zero;
            // The banner is short-lived. Once its server timestamp has seeded
            // the age, advance only on the local monotonic clock so a clock
            // resync or latency spike cannot skip the reveal window.
            formationLapStartedAtMonotonicSeconds = monotonicNowSeconds -
                Math.Clamp(observedAge.TotalSeconds, 0, RevealDelay.TotalSeconds);
        }
        else if (session.Phase is RaceSessionPhase.Lobby or RaceSessionPhase.Practice or
                 RaceSessionPhase.Qualifying or RaceSessionPhase.Grid or
                 RaceSessionPhase.OutLap or RaceSessionPhase.Finished)
        {
            formationLapStartedAtMonotonicSeconds = null;
        }

        previousPhase = session.Phase;
    }

    internal static FullRaceStrategyHudSnapshot CreateSnapshot(
        EstateRaceSession session,
        EstateRaceParticipant local,
        EstatePitStrategyPrediction? prediction)
    {
        var totalLaps = Math.Max(1, session.TotalRaceLaps);
        var currentLap = Math.Clamp(local.CompletedLaps + 1, 1, totalLaps);
        var completedStops = Math.Max(0, prediction?.CompletedPitStops ?? 0);
        var minimumStops = Math.Max(
            Math.Max(0, session.MinimumRequiredPitStops),
            Math.Max(0, prediction?.MinimumRequiredPitStops ?? 0));
        var requiredRemaining = Math.Max(0, minimumStops - completedStops);
        if (prediction is { RemainingRequiredPitStops: > 0 })
            requiredRemaining = Math.Max(requiredRemaining, prediction.RemainingRequiredPitStops);

        var hasSuggestedWindow = TryGetSuggestedWindow(prediction, totalLaps, out var suggestedStart, out var suggestedEnd);
        if (requiredRemaining == 0 && hasSuggestedWindow)
            requiredRemaining = 1;

        var maximumUsefulStops = Math.Max(0, totalLaps - currentLap);
        requiredRemaining = Math.Min(requiredRemaining, maximumUsefulStops);
        var windows = BuildWindows(
            totalLaps,
            currentLap,
            requiredRemaining,
            hasSuggestedWindow ? suggestedStart : null,
            hasSuggestedWindow ? suggestedEnd : null);
        var stints = BuildStints(totalLaps, windows);
        var confidence = prediction is null || prediction.Decision is
            EstatePitStrategyDecision.Unavailable or EstatePitStrategyDecision.Collecting
            ? EstatePitStrategyConfidence.Low
            : prediction.Confidence;

        return new FullRaceStrategyHudSnapshot(
            true,
            totalLaps,
            minimumStops,
            completedStops,
            windows,
            stints,
            FiniteNonNegative(prediction?.EstimatedPitLossSeconds),
            Finite(prediction?.ProjectedAdvantageSeconds),
            FinitePositive(prediction?.RepresentativeLapSeconds),
            confidence,
            prediction is not null && prediction.Decision is not
                (EstatePitStrategyDecision.Unavailable or EstatePitStrategyDecision.Collecting),
            hasSuggestedWindow,
            prediction is { HistoricalSampleCount: > 0 } or { UsesHistoricalPace: true });
    }

    private static FullRaceStrategyHudSnapshot CreatePreview(
        EstateRaceSession session,
        EstatePitStrategyPrediction? prediction)
    {
        const int totalLaps = 20;
        var minimumStops = Math.Max(1, session.MinimumRequiredPitStops);
        var windows = BuildWindows(totalLaps, 1, minimumStops, 7, Math.Min(9, totalLaps - 1));
        return new FullRaceStrategyHudSnapshot(
            true,
            totalLaps,
            minimumStops,
            0,
            windows,
            BuildStints(totalLaps, windows),
            FiniteNonNegative(prediction?.EstimatedPitLossSeconds) ?? 22.4,
            Finite(prediction?.ProjectedAdvantageSeconds) ?? 4.8,
            FinitePositive(prediction?.RepresentativeLapSeconds),
            prediction?.Confidence ?? EstatePitStrategyConfidence.Medium,
            true,
            true,
            true);
    }

    private static IReadOnlyList<FullRaceStrategyStopWindow> BuildWindows(
        int totalLaps,
        int currentLap,
        int stopCount,
        int? suggestedStart,
        int? suggestedEnd)
    {
        stopCount = Math.Min(Math.Max(0, stopCount), Math.Max(0, totalLaps - currentLap));
        if (stopCount <= 0 || totalLaps <= 1) return [];
        var windows = new List<FullRaceStrategyStopWindow>(stopCount);
        var previousTarget = Math.Max(0, currentLap - 1);
        for (var index = 0; index < stopCount; index++)
        {
            var remainingAfter = stopCount - index - 1;
            var minimumTarget = previousTarget + 1;
            var maximumTarget = totalLaps - 1 - remainingAfter;
            if (minimumTarget > maximumTarget) break;
            int start;
            int end;
            if (index == 0 && suggestedStart is int firstStart && suggestedEnd is int firstEnd)
            {
                start = Math.Clamp(firstStart, minimumTarget, maximumTarget);
                end = Math.Clamp(firstEnd, start, maximumTarget);
            }
            else
            {
                var remainingStops = stopCount - index;
                var target = (int)Math.Round(
                    previousTarget + (totalLaps - previousTarget) / (double)(remainingStops + 1),
                    MidpointRounding.AwayFromZero);
                target = Math.Clamp(target, minimumTarget, maximumTarget);
                start = Math.Max(minimumTarget, target - 1);
                end = Math.Min(maximumTarget, target + 1);
            }

            var targetLap = Math.Clamp(
                (int)Math.Round((start + end) / 2d, MidpointRounding.AwayFromZero),
                minimumTarget,
                maximumTarget);
            start = Math.Clamp(start, minimumTarget, targetLap);
            end = Math.Clamp(end, targetLap, maximumTarget);
            windows.Add(new FullRaceStrategyStopWindow(index + 1, start, end, targetLap));
            previousTarget = targetLap;
        }
        return windows;
    }

    private static IReadOnlyList<FullRaceStrategyStint> BuildStints(
        int totalLaps,
        IReadOnlyList<FullRaceStrategyStopWindow> windows)
    {
        var result = new List<FullRaceStrategyStint>(windows.Count + 1);
        var startLap = 1;
        for (var index = 0; index < windows.Count; index++)
        {
            var endLap = Math.Clamp(windows[index].TargetLap, startLap, totalLaps);
            result.Add(new FullRaceStrategyStint(index + 1, startLap, endLap));
            startLap = Math.Min(totalLaps, endLap + 1);
        }
        result.Add(new FullRaceStrategyStint(result.Count + 1, startLap, totalLaps));
        return result;
    }

    private static bool TryGetSuggestedWindow(
        EstatePitStrategyPrediction? prediction,
        int totalLaps,
        out int? start,
        out int? end)
    {
        start = null;
        end = null;
        if (prediction is null || prediction.Decision is not
            (EstatePitStrategyDecision.PitWindow or EstatePitStrategyDecision.PitThisLap) ||
            prediction.PitWindowStartLap is not int rawStart ||
            prediction.PitWindowEndLap is not int rawEnd || totalLaps <= 1)
            return false;
        start = Math.Clamp(rawStart, 1, totalLaps - 1);
        end = Math.Clamp(rawEnd, start.Value, totalLaps - 1);
        return true;
    }

    private static double? Finite(double? value) =>
        value is double number && double.IsFinite(number) ? number : null;

    private static double? FinitePositive(double? value) =>
        value is double number && double.IsFinite(number) && number > 0 ? number : null;

    private static double? FiniteNonNegative(double? value) =>
        value is double number && double.IsFinite(number) && number >= 0 ? number : null;
}

internal sealed record FullRaceStrategyHudSnapshot(
    bool IsVisible,
    int TotalLaps,
    int MinimumRequiredStops,
    int CompletedStops,
    IReadOnlyList<FullRaceStrategyStopWindow> StopWindows,
    IReadOnlyList<FullRaceStrategyStint> Stints,
    double? EstimatedPitLossSeconds,
    double? ProjectedAdvantageSeconds,
    double? RepresentativeLapSeconds,
    EstatePitStrategyConfidence Confidence,
    bool HasLiveEvidence,
    bool UsesSuggestedWindow,
    bool HasHistoricalEvidence)
{
    public int RemainingRequiredStops => Math.Max(0, MinimumRequiredStops - CompletedStops);

    public static FullRaceStrategyHudSnapshot Empty { get; } = new(
        false,
        0,
        0,
        0,
        [],
        [],
        null,
        null,
        null,
        EstatePitStrategyConfidence.Low,
        false,
        false,
        false);
}

internal sealed record FullRaceStrategyStopWindow(
    int Number,
    int StartLap,
    int EndLap,
    int TargetLap);

internal sealed record FullRaceStrategyStint(
    int Number,
    int StartLap,
    int EndLap);
