using System.Buffers.Binary;
using LazyForza.Domain;

namespace LazyForza.Telemetry;

public sealed class Fh6PacketParser
{
    public const int PacketLength = 324;

    public bool TryParse(
        ReadOnlySpan<byte> packet,
        long sequence,
        DateTimeOffset arrivalTime,
        TelemetrySourceKind source,
        out TelemetryFrame? frame,
        out string? error)
    {
        frame = null;
        if (packet.Length != PacketLength)
        {
            error = $"Expected {PacketLength} bytes, received {packet.Length}.";
            return false;
        }

        var raw = new Fh6RawTelemetry
        {
            IsRaceOn = I32(packet, 0),
            TimestampMS = U32(packet, 4),
            EngineMaxRpm = F32(packet, 8),
            EngineIdleRpm = F32(packet, 12),
            CurrentEngineRpm = F32(packet, 16),
            Acceleration = V3(packet, 20),
            Velocity = V3(packet, 32),
            AngularVelocity = V3(packet, 44),
            Yaw = F32(packet, 56),
            Pitch = F32(packet, 60),
            Roll = F32(packet, 64),
            NormalizedSuspensionTravel = W4(packet, 68),
            TireSlipRatio = W4(packet, 84),
            WheelRotationSpeed = W4(packet, 100),
            WheelOnRumbleStrip = WI4(packet, 116),
            WheelInPuddle = WI4(packet, 132),
            SurfaceRumble = W4(packet, 148),
            TireSlipAngle = W4(packet, 164),
            TireCombinedSlip = W4(packet, 180),
            SuspensionTravelMeters = W4(packet, 196),
            CarOrdinal = I32(packet, 212),
            CarClass = I32(packet, 216),
            CarPerformanceIndex = I32(packet, 220),
            DrivetrainType = I32(packet, 224),
            NumCylinders = I32(packet, 228),
            CarGroup = U32(packet, 232),
            SmashableVelDiff = F32(packet, 236),
            SmashableMass = F32(packet, 240),
            Position = V3(packet, 244),
            Speed = F32(packet, 256),
            Power = F32(packet, 260),
            Torque = F32(packet, 264),
            TireTemperature = W4(packet, 268),
            Boost = F32(packet, 284),
            Fuel = F32(packet, 288),
            DistanceTraveled = F32(packet, 292),
            BestLap = F32(packet, 296),
            LastLap = F32(packet, 300),
            CurrentLap = F32(packet, 304),
            CurrentRaceTime = F32(packet, 308),
            LapNumber = U16(packet, 312),
            RacePosition = packet[314],
            Accel = packet[315],
            Brake = packet[316],
            Clutch = packet[317],
            HandBrake = packet[318],
            Gear = packet[319],
            Steer = unchecked((sbyte)packet[320]),
            NormalizedDrivingLine = unchecked((sbyte)packet[321]),
            NormalizedAIBrakeDifference = unchecked((sbyte)packet[322]),
            UndefinedTailByte = packet[323]
        };

        if (!IsPlausible(raw, out error))
        {
            return false;
        }

        var grip = new WheelValues(
            Grip(raw.TireCombinedSlip.FrontLeft),
            Grip(raw.TireCombinedSlip.FrontRight),
            Grip(raw.TireCombinedSlip.RearLeft),
            Grip(raw.TireCombinedSlip.RearRight));
        var normalized = new NormalizedTelemetry(
            raw.Speed * 3.6,
            raw.Speed * 2.236936,
            raw.Power / 1000.0,
            raw.Accel / 255.0,
            raw.Brake / 255.0,
            raw.Clutch / 255.0,
            raw.HandBrake / 255.0,
            raw.EngineMaxRpm > 0 ? Math.Clamp(raw.CurrentEngineRpm / raw.EngineMaxRpm, 0, 1.2) : 0,
            grip);
        frame = new TelemetryFrame(sequence, arrivalTime, source, raw, normalized, packet.ToArray());
        error = null;
        return true;
    }

    private static bool IsPlausible(Fh6RawTelemetry raw, out string? error)
    {
        if (raw.IsRaceOn is not (0 or 1))
        {
            error = "IsRaceOn is not 0 or 1; little-endian/layout assumption failed.";
            return false;
        }

        var finite = new[]
        {
            raw.EngineMaxRpm, raw.EngineIdleRpm, raw.CurrentEngineRpm, raw.Speed, raw.Power, raw.Torque,
            raw.Position.X, raw.Position.Y, raw.Position.Z, raw.Fuel, raw.CurrentLap, raw.CurrentRaceTime
        };
        if (finite.Any(value => !float.IsFinite(value)))
        {
            error = "Packet contains a non-finite key value.";
            return false;
        }

        if (raw.EngineMaxRpm is < 0 or > 30000 || raw.CurrentEngineRpm is < -100 or > 35000)
        {
            error = "RPM is outside a conservative plausibility range.";
            return false;
        }

        if (raw.Speed is < -1 or > 1000 || raw.CarClass is < 0 or > 7 ||
            (raw.CarPerformanceIndex != 0 && raw.CarPerformanceIndex is < 100 or > 999) ||
            raw.DrivetrainType is < 0 or > 2 || raw.Fuel is < -0.05f or > 1.05f)
        {
            error = "One or more official range checks failed.";
            return false;
        }

        error = null;
        return true;
    }

    private static float Grip(float slip) => Math.Clamp(1 - MathF.Abs(slip), 0, 1);
    private static int I32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadInt32LittleEndian(value[offset..]);
    private static uint U32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);
    private static ushort U16(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(value[offset..]);
    private static float F32(ReadOnlySpan<byte> value, int offset) => BitConverter.Int32BitsToSingle(I32(value, offset));
    private static Vector3F V3(ReadOnlySpan<byte> value, int offset) => new(F32(value, offset), F32(value, offset + 4), F32(value, offset + 8));
    private static WheelValues W4(ReadOnlySpan<byte> value, int offset) =>
        new(F32(value, offset), F32(value, offset + 4), F32(value, offset + 8), F32(value, offset + 12));
    private static WheelFlags WI4(ReadOnlySpan<byte> value, int offset) =>
        new(I32(value, offset), I32(value, offset + 4), I32(value, offset + 8), I32(value, offset + 12));
}

