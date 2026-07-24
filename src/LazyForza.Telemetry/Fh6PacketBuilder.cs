using System.Buffers.Binary;

namespace LazyForza.Telemetry;

/// <summary>Deterministic synthetic fixture. It is always labelled Simulator/Demo and is not FH6 capture data.</summary>
public static class Fh6PacketBuilder
{
    public static byte[] BuildDemoPacket(long frameIndex, int hertz = 60)
    {
        if (hertz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hertz));
        }

        var seconds = frameIndex / (double)hertz;
        // The geometric loop is roughly 1.1 km at ~42 m/s, so 26 seconds is both deterministic and plausible.
        var lapSeconds = 26.0;
        var phase = (seconds % lapSeconds) / lapSeconds;
        var angle = phase * Math.PI * 2;
        var speed = 42f + (float)(10 * Math.Sin(angle * 3));
        var gear = speed switch { < 18 => 2, < 30 => 3, < 42 => 4, < 52 => 5, _ => 6 };
        var gearSlope = new[] { 0d, 0d, 250d, 180d, 135d, 105d, 86d };
        var rpm = (float)Math.Clamp(speed * gearSlope[gear] + 900 + (180 * Math.Sin(angle * 5)), 950, 8400);
        var throttle = (byte)Math.Clamp((int)Math.Round(155 + (100 * Math.Sin(angle * 2))), 0, 255);
        var braking = Math.Sin((angle - 0.8) * 3) > 0.82 ? 150 : 0;
        var radiusX = 190 + (35 * Math.Sin(angle * 2));
        var radiusZ = 155 + (25 * Math.Cos(angle * 3));
        var x = (float)(radiusX * Math.Cos(angle));
        var z = (float)(radiusZ * Math.Sin(angle));
        var packet = new byte[Fh6PacketParser.PacketLength];

        I32(packet, 0, 1);
        U32(packet, 4, unchecked((uint)Math.Round(seconds * 1000)));
        F32(packet, 8, 8500);
        F32(packet, 12, 900);
        F32(packet, 16, rpm);
        F32(packet, 24, 0.05f);
        F32(packet, 28, 0.2f);
        F32(packet, 40, speed);
        F32(packet, 48, (float)(speed / 150 * Math.Sin(angle)));
        F32(packet, 56, (float)(angle + (Math.PI / 2)));

        for (var offset = 68; offset <= 80; offset += 4) F32(packet, offset, 0.45f);
        var slip = throttle > 230 ? 0.12f : 0.04f;
        for (var offset = 84; offset <= 96; offset += 4) F32(packet, offset, slip);
        for (var offset = 100; offset <= 112; offset += 4) F32(packet, offset, speed / 0.34f);
        for (var offset = 164; offset <= 176; offset += 4) F32(packet, offset, 0.08f + (0.03f * MathF.Abs(MathF.Sin((float)angle))));
        for (var offset = 180; offset <= 192; offset += 4) F32(packet, offset, slip + 0.03f);
        for (var offset = 196; offset <= 208; offset += 4) F32(packet, offset, 0.09f);

        I32(packet, 212, 6001);
        I32(packet, 216, 6);
        I32(packet, 220, 917);
        I32(packet, 224, 2);
        I32(packet, 228, 8);
        U32(packet, 232, 42);
        F32(packet, 244, x);
        F32(packet, 248, (float)(4 + (1.5 * Math.Sin(angle * 2))));
        F32(packet, 252, z);
        F32(packet, 256, speed);
        var torque = 610f - (0.0000068f * MathF.Pow(rpm - 5200, 2));
        torque = MathF.Max(300, torque);
        F32(packet, 260, torque * rpm * (2 * MathF.PI / 60));
        F32(packet, 264, torque);
        F32(packet, 268, 88 + (float)(2 * Math.Sin(angle)));
        F32(packet, 272, 90 + (float)(2 * Math.Cos(angle)));
        F32(packet, 276, 86 + (float)(3 * Math.Sin(angle + 0.2)));
        F32(packet, 280, 87 + (float)(3 * Math.Cos(angle + 0.2)));
        F32(packet, 284, 13.5f);
        F32(packet, 288, 0.78f);
        F32(packet, 292, (float)(seconds * speed));
        F32(packet, 296, 51.4f);
        F32(packet, 300, frameIndex >= lapSeconds * hertz ? 52.0f : 0);
        F32(packet, 304, (float)(seconds % lapSeconds));
        F32(packet, 308, (float)seconds);
        U16(packet, 312, (ushort)(seconds / lapSeconds));
        packet[314] = 1;
        packet[315] = throttle;
        packet[316] = (byte)braking;
        packet[317] = 0;
        packet[318] = 0;
        packet[319] = (byte)gear; // FH6 live evidence: 0=R and 1=1st; no separate neutral code observed.
        packet[320] = unchecked((byte)(sbyte)Math.Clamp((int)(75 * Math.Sin(angle)), -127, 127));
        packet[323] = 0xA5; // Retained only to prove the undefined tail is preserved.
        return packet;
    }

    public static void WriteInt32(Span<byte> packet, int offset, int value) => I32(packet, offset, value);
    public static void WriteUInt32(Span<byte> packet, int offset, uint value) => U32(packet, offset, value);
    public static void WriteFloat(Span<byte> packet, int offset, float value) => F32(packet, offset, value);

    private static void I32(Span<byte> packet, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(packet[offset..], value);
    private static void U32(Span<byte> packet, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(packet[offset..], value);
    private static void U16(Span<byte> packet, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(packet[offset..], value);
    private static void F32(Span<byte> packet, int offset, float value) => I32(packet, offset, BitConverter.SingleToInt32Bits(value));
}
