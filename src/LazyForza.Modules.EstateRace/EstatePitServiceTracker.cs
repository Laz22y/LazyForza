using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal sealed class EstatePitServiceTracker
{
    private const double GateProgressToleranceMeters = 0.35;
    private const double StationarySpeedKph = 1.5;
    private const double MovementResetSpeedKph = 3;
    private const double ServiceZoneBoundaryToleranceMeters = 1;
    private static readonly TimeSpan ServiceZoneGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MovementGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CreepingGrace = TimeSpan.FromMilliseconds(750);
    private DateTimeOffset? previousArrival;
    private bool wasEligible;
    private bool creditedThisVisit;
    private double elapsedSeconds;
    private double pitLaneElapsedSeconds;
    private int completedServices;
    private bool pitLaneActive;
    private Vector3F? previousPosition;
    private double? previousRouteProgress;
    private DateTimeOffset? outsidePitCorridorSince;
    private DateTimeOffset? serviceZoneLastConfirmedAt;
    private DateTimeOffset? movementInterruptedAt;
    private Guid? serviceVisitId;
    private EstatePitDefinition? cachedPit;
    private double cachedEntryProgress;
    private double cachedExitProgress;

    internal int GateProgressRefreshCount { get; private set; }

    public EstatePitServiceState Current { get; private set; } = EstatePitServiceState.Empty;

    public EstatePitServiceState Observe(
        TelemetryFrame frame,
        EstatePitDefinition? pit,
        bool telemetryValid,
        bool serviceBlocked = false)
    {
        if (pit is null)
        {
            Reset();
            return Current;
        }
        var required = Math.Clamp(pit.MinimumServiceSeconds, 1, 60);
        var deltaSeconds = previousArrival is DateTimeOffset previous && frame.ArrivalTime > previous
            ? Math.Min(2, (frame.ArrivalTime - previous).TotalSeconds)
            : 0;
        // The game timestamp freezes while FH6 is paused. Once the car has been
        // confirmed stationary in the service zone, count trusted wall-clock
        // time and keep the last zone state until usable position data returns.
        if (!telemetryValid)
        {
            if (pitLaneActive) pitLaneElapsedSeconds += deltaSeconds;
            AccumulateUntil(frame.ArrivalTime);
            CreditCompletedService(required);
            previousArrival = frame.ArrivalTime;
            Current = Current with
            {
                RequiredSeconds = required,
                ElapsedSeconds = elapsedSeconds,
                RequirementMet = creditedThisVisit,
                CompletedServices = completedServices,
                IsCounting = wasEligible,
                CountingUpdatedAt = frame.ArrivalTime,
                PitLaneElapsedSeconds = pitLaneElapsedSeconds,
                VisitId = serviceVisitId,
                ProgressState = creditedThisVisit
                    ? EstatePitServiceProgressState.Completed
                    : wasEligible
                        ? EstatePitServiceProgressState.Counting
                        : Current.ProgressState
            };
            return Current;
        }
        var position = frame.Raw.Position;
        var route = EstateRaceGeometry.ProjectPitRoute(pit, position);
        EnsureGateProgress(pit);
        var entryProgress = cachedEntryProgress;
        var exitProgress = cachedExitProgress;
        var halfWidth = Math.Clamp(pit.LaneHalfWidthMeters, 1, 20);
        var corridorMatch = route.DistanceMeters <= halfWidth;
        var reverseProgressTolerance = Math.Clamp(halfWidth * 0.25, 0.5, 1.5);
        var routeDirectionCompatible = previousRouteProgress is double previousDirectionProgress &&
                                       route.ProgressMeters >= previousDirectionProgress - reverseProgressTolerance;
        var betweenEnforcementGates = corridorMatch &&
                                      route.ProgressMeters >= entryProgress - GateProgressToleranceMeters &&
                                      route.ProgressMeters <= exitProgress + GateProgressToleranceMeters;
        var routeProgressEntered = previousRouteProgress is double previousProgress &&
                                   previousProgress < entryProgress - GateProgressToleranceMeters &&
                                   route.ProgressMeters >= entryProgress - GateProgressToleranceMeters &&
                                   corridorMatch;
        // A curved or oblique pit entry can make the recorded gate normal less
        // representative than the centre line. Once two usable samples follow
        // the recorded lane beyond the entry, accept small reverse projection
        // jitter instead of requiring another perfect directed gate crossing.
        var recoveredPastEntry = !pitLaneActive &&
                                 previousPosition is not null &&
                                 routeDirectionCompatible &&
                                 betweenEnforcementGates;
        var entered = previousPosition is Vector3F previousPoint &&
                          EstateRaceGeometry.CrossesForwardGate(pit.EntryGate, previousPoint, position) ||
                      routeProgressEntered || recoveredPastEntry;
        var routeProgressExited = previousRouteProgress is double previousExitProgress &&
                                  previousExitProgress < exitProgress + GateProgressToleranceMeters &&
                                  route.ProgressMeters >= exitProgress - GateProgressToleranceMeters &&
                                  routeDirectionCompatible &&
                                  corridorMatch;
        var reachedExit = pitLaneActive && corridorMatch && routeDirectionCompatible &&
                          route.ProgressMeters >= exitProgress - GateProgressToleranceMeters;
        var exited = previousPosition is Vector3F previousExitPoint &&
                         EstateRaceGeometry.CrossesForwardGate(pit.ExitGate, previousExitPoint, position) ||
                     routeProgressExited || reachedExit;
        if (entered || !pitLaneActive && previousPosition is null && betweenEnforcementGates)
        {
            pitLaneActive = true;
            pitLaneElapsedSeconds = entered ? 0 : pitLaneElapsedSeconds;
            if (entered)
            {
                elapsedSeconds = 0;
                creditedThisVisit = false;
                wasEligible = false;
            }
        }
        if (pitLaneActive) pitLaneElapsedSeconds += deltaSeconds;
        var strictlyInZone = EstateRaceGeometry.IsInServiceZone(pit, frame.Raw.Position);
        if (strictlyInZone)
        {
            serviceVisitId ??= Guid.NewGuid();
            serviceZoneLastConfirmedAt = frame.ArrivalTime;
        }
        var inZoneGrace = !strictlyInZone &&
                          serviceVisitId is not null &&
                          serviceZoneLastConfirmedAt is DateTimeOffset lastConfirmed &&
                          frame.ArrivalTime - lastConfirmed <= ServiceZoneGrace &&
                          EstateRaceGeometry.IsInServiceZone(
                              pit,
                              frame.Raw.Position,
                              ServiceZoneBoundaryToleranceMeters);
        var inZone = strictlyInZone || inZoneGrace;
        if (inZone) pitLaneActive = true;
        var clearlyOutsideCorridor = route.DistanceMeters > halfWidth * 1.75 + 2;
        if (pitLaneActive && !inZone && !corridorMatch && clearlyOutsideCorridor)
        {
            outsidePitCorridorSince ??= frame.ArrivalTime;
            if (frame.ArrivalTime - outsidePitCorridorSince.Value >= TimeSpan.FromMilliseconds(750))
            {
                pitLaneActive = false;
                wasEligible = false;
            }
        }
        else
        {
            outsidePitCorridorSince = null;
        }
        var stationary = frame.Normalized.SpeedKph <= StationarySpeedKph;
        var eligible = inZone && stationary && !serviceBlocked;
        var movementGraceActive = false;
        if (!inZone)
        {
            elapsedSeconds = 0;
            creditedThisVisit = false;
            movementInterruptedAt = null;
            serviceZoneLastConfirmedAt = null;
            serviceVisitId = null;
        }
        else if (serviceBlocked)
        {
            elapsedSeconds = 0;
            creditedThisVisit = false;
            movementInterruptedAt = null;
        }
        else if (stationary)
        {
            movementInterruptedAt = null;
            if (wasEligible) AccumulateUntil(frame.ArrivalTime);
        }
        else
        {
            movementInterruptedAt ??= frame.ArrivalTime;
            var resetDelay = frame.Normalized.SpeedKph > MovementResetSpeedKph
                ? MovementGrace
                : CreepingGrace;
            movementGraceActive = frame.ArrivalTime - movementInterruptedAt.Value < resetDelay;
            if (!movementGraceActive && !creditedThisVisit)
                elapsedSeconds = 0;
        }

        if (eligible) CreditCompletedService(required);

        if (exited)
        {
            pitLaneActive = false;
            outsidePitCorridorSince = null;
            inZone = false;
            eligible = false;
            wasEligible = false;
            serviceZoneLastConfirmedAt = null;
            movementInterruptedAt = null;
            serviceVisitId = null;
        }

        previousArrival = frame.ArrivalTime;
        previousPosition = position;
        previousRouteProgress = route.ProgressMeters;
        wasEligible = eligible;
        var approaching = !pitLaneActive && EstateRaceGeometry.IsApproachingPitEntry(
            pit,
            position,
            route,
            entryProgress);
        Current = new EstatePitServiceState(
            pitLaneActive,
            inZone,
            required,
            elapsedSeconds,
            creditedThisVisit,
            completedServices,
            eligible,
            frame.ArrivalTime,
            pitLaneElapsedSeconds,
            approaching,
            pit.SpeedLimitKph,
            frame.Normalized.SpeedKph,
            pitLaneActive && frame.Normalized.SpeedKph > pit.SpeedLimitKph + 1,
            corridorMatch,
            serviceVisitId,
            creditedThisVisit
                ? EstatePitServiceProgressState.Completed
                : !inZone
                    ? EstatePitServiceProgressState.None
                    : serviceBlocked
                        ? EstatePitServiceProgressState.Blocked
                        : eligible
                            ? EstatePitServiceProgressState.Counting
                            : movementGraceActive
                                ? EstatePitServiceProgressState.MovementGrace
                                : EstatePitServiceProgressState.WaitingForStop);
        return Current;
    }

    public void Reset()
    {
        previousArrival = null;
        wasEligible = false;
        creditedThisVisit = false;
        elapsedSeconds = 0;
        pitLaneElapsedSeconds = 0;
        completedServices = 0;
        pitLaneActive = false;
        previousPosition = null;
        previousRouteProgress = null;
        outsidePitCorridorSince = null;
        serviceZoneLastConfirmedAt = null;
        movementInterruptedAt = null;
        serviceVisitId = null;
        cachedPit = null;
        cachedEntryProgress = 0;
        cachedExitProgress = 0;
        Current = EstatePitServiceState.Empty;
    }

    private void EnsureGateProgress(EstatePitDefinition pit)
    {
        if (ReferenceEquals(cachedPit, pit)) return;
        cachedPit = pit;
        cachedEntryProgress = EstateRaceGeometry.PitGateProgress(pit, pit.EntryGate);
        cachedExitProgress = EstateRaceGeometry.PitGateProgress(pit, pit.ExitGate);
        GateProgressRefreshCount++;
    }

    private void AccumulateUntil(DateTimeOffset now)
    {
        if (!wasEligible || previousArrival is not DateTimeOffset previous || now <= previous) return;
        elapsedSeconds = Math.Min(86_400, elapsedSeconds + (now - previous).TotalSeconds);
    }

    private void CreditCompletedService(double required)
    {
        if (required <= 0 || elapsedSeconds < required || creditedThisVisit) return;
        creditedThisVisit = true;
        completedServices++;
    }
}
