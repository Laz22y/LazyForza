using System.Buffers.Binary;
using LazyForza.Domain;

namespace LazyForza.Storage;

internal static class LapDynamicsCodec
{
    public const int EncodedSize = 27;
    private const byte CurrentVersion = 1;
    private const double SteeringScale = 32767;
    private const double SlipScale = 4096;
    private const double MaximumSlipMagnitude = 7.999;

    public static byte[] Encode(LapDynamics dynamics)
    {
        var bytes = new byte[EncodedSize];
        bytes[0] = CurrentVersion;
        var offset = 1;
        Write(bytes, ref offset, dynamics.Steering, SteeringScale, 1);
        WriteWheelValues(bytes, ref offset, dynamics.TireSlipRatio);
        WriteWheelValues(bytes, ref offset, dynamics.TireSlipAngle);
        WriteWheelValues(bytes, ref offset, dynamics.TireCombinedSlip);
        return bytes;
    }

    public static LapDynamics? DecodeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
        return Decode(bytes);
    }

    public static string EncodeHex(LapDynamics dynamics) =>
        Convert.ToHexString(Encode(dynamics));

    private static LapDynamics? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedSize || bytes[0] != CurrentVersion) return null;
        var offset = 1;
        var steering = Read(bytes, ref offset, SteeringScale);
        var ratio = ReadWheelValues(bytes, ref offset);
        var angle = ReadWheelValues(bytes, ref offset);
        var combined = ReadWheelValues(bytes, ref offset);
        return new LapDynamics(steering, ratio, angle, combined);
    }

    private static void WriteWheelValues(
        Span<byte> destination,
        ref int offset,
        WheelValues values)
    {
        Write(destination, ref offset, values.FrontLeft, SlipScale, MaximumSlipMagnitude);
        Write(destination, ref offset, values.FrontRight, SlipScale, MaximumSlipMagnitude);
        Write(destination, ref offset, values.RearLeft, SlipScale, MaximumSlipMagnitude);
        Write(destination, ref offset, values.RearRight, SlipScale, MaximumSlipMagnitude);
    }

    private static WheelValues ReadWheelValues(
        ReadOnlySpan<byte> source,
        ref int offset) =>
        new(
            (float)Read(source, ref offset, SlipScale),
            (float)Read(source, ref offset, SlipScale),
            (float)Read(source, ref offset, SlipScale),
            (float)Read(source, ref offset, SlipScale));

    private static void Write(
        Span<byte> destination,
        ref int offset,
        double value,
        double scale,
        double maximumMagnitude)
    {
        var normalized = double.IsFinite(value)
            ? Math.Clamp(value, -maximumMagnitude, maximumMagnitude)
            : 0;
        var quantized = checked((short)Math.Round(normalized * scale));
        BinaryPrimitives.WriteInt16LittleEndian(destination[offset..], quantized);
        offset += sizeof(short);
    }

    private static double Read(
        ReadOnlySpan<byte> source,
        ref int offset,
        double scale)
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(source[offset..]);
        offset += sizeof(short);
        return value / scale;
    }
}
