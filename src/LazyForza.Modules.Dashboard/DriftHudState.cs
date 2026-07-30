using LazyForza.Domain;

namespace LazyForza.Modules.DriftDashboard;

public enum DriftPracticePhase
{
    Waiting,
    Ready,
    Building,
    Stable,
    Recovering
}

public enum DriftGuidanceTone
{
    Neutral,
    Positive,
    Warning
}

/// <summary>
/// Derived practice feedback from the player's own official FH6 Data Out stream.
/// Stability is a LazyForza training indicator, not an in-game drift score.
/// </summary>
public sealed record DriftHudState(
    DateTimeOffset UpdatedAt,
    TelemetrySourceKind Source,
    string SourceLabel,
    bool IsStale,
    bool IsDriving,
    DriftPracticePhase Phase,
    string PhaseLabel,
    string Guidance,
    DriftGuidanceTone GuidanceTone,
    int SpeedKph,
    int? ForwardGear,
    string GearDisplay,
    double DriftAngleDegrees,
    double YawRateDegreesPerSecond,
    double Steering,
    double Throttle,
    double Brake,
    double Clutch,
    double HandBrake,
    double FrontSlip,
    double RearSlip,
    double RearLongitudinalSlip,
    bool IsDrifting,
    double CurrentDriftSeconds,
    double StableDriftSeconds,
    double BestStableDriftSeconds,
    double StabilityScore,
    double ThrottleSmoothness,
    double SteeringCoordination);
