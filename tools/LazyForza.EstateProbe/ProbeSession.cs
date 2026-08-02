using System.IO;
using System.Text.Json;

namespace LazyForza.EstateProbe;

public sealed record ProbeMarkerDocument(
    string Name,
    DateTimeOffset CapturedAt,
    double X,
    double Y,
    double Z,
    double YawRadians,
    double SpreadMeters,
    int SampleCount,
    double MeanSpeedMps);

public sealed record ProbeTraceSample(
    DateTimeOffset ArrivalTime,
    uint TimestampMs,
    double X,
    double Y,
    double Z,
    double YawRadians,
    double SpeedMps,
    int IsRaceOn,
    double CurrentLapSeconds,
    double CurrentRaceSeconds,
    ushort LapNumber,
    byte RacePosition);

public sealed record ProbeSessionSummary(
    long ValidPackets,
    long InvalidPackets,
    long DrivingPackets,
    long CurrentLapPositivePackets,
    long CurrentRacePositivePackets,
    long RacePositionPositivePackets,
    int CoordinateJumpCount,
    int TimestampBackwardCount,
    DateTimeOffset? FirstPacketAt,
    DateTimeOffset? LastPacketAt,
    int LastCarOrdinal,
    int LastCarClass,
    int LastPerformanceIndex);

public sealed record ProbeSessionDocument(
    int SchemaVersion,
    string ToolVersion,
    Guid SessionId,
    string SessionName,
    string SessionRole,
    DateTimeOffset StartedAt,
    DateTimeOffset SavedAt,
    string ListenAddress,
    int ListenPort,
    ProbeSessionSummary Summary,
    IReadOnlyList<ProbeMarkerDocument> Markers,
    IReadOnlyList<ProbeTraceSample> Trace,
    string Notes);

internal static class ProbeSessionFile
{
    public const int CurrentSchemaVersion = 1;
    public const string Extension = ".lfzestateprobe.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task SaveAsync(string path, ProbeSessionDocument session, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProbeSessionDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var session = await JsonSerializer.DeserializeAsync<ProbeSessionDocument>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (session is null) throw new InvalidDataException("会话文件为空或格式无效。");
        if (session.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"不支持会话格式版本 {session.SchemaVersion}。");
        return session;
    }
}
