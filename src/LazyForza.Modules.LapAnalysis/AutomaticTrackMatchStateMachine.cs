using LazyForza.Domain;

namespace LazyForza.Modules.LapAnalysis;

/// <summary>
/// Holds the lifecycle and buffered telemetry for automatic route
/// identification. Candidate scoring is deliberately kept stateless outside
/// this lifecycle object.
/// </summary>
internal sealed class AutomaticTrackMatchStateMachine
{
    public List<TelemetryFrame> Frames { get; } = [];
    public bool Started { get; set; }
    public bool Locked { get; set; }
    public bool Rejected { get; set; }
    public bool StartedMidLap { get; set; }
    public bool StartedAtConfirmedLine { get; set; }
    public bool RouteAcquired { get; set; }
    public double TravelMeters { get; set; }
    public int CoarseEligibleCount { get; set; }
    public Vector3F? PreviousPosition { get; set; }
    public DateTimeOffset StartedAt { get; set; }

    public void Begin(
        TelemetryFrame frame,
        bool allowMidRouteStart,
        bool startedAtConfirmedLine)
    {
        Started = true;
        Rejected = false;
        StartedMidLap = allowMidRouteStart;
        StartedAtConfirmedLine = startedAtConfirmedLine;
        RouteAcquired = false;
        TravelMeters = 0;
        PreviousPosition = null;
        StartedAt = frame.ArrivalTime;
        Frames.Clear();
    }

    public void Reset()
    {
        Started = false;
        Locked = false;
        Rejected = false;
        StartedMidLap = false;
        StartedAtConfirmedLine = false;
        RouteAcquired = false;
        TravelMeters = 0;
        CoarseEligibleCount = 0;
        PreviousPosition = null;
        StartedAt = default;
        Frames.Clear();
    }
}
