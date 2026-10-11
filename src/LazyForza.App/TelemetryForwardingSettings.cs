using System.Text.Json;
using LazyForza.Telemetry;

namespace LazyForza.App;

internal static class TelemetryForwardingSettings
{
    internal const string Key = "telemetry.forwarding";
    internal static TelemetryForwardingOptions Read(string? json)
    {
        try { return json is null ? new() : JsonSerializer.Deserialize<TelemetryForwardingOptions>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }
    internal static string Write(TelemetryForwardingOptions options) => JsonSerializer.Serialize(options);
}
