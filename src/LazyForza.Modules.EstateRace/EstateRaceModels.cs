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

public enum RaceSessionPhase
{
    Lobby,
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

public sealed record EstateRaceConnectionProfile(
    string ServerAddress,
    string Password,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    string? TeamId = null);

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
    IReadOnlyList<EstateRaceTeam>? Teams = null);

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
    string? TrackPackageHash = null);

public readonly record struct EstateRaceMapPoint(double X, double Y);

public sealed record EstateRacePenalty(
    Guid Id,
    RacePenaltyKind Kind,
    double? ValueSeconds,
    int? GridPlaces,
    string Reason,
    bool IsServed,
    bool IsRevoked,
    bool IsPostRaceAdjustment = false);

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
    bool IsServingDriveThrough = false);

public sealed record EstateRaceBanner(
    Guid Id,
    RaceBannerKind Kind,
    string Title,
    string? Detail,
    Guid? ParticipantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

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
    IReadOnlyList<double?>? FastestLapSectorSeconds = null);

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
    bool IsSpeeding = false)
{
    public static EstatePitServiceState Empty { get; } = new(false, false, 0, 0, false, 0);
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
    EstatePitServiceState PitService)
{
    public bool IsConnected => ConnectionState == EstateRaceConnectionState.Connected;
}
