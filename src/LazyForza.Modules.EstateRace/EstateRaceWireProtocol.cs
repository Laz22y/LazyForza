using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.Modules.EstateRace;

internal static class EstateRaceWireProtocol
{
    public const int Version = EstateRaceProtocol.CurrentVersion;
    public const int MaximumMessageBytes = EstateRaceProtocol.MaximumMessageBytes;

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
