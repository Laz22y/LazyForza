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

public enum RaceSessionPhase
{
    Lobby,
    Practice,
    Qualifying,
    Grid,
    OutLap,
    FormationLap,
    Countdown,
    Race,
    Suspended,
    Finished
}

public enum RaceControlFlag
{
    Green,
    Yellow,
    Red,
    Chequered
}

public enum RaceParticipantStatus
{
    Connected,
    Ready,
    OnTrack,
    InPitLane,
    InService,
    Finished,
    DidNotFinish,
    Disqualified,
    Disconnected
}

public enum RaceGripCondition
{
    Unknown,
    SlightlyReduced,
    ModeratelyReduced,
    SeverelyReduced,
    AtLimit
}

public enum RacePenaltyKind
{
    Warning,
    Time,
    DriveThrough,
    StopAndGo,
    GridDrop,
    Disqualification
}

public enum RaceBannerKind
{
    Information,
    FastestLap,
    Penalty,
    YellowFlag,
    RedFlag,
    BlueFlag,
    ChequeredFlag,
    Winner
}

public enum RaceInvestigationStatus
{
    Pending,
    Penalized,
    Dismissed
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

public sealed record EstateRaceTeam(
    string Id,
    string Name,
    string ThemeColor)
{
    public override string ToString() => Name;
}

public sealed record EstateRaceServerDescriptor(
    string ServerName,
    int ProtocolVersion,
    int MaximumParticipants,
    bool RequiresPassword,
    string? ActiveTrackId,
    string? ActiveTrackRevision,
    RaceSessionPhase Phase,
    DateTimeOffset ServerTime,
    string? ActiveTrackName = null,
    string? ActiveTrackPackageHash = null,
    bool AllowTeams = true,
    int SectorCount = 0,
    int DriversPerTeam = 6,
    IReadOnlyList<EstateRaceTeam>? Teams = null,
    bool TrackPackageAvailable = false,
    long? TrackPackageSizeBytes = null,
    string? TrackPackageDownloadPath = null,
    string? TrackPackageFileSha256 = null,
    string? OrganizerLogoHash = null,
    string? OrganizerLogoMimeType = null,
    string? OrganizerLogoDownloadPath = null,
    bool SupportsObservers = false,
    int MaximumObservers = 0);

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

public sealed record EstateRacePenalty(
    Guid Id,
    RacePenaltyKind Kind,
    double? ValueSeconds,
    int? GridPlaces,
    string Reason,
    bool IsServed,
    bool IsRevoked,
    bool IsPostRaceAdjustment = false,
    bool IsAutomatic = false,
    Guid? InvestigationId = null);

public sealed record EstateRaceInvestigation(
    Guid Id,
    Guid ParticipantId,
    string Offense,
    DateTimeOffset DetectedAt,
    int LapNumber,
    RaceInvestigationStatus Status,
    Guid? PenaltyId = null,
    DateTimeOffset? ResolvedAt = null,
    IReadOnlyList<Guid>? RelatedParticipantIds = null,
    EstateCollisionEvidenceSnapshot? CollisionEvidence = null);

public sealed record EstateCollisionEvidenceSnapshot(
    DateTimeOffset IncidentAt,
    Guid ReporterParticipantId,
    Guid OtherParticipantId,
    string ReporterName,
    string OtherName,
    string ReporterThemeColor,
    string OtherThemeColor,
    double ReporterWorldX,
    double ReporterWorldY,
    double ReporterWorldZ,
    double OtherWorldX,
    double OtherWorldY,
    double OtherWorldZ,
    double ReporterVelocityX,
    double ReporterVelocityZ,
    double OtherVelocityX,
    double OtherVelocityZ,
    double HorizontalDistanceMeters,
    double VerticalDistanceMeters,
    double RelativeSpeedKph,
    double ImpactMagnitudeMps,
    double ImpactSpeedLossMps);

public sealed record EstateRaceParticipant(
    Guid Id,
    int Position,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    RaceParticipantStatus Status,
    bool IsConnected,
    bool IsReady,
    int CompletedLaps,
    int CurrentSector,
    double TrackProgress,
    double MapX,
    double MapY,
    double SpeedKph,
    double CurrentLapSeconds,
    double? LastLapSeconds,
    double? BestLapSeconds,
    double? GapToLeaderSeconds,
    double? IntervalSeconds,
    bool IsInPitLane,
    bool IsInServiceZone,
    double PitServiceElapsedSeconds,
    bool PitServiceRequirementMet,
    int CompletedPitServices,
    RaceGripCondition GripCondition,
    IReadOnlyList<double?> BestSectorSeconds,
    IReadOnlyList<EstateRacePenalty> Penalties,
    DateTimeOffset LastSeenAt,
    bool QualifyingFinalLapPending = false,
    double? RaceTotalSeconds = null,
    double? AdjustedRaceTotalSeconds = null,
    double TimePenaltySeconds = 0,
    int TrackLimitWarnings = 0,
    string? TeamId = null,
    string? TeamColor = null,
    double PitLaneElapsedSeconds = 0,
    double PendingTimePenaltySeconds = 0,
    bool IsServingTimePenalty = false,
    double PenaltyServiceElapsedSeconds = 0,
    double PenaltyServiceRequiredSeconds = 0,
    bool HasPendingDriveThrough = false,
    bool PenaltyServiceCompleted = false,
    int? DriveThroughLapsRemaining = null,
    DateTimeOffset? DriveThroughReminderAt = null,
    bool DriveThroughOverdue = false,
    bool IsServingDriveThrough = false,
    bool QualifyingEligible = true,
    int? QualifyingEliminatedInSession = null,
    IReadOnlyList<double?>? QualifyingSessionBestLapSeconds = null,
    bool PracticeFinalLapPending = false,
    IReadOnlyList<double?>? PracticeSessionBestLapSeconds = null);

public sealed record EstateRaceObserver(
    Guid Id,
    string DisplayName,
    DateTimeOffset ConnectedAt);

public sealed record EstateRaceBanner(
    Guid Id,
    RaceBannerKind Kind,
    string Title,
    string? Detail,
    Guid? ParticipantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsInvestigation = false);

public sealed record EstateRaceYellowZone(
    int? SectorIndex,
    bool IsAutomatic,
    string Reason,
    Guid? ParticipantId,
    string? ParticipantName);

public sealed record EstateRaceBlueFlag(
    Guid RecipientParticipantId,
    Guid ApproachingParticipantId,
    double DistanceAhead);

public sealed record EstateRaceSession(
    long Revision,
    string SessionName,
    RaceSessionPhase Phase,
    RaceControlFlag Flag,
    string? FlagMessage,
    string? TrackId,
    string? TrackRevision,
    string? TrackPackageHash,
    int TotalRaceLaps,
    DateTimeOffset? StartsAt,
    DateTimeOffset? QualifyingEndsAt,
    Guid? FastestParticipantId,
    double? FastestLapSeconds,
    IReadOnlyList<double?> FastestSectorSeconds,
    EstateRaceBanner? Banner,
    IReadOnlyList<EstateRaceParticipant> Participants,
    DateTimeOffset ServerTime,
    IReadOnlyList<EstateRaceYellowZone>? YellowZones = null,
    int SectorCount = 0,
    bool AllowTeams = true,
    string? TrackName = null,
    IReadOnlyList<EstateRaceBlueFlag>? BlueFlags = null,
    DateTimeOffset? StartSequenceAt = null,
    int IlluminatedStartLights = 0,
    bool StartLightsOut = false,
    bool QualifyingTimeExpired = false,
    double? RaceElapsedSeconds = null,
    RaceSessionPhase? SuspendedFromPhase = null,
    int DriversPerTeam = 6,
    IReadOnlyList<EstateRaceTeam>? Teams = null,
    bool ChequeredImminent = false,
    IReadOnlyList<double?>? FastestLapSectorSeconds = null,
    string? OrganizerLogoHash = null,
    string? OrganizerLogoMimeType = null,
    string? OrganizerLogoDownloadPath = null,
    IReadOnlyList<EstateRacePenalty>? Penalties = null,
    IReadOnlyList<EstateRaceInvestigation>? Investigations = null,
    int QualifyingSessionNumber = 0,
    int QualifyingSessionCount = 1,
    IReadOnlyList<int>? QualifyingSessionMinutes = null,
    IReadOnlyList<int>? QualifyingEliminationCounts = null,
    DateTimeOffset? PracticeEndsAt = null,
    bool PracticeTimeExpired = false,
    int PracticeSessionNumber = 0,
    int PracticeSessionCount = 1,
    IReadOnlyList<int>? PracticeSessionMinutes = null,
    IReadOnlyList<EstateRaceObserver>? Observers = null,
    int MinimumRequiredPitStops = 1);

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
    bool IsOnPitRoute = false)
{
    public static EstatePitServiceState Empty { get; } = new(false, false, 0, 0, false, 0);
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
    TimeSpan? ServerClockOffset = null)
{
    public bool IsConnected => ConnectionState == EstateRaceConnectionState.Connected;
}
