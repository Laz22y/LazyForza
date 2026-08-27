namespace LazyForza.Fh5MapProbe;

public enum Fh5MapRegion
{
    Mexico,
    HotWheelsPark,
    SierraNueva
}

public sealed record Fh5MapOption(Fh5MapRegion Value, string DisplayName, string FileName)
{
    public override string ToString() => DisplayName;
}

public sealed record Fh5CaptureSettings(
    Fh5MapRegion Region,
    string RegionName,
    string SessionLabel,
    string ListenAddress,
    int ListenPort,
    string OutputPath,
    DateTimeOffset StartedAt);

public sealed record Fh5CoordinateMarker(
    Guid Id,
    string Name,
    DateTimeOffset CapturedAt,
    double X,
    double Y,
    double Z,
    double SpreadMeters,
    int SampleCount,
    double MeanSpeedMps);

public sealed record Fh5CoordinateBounds(
    double MinimumX,
    double MaximumX,
    double MinimumY,
    double MaximumY,
    double MinimumZ,
    double MaximumZ);

public sealed record Fh5CaptureManifest(
    int SchemaVersion,
    string ToolVersion,
    string Game,
    string Region,
    string RegionName,
    string SessionLabel,
    string Notes,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string ListenAddress,
    int ListenPort,
    long TotalPackets,
    long ValidPackets,
    long InvalidPackets,
    long ActiveDrivingPackets,
    IReadOnlyDictionary<int, long> PacketLengths,
    IReadOnlyDictionary<string, long> RejectionReasons,
    Fh5CoordinateBounds? ActiveCoordinateBounds,
    double MaximumSpeedDeltaMps,
    int MarkerCount,
    string RawPacketFormat,
    string ParsedFrameFormat,
    string ProtocolLayout);

public sealed record Fh5CaptureSnapshot(
    bool IsRunning,
    DateTimeOffset StartedAt,
    long TotalPackets,
    long ValidPackets,
    long InvalidPackets,
    long ActiveDrivingPackets,
    IReadOnlyDictionary<int, long> PacketLengths,
    Fh5DataOutFrame? LatestFrame,
    DateTimeOffset? LatestPacketAt,
    Fh5CoordinateBounds? ActiveCoordinateBounds,
    double MaximumSpeedDeltaMps,
    string? LastError,
    IReadOnlyList<Fh5CoordinateMarker> Markers,
    string OutputPath);
