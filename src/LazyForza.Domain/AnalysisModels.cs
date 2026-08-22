namespace LazyForza.Domain;

public enum LearningState
{
    NotStarted,
    Collecting,
    Insufficient,
    Ready,
    Stale,
    Error
}

public sealed record VehicleProfileFingerprint(
    int CarOrdinal,
    int CarClass,
    int PerformanceIndex,
    int DrivetrainType,
    int NumCylinders,
    int RoundedMaxRpm,
    string GearSlopeSignature,
    string CurveSignature)
{
    public static VehicleProfileFingerprint FromFrame(TelemetryFrame frame) => new(
        frame.Raw.CarOrdinal,
        PerformanceClassCatalog.Resolve(frame.Raw.CarClass, frame.Raw.CarPerformanceIndex),
        frame.Raw.CarPerformanceIndex,
        frame.Raw.DrivetrainType,
        frame.Raw.NumCylinders,
        (int)(MathF.Round(frame.Raw.EngineMaxRpm / 50f) * 50f),
        "learning",
        "learning");
}

public static class VehicleProfileIdentity
{
    public const string PendingSignature = "learning";

    public static bool IsResolved(VehicleProfileFingerprint? fingerprint) =>
        fingerprint is not null &&
        !string.Equals(fingerprint.GearSlopeSignature, PendingSignature, StringComparison.Ordinal) &&
        !string.Equals(fingerprint.CurveSignature, PendingSignature, StringComparison.Ordinal);

    public static string Create(VehicleProfileFingerprint fingerprint) =>
        $"{fingerprint.CarOrdinal}:{fingerprint.CarClass}:{fingerprint.PerformanceIndex}:" +
        $"{fingerprint.DrivetrainType}:{fingerprint.NumCylinders}:{fingerprint.RoundedMaxRpm}:" +
        $"{fingerprint.GearSlopeSignature}:{fingerprint.CurveSignature}";

    public static string? TryCreate(VehicleProfileFingerprint? fingerprint) =>
        IsResolved(fingerprint) ? Create(fingerprint!) : null;
}

public sealed record EngineCurveBin(
    int RpmCenter,
    int SampleCount,
    double MedianPowerWatts,
    double MedianTorqueNm,
    double MedianBoostPsi,
    double MedianAbsoluteDeviation,
    double Confidence);

public sealed record GearModel(int Gear, double RpmPerMeterPerSecond, int SampleCount, double Confidence);

public sealed record ShiftTarget(
    int FromGear,
    int ToGear,
    double TargetRpm,
    double CueRpm,
    double AfterShiftRpm,
    double Confidence,
    bool UsedLimiterFallback);

public sealed record ShiftLearningSnapshot(
    LearningState State,
    double Progress,
    double Confidence,
    VehicleProfileFingerprint? Fingerprint,
    IReadOnlyList<EngineCurveBin> Curve,
    IReadOnlyList<GearModel> Gears,
    IReadOnlyList<ShiftTarget> Targets,
    IReadOnlyDictionary<string, int> RejectedSamples,
    string StatusMessage)
{
    public int AcceptedSamples { get; init; }
    public int ReadyBins { get; init; }
    public int RequiredBins { get; init; }
    public int ReadyGears { get; init; }
    public double? EstimatedSecondsRemaining { get; init; }
    public string Guidance { get; init; } = string.Empty;
    public long ConfigurationRevision { get; init; }
}

public readonly record struct TrackPoint(double X, double Y, double Z, double S, double TangentX, double TangentZ)
{
    public double DistanceSquaredTo(TrackPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

public enum TrackLayoutKind
{
    Circuit,
    PointToPoint
}

public enum TrackCatalogKind
{
    UserCustom,
    PlaygroundOfficial
}

public enum TrackTimingKind
{
    GameEvent,
    EstateGeometry
}

public sealed record TrackTemplate(
    Guid Id,
    string Name,
    int Direction,
    string Source,
    string? GameBuild,
    IReadOnlyList<TrackPoint> Points,
    double LengthMeters,
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ,
    double MatchingToleranceMeters,
    double Confidence,
    int CaptureLapCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public TrackLayoutKind LayoutKind { get; init; } = TrackLayoutKind.Circuit;
    public TrackCatalogKind CatalogKind { get; init; } = TrackCatalogKind.UserCustom;
    public TrackTimingKind TimingKind { get; init; } = TrackTimingKind.GameEvent;
    public string? Category { get; init; }
}

public enum SectorFeatureType
{
    EqualDistance,
    Braking,
    Corner,
    CornerExit
}

public sealed record SectorDefinition(
    Guid TrackId,
    int SectorSchemaVersion,
    int Index,
    double StartS,
    double EndS,
    SectorFeatureType FeatureType,
    string AlgorithmVersion);

public sealed record LapSample(
    double S,
    double ElapsedSeconds,
    double SpeedMps,
    double Rpm,
    byte Gear,
    double Accel,
    double Brake,
    double DeltaSeconds,
    double X,
    double Y,
    double Z,
    LapDynamics? Dynamics = null);

/// <summary>
/// Compact per-sample driving inputs retained for saved-lap dynamics analysis.
/// A null value means the lap was recorded by a build that predates dynamics capture.
/// </summary>
public sealed record LapDynamics(
    double Steering,
    WheelValues TireSlipRatio,
    WheelValues TireSlipAngle,
    WheelValues TireCombinedSlip);

public sealed record LapSegment(int Index, double TimeSeconds, bool IsValid);

public sealed record LapSummary(
    Guid Id,
    Guid TrackId,
    int Direction,
    int SectorSchemaVersion,
    Guid SessionId,
    VehicleProfileFingerprint Vehicle,
    DateTimeOffset StartedAt,
    double TotalSeconds,
    bool IsValid,
    string? InvalidReason,
    IReadOnlyList<LapSegment> Segments,
    string? PlayerCode = null)
{
    public static LapSummary FromRecord(LapRecord lap) => new(
        lap.Id,
        lap.TrackId,
        lap.Direction,
        lap.SectorSchemaVersion,
        lap.SessionId,
        lap.Vehicle,
        lap.StartedAt,
        lap.TotalSeconds,
        lap.IsValid,
        lap.InvalidReason,
        lap.Segments,
        lap.PlayerCode);

    public LapRecord WithSamples(IReadOnlyList<LapSample> samples) => new(
        Id,
        TrackId,
        Direction,
        SectorSchemaVersion,
        SessionId,
        Vehicle,
        StartedAt,
        TotalSeconds,
        IsValid,
        InvalidReason,
        Segments,
        samples,
        PlayerCode);
}

public sealed record LapRecord(
    Guid Id,
    Guid TrackId,
    int Direction,
    int SectorSchemaVersion,
    Guid SessionId,
    VehicleProfileFingerprint Vehicle,
    DateTimeOffset StartedAt,
    double TotalSeconds,
    bool IsValid,
    string? InvalidReason,
    IReadOnlyList<LapSegment> Segments,
    IReadOnlyList<LapSample> Samples,
    string? PlayerCode = null);

public enum SectorColorState
{
    Gray,
    Yellow,
    Green,
    Purple
}

public sealed record SectorComparison(
    int Index,
    double? CurrentSeconds,
    double? CurrentCompetitionBestSeconds,
    double? HistoricalBestSeconds,
    double? DeltaSeconds,
    SectorColorState State,
    bool IsCurrent);
