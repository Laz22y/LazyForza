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
    string? TeamName);

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
    int SectorCount = 0);

public sealed record EstateCompletedLapEvent(
    Guid EventId,
    int LapNumber,
    double LapSeconds,
    IReadOnlyList<double> SectorSeconds,
    bool IsValid,
    string? InvalidReason);

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
    bool IsRevoked);

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
    bool QualifyingFinalLapPending = false);

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
    bool QualifyingTimeExpired = false);

public sealed record EstatePitServiceState(
    bool IsInPitLane,
    bool IsInServiceZone,
    double RequiredSeconds,
    double ElapsedSeconds,
    bool RequirementMet,
    int CompletedServices)
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
