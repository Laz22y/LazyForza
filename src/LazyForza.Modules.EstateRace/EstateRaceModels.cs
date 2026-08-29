using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

public enum EstateRaceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Rejected,
    Faulted
}

public enum EstateRaceConnectionRole
{
    Driver,
    Observer
}

public enum EstateRaceNetworkQuality
{
    Normal,
    HighLatency,
    Unstable,
    Reconnecting
}

public sealed record EstateRaceConnectionProfile(
    string ServerAddress,
    string Password,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    string? TeamId = null,
    EstateRaceConnectionRole Role = EstateRaceConnectionRole.Driver)
{
    public bool IsObserver => Role == EstateRaceConnectionRole.Observer;
}

public sealed partial record EstateRaceTeam
{
    public override string ToString() => Name;
}

public sealed record EstateRaceServerFavorite(
    Guid Id,
    string Name,
    string ServerAddress,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() => $"{Name} · {ServerAddress}";
}

public sealed record EstateRaceConnectionTestResult(
    EstateRaceServerDescriptor Descriptor,
    TimeSpan RoundTripTime);

public sealed record EstateCompletedLapEvent(
    Guid EventId,
    int LapNumber,
    double LapSeconds,
    IReadOnlyList<double> SectorSeconds,
    bool IsValid,
    string? InvalidReason,
    bool IsBestLapEligible = true);

public sealed record EstateRaceTrackContext(
    TrackTemplate Track,
    EstateTrackDefinition Definition,
    double CurrentLapSeconds,
    int CompletedLaps,
    int CurrentSector,
    bool IsTimingActive,
    EstateCompletedLapEvent? LastCompletedLap,
    int SectorCount = 0,
    string? TrackPackageHash = null,
    IReadOnlyList<SectorDefinition>? Sectors = null);

public readonly record struct EstateRaceMapPoint(double X, double Y);

public readonly record struct EstateRaceMapGate(
    EstateRaceMapPoint Left,
    EstateRaceMapPoint Right);

public sealed record EstateRaceMapSector(
    int SectorIndex,
    IReadOnlyList<EstateRaceMapPoint> Points);

public sealed record EstateRaceOrganizerLogo(
    string Sha256,
    string MimeType,
    byte[] Bytes);

public sealed record EstatePitServiceState(
    bool IsInPitLane,
    bool IsInServiceZone,
    double RequiredSeconds,
    double ElapsedSeconds,
    bool RequirementMet,
    int CompletedServices,
    bool IsCounting = false,
    DateTimeOffset? CountingUpdatedAt = null,
    double PitLaneElapsedSeconds = 0,
    bool IsApproachingPit = false,
    double SpeedLimitKph = 0,
    double CurrentSpeedKph = 0,
    bool IsSpeeding = false,
    bool IsOnPitRoute = false,
    Guid? VisitId = null,
    EstatePitServiceProgressState ProgressState = EstatePitServiceProgressState.None)
{
    public static EstatePitServiceState Empty { get; } = new(false, false, 0, 0, false, 0);
}

public enum EstatePitServiceProgressState
{
    None,
    WaitingForStop,
    Counting,
    MovementGrace,
    Blocked,
    Completed
}

public enum EstatePitStrategyDecision
{
    Unavailable,
    Collecting,
    StayOut,
    PitThisLap,
    PitWindow,
    InPit,
    Finished
}

public enum EstatePitStrategyConfidence
{
    Low,
    Medium,
    High
}

public sealed record EstatePitStrategyPrediction(
    EstatePitStrategyDecision Decision,
    string Title,
    string Summary,
    int? PitWindowStartLap,
    int? PitWindowEndLap,
    double? EstimatedPitLossSeconds,
    bool UsesObservedPitLoss,
    double? RepresentativeLapSeconds,
    double? DegradationPerLapSeconds,
    double? ProjectedAdvantageSeconds,
    EstatePitStrategyConfidence Confidence,
    int CleanLapCount,
    int ExcludedLapCount,
    int BoundaryIncidentLapCount,
    int AnomalousLapCount,
    int PitAffectedLapCount,
    int ObservedPitStopCount,
    EstatePitLossSource PitLossSource = EstatePitLossSource.None,
    int HistoricalSampleCount = 0,
    int HistoricalEvidenceLapCount = 0,
    string? HistoricalMatchDescription = null,
    bool UsesHistoricalPace = false,
    int MinimumRequiredPitStops = 0,
    int CompletedPitStops = 0,
    int RemainingRequiredPitStops = 0);

public enum EstatePitLossSource
{
    None,
    CurrentSession,
    Historical,
    ConfiguredGeometry
}

public enum EstatePracticeTestKind
{
    LongRun,
    PitStopSimulation,
    QualifyingSimulation
}

public enum EstatePracticeTestStatus
{
    Ready,
    Active,
    Completed,
    Failed,
    Cancelled
}

public sealed record EstatePracticeTestItemState(
    EstatePracticeTestKind Kind,
    string Title,
    string Description,
    EstatePracticeTestStatus Status,
    string Guidance,
    int CompletedSteps,
    int TargetSteps,
    string? LastResult = null,
    DateTimeOffset? HudVisibleUntil = null,
    DateTimeOffset? HudVisibleFrom = null)
{
    public bool IsActive => Status == EstatePracticeTestStatus.Active;

    public bool IsVisibleOnHud(DateTimeOffset now) =>
        IsActive ||
        Status is EstatePracticeTestStatus.Completed or EstatePracticeTestStatus.Failed &&
        HudVisibleUntil is DateTimeOffset visibleUntil && visibleUntil > now;
}

public sealed record EstatePracticeTestPanelState(
    bool IsPracticeSession,
    EstatePracticeTestKind? ActiveKind,
    IReadOnlyList<EstatePracticeTestItemState> Items,
    int StoredSampleCount = 0)
{
    public static EstatePracticeTestPanelState Hidden { get; } = new(false, null, []);
}

public sealed record EstateRaceHudState(
    DateTimeOffset UpdatedAt,
    EstateRaceConnectionState ConnectionState,
    string ConnectionText,
    Guid? LocalParticipantId,
    EstateRaceSession? Session,
    IReadOnlyList<EstateRaceMapPoint> TrackOutline,
    RaceGripCondition LocalGripCondition,
    string GripExplanation,
    EstatePitServiceState PitService,
    IReadOnlyList<EstateRaceMapPoint>? PitLaneOutline = null,
    EstateRaceMapGate? StartFinishGate = null,
    IReadOnlyList<EstateRaceMapSector>? TrackSectors = null,
    EstateRaceOrganizerLogo? OrganizerLogo = null,
    bool IsObserver = false,
    EstatePitStrategyPrediction? PitStrategy = null,
    EstatePracticeTestPanelState? PracticeTests = null,
    TimeSpan EstimatedOneWayLatency = default,
    TimeSpan? ServerClockOffset = null,
    TimeSpan EstimatedRoundTripLatency = default,
    TimeSpan NetworkJitter = default,
    DateTimeOffset? LastServerResponseAt = null)
{
    public bool IsConnected => ConnectionState == EstateRaceConnectionState.Connected;
}
