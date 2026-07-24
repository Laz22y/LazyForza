using LazyForza.Domain;

namespace LazyForza.Modules.Dashboard;

public sealed record DashboardHudState(
    DateTimeOffset UpdatedAt,
    TelemetrySourceKind Source,
    string SourceLabel,
    bool IsStale,
    bool IsDriving,
    byte RawGear,
    int? ForwardGear,
    string GearDisplay,
    bool IsGearDisplayHeld,
    int SpeedKph,
    double Rpm,
    double MaxRpm,
    double PowerKw,
    double TorqueNm,
    WheelValues TireTemperatureCelsius,
    WheelValues GripUi,
    double Brake,
    double Throttle,
    int CarClass,
    int PerformanceIndex,
    ShiftLearningSnapshot ShiftLearning)
{
    public double SpeedMps { get; init; }
    public Vector3F Acceleration { get; init; }
    public double Steering { get; init; }
    public double Clutch { get; init; }
    public double HandBrake { get; init; }
    public bool ShiftRecommendationsEnabled { get; init; } = true;

    public int? RecommendedGear
    {
        get
        {
            if (!ShiftRecommendationsEnabled ||
                IsGearDisplayHeld ||
                ForwardGear is not int currentGear)
                return null;

            var upshift = ShiftLearning.Targets
                .Where(target => target.FromGear == currentGear && Rpm >= target.CueRpm && Rpm <= MaxRpm * 1.05)
                .OrderBy(target => target.ToGear)
                .FirstOrDefault();
            if (upshift is not null) return upshift.ToGear;

            var downshift = ShiftLearning.Targets
                .Where(target => target.ToGear == currentGear && Rpm < target.AfterShiftRpm * 0.9)
                .OrderByDescending(target => target.FromGear)
                .FirstOrDefault();
            return downshift?.FromGear ?? currentGear;
        }
    }

    public bool UpshiftCueActive => RecommendedGear is int recommended && ForwardGear is int current && recommended > current;
    public bool DownshiftCueActive => RecommendedGear is int recommended && ForwardGear is int current && recommended < current;
}
