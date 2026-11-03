namespace LazyForza.Domain;

/// <summary>Unaligned analysis samples retained while a route is unidentified.</summary>
public sealed record RecordedLapCapture(
    Guid Id, Guid SessionId, TelemetrySourceKind Source, DateTimeOffset StartedAt,
    VehicleProfileFingerprint Vehicle, string? PlayerCode, bool StartConfirmed,
    string? Fault, IReadOnlyList<LapSample> Samples)
{
    public bool StartedAtEventStart { get; init; }
    public TrackTemplate? ReferenceTrack { get; init; }
    public IReadOnlyList<SectorDefinition>? ReferenceSectors { get; init; }
    public bool FinishConfirmed { get; init; }
}
