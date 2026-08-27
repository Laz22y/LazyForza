using System.Buffers.Binary;

namespace LazyForza.Fh5MapProbe;

public sealed record Fh5DataOutFrame(
    int PacketLength,
    int IsRaceOn,
    uint TimestampMs,
    float EngineMaxRpm,
    float CurrentEngineRpm,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    float Yaw,
    float Pitch,
    float Roll,
    int CarOrdinal,
    int CarClass,
    int PerformanceIndex,
    int DrivetrainType,
    int NumCylinders,
    int CarCategory,
    uint HorizonUnknown1,
    uint HorizonUnknown2,
    float PositionX,
    float PositionY,
    float PositionZ,
    float SpeedMps,
    float DistanceTraveledMeters,
    float CurrentLapSeconds,
    float CurrentRaceSeconds,
    ushort LapNumber,
    byte RacePosition,
    byte Accel,
    byte Brake,
    byte Gear,
    sbyte Steer,
    double VelocityMagnitudeMps,
    double SpeedDeltaMps);

public static class Fh5DataOutParser
{
    public const int ShortPacketLength = 323;
    public const int PaddedPacketLength = 324;
    public const int PositionOffset = 244;

    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        out Fh5DataOutFrame? frame,
        out string? error)
    {
        frame = null;
        if (packet.Length is not (ShortPacketLength or PaddedPacketLength))
        {
            error = "包长不是 FH5 Horizon Dash 的 323 或 324 字节。";
            return false;
        }

        var isRaceOn = I32(packet, 0);
        if (isRaceOn is not (0 or 1))
        {
            error = "IsRaceOn 不是 0 或 1，字节序或布局不匹配。";
            return false;
        }

        var velocityX = F32(packet, 32);
        var velocityY = F32(packet, 36);
        var velocityZ = F32(packet, 40);
        var positionX = F32(packet, PositionOffset);
        var positionY = F32(packet, PositionOffset + 4);
        var positionZ = F32(packet, PositionOffset + 8);
        var speed = F32(packet, 256);
        var velocityMagnitude = Math.Sqrt(
            velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ);
        var speedDelta = Math.Abs(speed - velocityMagnitude);
        var engineMaxRpm = F32(packet, 8);
        var currentEngineRpm = F32(packet, 16);
        var carClass = I32(packet, 216);
        var performanceIndex = I32(packet, 220);
        var drivetrain = I32(packet, 224);
        var finiteValues = new[]
        {
            engineMaxRpm,
            currentEngineRpm,
            velocityX,
            velocityY,
            velocityZ,
            F32(packet, 56),
            F32(packet, 60),
            F32(packet, 64),
            positionX,
            positionY,
            positionZ,
            speed,
            F32(packet, 292),
            F32(packet, 304),
            F32(packet, 308)
        };
        if (finiteValues.Any(value => !float.IsFinite(value)) ||
            !double.IsFinite(velocityMagnitude))
        {
            error = "关键字段包含非有限数值。";
            return false;
        }

        if (engineMaxRpm is < 0 or > 30_000 || currentEngineRpm is < -100 or > 35_000 ||
            speed is < -1 or > 1_000 || Math.Abs(positionX) > 10_000_000 ||
            Math.Abs(positionY) > 10_000_000 || Math.Abs(positionZ) > 10_000_000 ||
            carClass is < 0 or > 7 ||
            performanceIndex != 0 && performanceIndex is < 100 or > 999 ||
            drivetrain is < 0 or > 2)
        {
            error = "关键字段超出保守合理范围。";
            return false;
        }

        var movingSpeed = Math.Max(speed, velocityMagnitude);
        if (isRaceOn == 1 && movingSpeed > 2 && speedDelta > Math.Max(2.5, movingSpeed * 0.12))
        {
            error = "Speed 与三轴 Velocity 模长不一致，位置段偏移可能不匹配。";
            return false;
        }

        frame = new Fh5DataOutFrame(
            packet.Length,
            isRaceOn,
            U32(packet, 4),
            engineMaxRpm,
            currentEngineRpm,
            velocityX,
            velocityY,
            velocityZ,
            F32(packet, 56),
            F32(packet, 60),
            F32(packet, 64),
            I32(packet, 212),
            carClass,
            performanceIndex,
            drivetrain,
            I32(packet, 228),
            I32(packet, 232),
            U32(packet, 236),
            U32(packet, 240),
            positionX,
            positionY,
            positionZ,
            speed,
            F32(packet, 292),
            F32(packet, 304),
            F32(packet, 308),
            U16(packet, 312),
            packet[314],
            packet[315],
            packet[316],
            packet[319],
            unchecked((sbyte)packet[320]),
            velocityMagnitude,
            speedDelta);
        error = null;
        return true;
    }

    private static int I32(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(value[offset..]);

    private static uint U32(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);

    private static ushort U16(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(value[offset..]);

    private static float F32(ReadOnlySpan<byte> value, int offset) =>
        BitConverter.Int32BitsToSingle(I32(value, offset));
}
