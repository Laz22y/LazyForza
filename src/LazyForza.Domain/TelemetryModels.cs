namespace LazyForza.Domain;

public enum TelemetrySourceKind
{
    Live,
    Simulator,
    Replay
}

public enum TelemetryStreamState
{
    Disconnected,
    Connecting,
    Live,
    Replay,
    Stale,
    Faulted
}

public readonly record struct Vector3F(float X, float Y, float Z);

public readonly record struct WheelValues(float FrontLeft, float FrontRight, float RearLeft, float RearRight)
{
    public float this[int index] => index switch
    {
        0 => FrontLeft,
        1 => FrontRight,
        2 => RearLeft,
        3 => RearRight,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public float MaxAbsolute => MathF.Max(
        MathF.Max(MathF.Abs(FrontLeft), MathF.Abs(FrontRight)),
        MathF.Max(MathF.Abs(RearLeft), MathF.Abs(RearRight)));
}

public readonly record struct WheelFlags(int FrontLeft, int FrontRight, int RearLeft, int RearRight);

/// <summary>Officially named FH6 fields at byte offsets 0..322. Byte 323 is retained but has no business meaning.</summary>
public sealed record Fh6RawTelemetry
{
    public int IsRaceOn { get; init; }
    public uint TimestampMS { get; init; }
    public float EngineMaxRpm { get; init; }
    public float EngineIdleRpm { get; init; }
    public float CurrentEngineRpm { get; init; }
    public Vector3F Acceleration { get; init; }
    public Vector3F Velocity { get; init; }
    public Vector3F AngularVelocity { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public float Roll { get; init; }
    public WheelValues NormalizedSuspensionTravel { get; init; }
    public WheelValues TireSlipRatio { get; init; }
    public WheelValues WheelRotationSpeed { get; init; }
    public WheelFlags WheelOnRumbleStrip { get; init; }
    public WheelFlags WheelInPuddle { get; init; }
    public WheelValues SurfaceRumble { get; init; }
    public WheelValues TireSlipAngle { get; init; }
    public WheelValues TireCombinedSlip { get; init; }
    public WheelValues SuspensionTravelMeters { get; init; }
    public int CarOrdinal { get; init; }
    public int CarClass { get; init; }
    public int CarPerformanceIndex { get; init; }
    public int DrivetrainType { get; init; }
    public int NumCylinders { get; init; }
    public uint CarGroup { get; init; }
    public float SmashableVelDiff { get; init; }
    public float SmashableMass { get; init; }
    public Vector3F Position { get; init; }
    public float Speed { get; init; }
    public float Power { get; init; }
    public float Torque { get; init; }
    public WheelValues TireTemperature { get; init; }
    public float Boost { get; init; }
    public float Fuel { get; init; }
    public float DistanceTraveled { get; init; }
    public float BestLap { get; init; }
    public float LastLap { get; init; }
    public float CurrentLap { get; init; }
    public float CurrentRaceTime { get; init; }
    public ushort LapNumber { get; init; }
    public byte RacePosition { get; init; }
    public byte Accel { get; init; }
    public byte Brake { get; init; }
    public byte Clutch { get; init; }
    public byte HandBrake { get; init; }
    public byte Gear { get; init; }
    public sbyte Steer { get; init; }
    public sbyte NormalizedDrivingLine { get; init; }
    public sbyte NormalizedAIBrakeDifference { get; init; }
    public byte UndefinedTailByte { get; init; }
}

public sealed record NormalizedTelemetry(
    double SpeedKph,
    double SpeedMph,
    double PowerKw,
    double AccelRatio,
    double BrakeRatio,
    double ClutchRatio,
    double HandBrakeRatio,
    double RpmRatio,
    WheelValues GripUi);

public sealed record TelemetryFrame(
    long Sequence,
    DateTimeOffset ArrivalTime,
    TelemetrySourceKind Source,
    Fh6RawTelemetry Raw,
    NormalizedTelemetry Normalized,
    ReadOnlyMemory<byte> RawPacket);

public sealed record TelemetryDiagnostics(
    string ListenAddress,
    int ListenPort,
    TelemetryStreamState State,
    long ValidPackets,
    long InvalidPackets,
    long EstimatedDroppedPackets,
    long DuplicatePackets,
    long OutOfOrderPackets,
    long TimestampWraps,
    double PacketsPerSecond,
    DateTimeOffset? LastPacketAt,
    string? LastError);

