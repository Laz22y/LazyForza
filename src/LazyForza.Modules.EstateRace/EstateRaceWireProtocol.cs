using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.Modules.EstateRace;

internal static class EstateRaceWireProtocol
{
    public const int Version = 2;
    public const int MaximumMessageBytes = 64 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Serialize<T>(string type, long sequence, T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new RaceEnvelope<T>(Version, type, sequence, payload),
            JsonOptions);
}

internal sealed record RaceEnvelope<T>(
    int ProtocolVersion,
    string Type,
    long Sequence,
    T Payload);

internal sealed record RaceIncomingEnvelope(
    int ProtocolVersion,
    string Type,
    long Sequence,
    JsonElement Payload);

internal sealed record RaceLoginRequest(
    string Password,
    string DisplayName,
    string ThemeColor,
    string? TeamName,
    string ClientVersion,
    string? ResumeToken,
    string? TrackId,
    string? TrackRevision,
    string? TrackPackageHash,
    int? SectorCount = null,
    string? TeamId = null,
    bool IsObserver = false);

internal sealed record RaceLoginAccepted(
    Guid ParticipantId,
    string ResumeToken,
    EstateRaceSession Snapshot,
    DateTimeOffset ServerTime,
    bool IsObserver = false);

internal sealed record RaceLoginRejected(string Code, string Message);

internal sealed record RaceReadyUpdate(bool IsReady);

internal sealed record RaceClockPing(long ClientMonotonicMilliseconds);

internal sealed record RaceClockPong(
    long ClientMonotonicMilliseconds,
    long ServerUnixMilliseconds);

internal sealed record RaceTelemetryUpdate(
    long ClientMonotonicMilliseconds,
    double TrackProgress,
    double LateralOffsetMeters,
    double MapX,
    double MapY,
    double SpeedKph,
    int CompletedLaps,
    int CurrentSector,
    double CurrentLapSeconds,
    bool IsInPitLane,
    bool IsInServiceZone,
    bool IsTelemetryValid,
    bool IsPausedOrRewinding,
    RaceGripCondition GripCondition,
    double PitServiceElapsedSeconds,
    bool PitServiceRequirementMet,
    int CompletedPitServices,
    double TrackToleranceMeters = 18,
    double TrackLengthMeters = 0,
    double PitSpeedLimitKph = 0,
    double PitLaneElapsedSeconds = 0,
    bool IsApproachingPit = false,
    bool IsOnPitRoute = false,
    bool HasWorldPosition = false,
    double WorldX = 0,
    double WorldY = 0,
    double WorldZ = 0,
    double VelocityX = 0,
    double VelocityY = 0,
    double VelocityZ = 0,
    long ImpactSequence = 0,
    double ImpactMagnitudeMps = 0,
    double ImpactSpeedLossMps = 0,
    double ImpactWorldX = 0,
    double ImpactWorldY = 0,
    double ImpactWorldZ = 0,
    int ImpactAgeMilliseconds = 0);

internal sealed record RaceLapCompleted(
    Guid EventId,
    int LapNumber,
    double LapSeconds,
    IReadOnlyList<double> SectorSeconds,
    bool IsValid,
    string? InvalidReason,
    long ClientMonotonicMilliseconds,
    bool IsBestLapEligible = true);

internal sealed record RaceProtocolError(string Code, string Message);
