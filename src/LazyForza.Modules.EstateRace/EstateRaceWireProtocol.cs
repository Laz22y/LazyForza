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
    int? SectorCount = null);

internal sealed record RaceLoginAccepted(
    Guid ParticipantId,
    string ResumeToken,
    EstateRaceSession Snapshot,
    DateTimeOffset ServerTime);

internal sealed record RaceLoginRejected(string Code, string Message);

internal sealed record RaceReadyUpdate(bool IsReady);

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
    int CompletedPitServices);

internal sealed record RaceLapCompleted(
    Guid EventId,
    int LapNumber,
    double LapSeconds,
    IReadOnlyList<double> SectorSeconds,
    bool IsValid,
    string? InvalidReason,
    long ClientMonotonicMilliseconds);

internal sealed record RaceProtocolError(string Code, string Message);
