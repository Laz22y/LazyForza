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

public enum DriftSpinRiskLevel
{
    Safe,
    Caution,
    Critical
}

public enum DriftSteeringCue
{
    Hold,
    Left,
    Right
}

public enum DriftGearCue
{
    Hold,
    ShiftUp,
    ShiftDown
}

/// <summary>
/// Derived practice feedback from the player's own official FH6 Data Out stream.
/// Spin risk, control reserve, and angle score potential are LazyForza training
/// indicators. They are not in-game drift scores or optimal shift points.
/// </summary>
public sealed record DriftHudState(
    DateTimeOffset UpdatedAt,
    TelemetrySourceKind Source,
    string SourceLabel,
    bool IsStale,
    bool IsDriving,
    DriftPracticePhase Phase,
    string PhaseLabel,
    DriftSpinRiskLevel SpinRiskLevel,
    double SpinRisk,
    DriftSteeringCue SteeringCue,
    double SteeringCueStrength,
    DriftGearCue GearCue,
    double AngleScorePotential,
    bool CanBuildAngle,
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
