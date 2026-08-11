using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal sealed class EstatePitServiceTracker
{
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
                PitLaneElapsedSeconds = pitLaneElapsedSeconds
            };
            return Current;
        }
        var position = frame.Raw.Position;
        var route = EstateRaceGeometry.ProjectPitRoute(pit, position);
        var entryProgress = EstateRaceGeometry.PitGateProgress(pit, pit.EntryGate);
        var exitProgress = EstateRaceGeometry.PitGateProgress(pit, pit.ExitGate);
        var halfWidth = Math.Clamp(pit.LaneHalfWidthMeters, 1, 20);
        var corridorMatch = route.DistanceMeters <= halfWidth;
        var routeProgressEntered = previousRouteProgress is double previousProgress &&
                                   previousProgress < entryProgress - 0.25 &&
                                   route.ProgressMeters >= entryProgress - 0.25 &&
                                   corridorMatch;
        var entered = previousPosition is Vector3F previousPoint &&
                      EstateRaceGeometry.CrossesForwardGate(pit.EntryGate, previousPoint, position) ||
                      routeProgressEntered;
        var routeProgressExited = previousRouteProgress is double previousExitProgress &&
                                  previousExitProgress < exitProgress - 0.25 &&
                                  route.ProgressMeters >= exitProgress - 0.25 &&
                                  corridorMatch;
        var exited = previousPosition is Vector3F previousExitPoint &&
                     EstateRaceGeometry.CrossesForwardGate(pit.ExitGate, previousExitPoint, position) ||
                     routeProgressExited ||
                     pitLaneActive && corridorMatch && route.ProgressMeters > exitProgress + 0.75;
        var betweenEnforcementGates = corridorMatch &&
                                      route.ProgressMeters >= entryProgress - 0.75 &&
                                      route.ProgressMeters <= exitProgress + 0.75;
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
        var inZone = EstateRaceGeometry.IsInServiceZone(pit, frame.Raw.Position);
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
        var eligible = inZone && frame.Normalized.SpeedKph <= 1.5 && !serviceBlocked;
        if (!inZone)
        {
            elapsedSeconds = 0;
            creditedThisVisit = false;
        }
        else if (serviceBlocked)
        {
            elapsedSeconds = 0;
            creditedThisVisit = false;
        }
        else if (!eligible)
        {
            elapsedSeconds = 0;
        }
        else if (wasEligible)
            AccumulateUntil(frame.ArrivalTime);

        if (eligible) CreditCompletedService(required);

        if (exited)
        {
            pitLaneActive = false;
            outsidePitCorridorSince = null;
            inZone = false;
            eligible = false;
            wasEligible = false;
        }

        previousArrival = frame.ArrivalTime;
        previousPosition = position;
        previousRouteProgress = route.ProgressMeters;
        wasEligible = eligible;
        var approaching = !pitLaneActive && EstateRaceGeometry.IsApproachingPitEntry(pit, position);
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
            corridorMatch);
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
        Current = EstatePitServiceState.Empty;
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
