namespace LazyForza.Domain;

public readonly record struct EstateGatePoint(double X, double Y, double Z);

/// <summary>
/// A finite, directed timing gate. Left/right describe the physical line and
/// ForwardX/ForwardZ identify the only direction that may trigger timing.
/// </summary>
public sealed record EstateTimingGate(
    EstateGatePoint Left,
    EstateGatePoint Right,
    double ForwardX,
    double ForwardZ,
    double FitRmsMeters,
    double TraceOffsetMeters,
    double TraceAngleDifferenceDegrees,
    double HeightToleranceMeters = 2.5,
    double EndpointMarginMeters = 0.75)
{
    public bool HasDirection => Math.Sqrt(ForwardX * ForwardX + ForwardZ * ForwardZ) > 0.9;
}

public sealed record EstateCheckpoint(
    int Index,
    EstateTimingGate Gate,
    double RouteProgressMeters);

/// <summary>
/// Reserved in phase one so track packages remain forward-compatible with
/// race-control pit rules. Tire wear and damage reset are deliberately absent:
/// FH6 Data Out does not expose either as verifiable telemetry.
/// </summary>
public sealed record EstatePitDefinition(
    EstateTimingGate EntryGate,
    EstateTimingGate ExitGate,
    IReadOnlyList<EstateGatePoint> CenterLine,
    EstateGatePoint ServiceCenter,
    double ServiceRadiusMeters,
    double SpeedLimitKph,
    double MinimumServiceSeconds);

public sealed record EstateTrackDefinition(
    Guid TrackId,
    string MapName,
    string? Creator,
    string? ShareCode,
    string MapRevision,
    EstateTimingGate StartFinishGate,
    IReadOnlyList<EstateCheckpoint> Checkpoints,
    EstatePitDefinition? Pit,
    double ReferenceLapSeconds,
    double ValidationLapSeconds,
    double ValidationProjectionRatio,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
