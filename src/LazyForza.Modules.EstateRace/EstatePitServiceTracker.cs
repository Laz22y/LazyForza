using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal sealed class EstatePitServiceTracker
{
    private uint? previousTimestamp;
    private bool wasEligible;
    private bool creditedThisVisit;
    private double elapsedSeconds;
    private int completedServices;

    public EstatePitServiceState Current { get; private set; } = EstatePitServiceState.Empty;

    public EstatePitServiceState Observe(
        TelemetryFrame frame,
        EstatePitDefinition? pit,
        bool telemetryValid)
    {
        if (pit is null)
        {
            Reset();
            return Current;
        }
        // Pausing, opening a menu or rewinding makes the timestamp and position
        // temporarily unusable. Freeze the current visit instead of treating
        // the bad frame as leaving the service zone or breaking the stop.
        if (!telemetryValid)
        {
            previousTimestamp = null;
            wasEligible = false;
            return Current;
        }
        var inLane = EstateRaceGeometry.IsInPitLane(pit, frame.Raw.Position);
        var inZone = EstateRaceGeometry.IsInServiceZone(pit, frame.Raw.Position);
        var required = Math.Clamp(pit.MinimumServiceSeconds, 1, 60);
        var eligible = inZone && frame.Normalized.SpeedKph <= 5;
        if (!inZone)
        {
            elapsedSeconds = 0;
            creditedThisVisit = false;
        }
        else if (!eligible)
        {
            elapsedSeconds = 0;
        }
        else if (wasEligible && previousTimestamp is uint previous)
        {
            var delta = TimestampDeltaSeconds(previous, frame.Raw.TimestampMS);
            if (delta is > 0 and <= 1)
                elapsedSeconds = Math.Min(required, elapsedSeconds + delta);
        }

        if (eligible && required > 0 && elapsedSeconds >= required && !creditedThisVisit)
        {
            creditedThisVisit = true;
            completedServices++;
        }

        previousTimestamp = frame.Raw.TimestampMS;
        wasEligible = eligible;
        Current = new EstatePitServiceState(
            inLane,
            inZone,
            required,
            elapsedSeconds,
            creditedThisVisit,
            completedServices);
        return Current;
    }

    public void Reset()
    {
        previousTimestamp = null;
        wasEligible = false;
        creditedThisVisit = false;
        elapsedSeconds = 0;
        completedServices = 0;
        Current = EstatePitServiceState.Empty;
    }

    private static double TimestampDeltaSeconds(uint previous, uint current)
    {
        if (current >= previous) return (current - previous) / 1000d;
        if (previous > uint.MaxValue - 10_000 && current < 10_000)
            return ((double)uint.MaxValue - previous + 1 + current) / 1000d;
        return 0;
    }
}
