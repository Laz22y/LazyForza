namespace LazyForza.Domain;

public static class TelemetryDataPartition
{
    public static string TrackSource(TelemetrySourceKind source) => source switch
    {
        TelemetrySourceKind.Live => "fh6_udp_live",
        TelemetrySourceKind.Simulator => "simulator",
        _ => "replay"
    };
}
