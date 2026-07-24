using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.Telemetry.Tests;

[TestClass]
public sealed class PacketParserTests
{
    private readonly Fh6PacketParser parser = new();

    [TestMethod]
    public void ParsesCriticalOffsetsAndUndefinedTail()
    {
        var packet = Fh6PacketBuilder.BuildDemoPacket(120);
        Fh6PacketBuilder.WriteInt32(packet, 212, 123456);
        Fh6PacketBuilder.WriteInt32(packet, 216, 5);
        Fh6PacketBuilder.WriteInt32(packet, 220, 888);
        Fh6PacketBuilder.WriteUInt32(packet, 232, 0xDEADBEEF);
        Fh6PacketBuilder.WriteFloat(packet, 236, 4.25f);
        Fh6PacketBuilder.WriteFloat(packet, 240, 17.5f);
        Fh6PacketBuilder.WriteFloat(packet, 244, 101.25f);
        Fh6PacketBuilder.WriteFloat(packet, 248, 202.5f);
        Fh6PacketBuilder.WriteFloat(packet, 252, -303.75f);
        packet[315] = 255;
        packet[316] = 128;
        packet[319] = 6;
        packet[320] = unchecked((byte)(sbyte)-127);
        packet[321] = 42;
        packet[322] = unchecked((byte)(sbyte)-24);
        packet[323] = 0x7E;

        Assert.IsTrue(parser.TryParse(packet, 9, DateTimeOffset.UnixEpoch, TelemetrySourceKind.Replay, out var frame, out var error), error);
        Assert.IsNotNull(frame);
        Assert.AreEqual(123456, frame.Raw.CarOrdinal);
        Assert.AreEqual(5, frame.Raw.CarClass);
        Assert.AreEqual(888, frame.Raw.CarPerformanceIndex);
        Assert.AreEqual(0xDEADBEEFu, frame.Raw.CarGroup);
        Assert.AreEqual(4.25f, frame.Raw.SmashableVelDiff);
        Assert.AreEqual(17.5f, frame.Raw.SmashableMass);
        Assert.AreEqual(new Vector3F(101.25f, 202.5f, -303.75f), frame.Raw.Position);
        Assert.AreEqual(1d, frame.Normalized.AccelRatio, 1e-9);
        Assert.AreEqual(128 / 255d, frame.Normalized.BrakeRatio, 1e-9);
        Assert.AreEqual((sbyte)-127, frame.Raw.Steer);
        Assert.AreEqual((sbyte)-24, frame.Raw.NormalizedAIBrakeDifference);
        Assert.AreEqual(0x7E, frame.Raw.UndefinedTailByte);
    }

    [DataTestMethod]
    [DataRow(323)]
    [DataRow(325)]
    public void RejectsWrongLength(int length)
    {
        Assert.IsFalse(parser.TryParse(new byte[length], 0, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out _, out var error));
        StringAssert.Contains(error, "324");
    }

    [TestMethod]
    public void RejectsWrongEndianRaceFlag()
    {
        var packet = Fh6PacketBuilder.BuildDemoPacket(1);
        packet[0] = 0;
        packet[3] = 1; // big-endian representation of one; little-endian reads 16,777,216
        Assert.IsFalse(parser.TryParse(packet, 0, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out _, out var error));
        StringAssert.Contains(error, "little-endian");
    }

    [TestMethod]
    public void PreservesTemperatureWithoutInventingUnit()
    {
        var packet = Fh6PacketBuilder.BuildDemoPacket(10);
        Fh6PacketBuilder.WriteFloat(packet, 268, 123.45f);
        Assert.IsTrue(parser.TryParse(packet, 0, DateTimeOffset.UtcNow, TelemetrySourceKind.Simulator, out var frame, out _));
        Assert.AreEqual(123.45f, frame!.Raw.TireTemperature.FrontLeft);
    }

    [DataTestMethod]
    [DataRow((byte)0, "R", null)]
    [DataRow((byte)1, "1", 1)]
    [DataRow((byte)2, "2", 2)]
    [DataRow((byte)7, "7", 7)]
    [DataRow((byte)255, "—", null)]
    public void FormatsWireGearCodesForDrivers(byte rawCode, string display, int? forwardGear)
    {
        Assert.AreEqual(display, ForzaGear.Display(rawCode));
        Assert.AreEqual(forwardGear, ForzaGear.ForwardNumber(rawCode));
    }

    [TestMethod]
    public void HoldsLastKnownGearAcrossShortUnknownDownshiftCode()
    {
        var stabilizer = new GearDisplayStabilizer(TimeSpan.FromMilliseconds(350));
        var start = DateTimeOffset.UnixEpoch;

        var third = stabilizer.Resolve(3, start, true);
        var transition = stabilizer.Resolve(255, start.AddMilliseconds(100), true);
        var second = stabilizer.Resolve(2, start.AddMilliseconds(160), true);

        Assert.AreEqual(new ResolvedGear(3, "3", false), third);
        Assert.AreEqual(new ResolvedGear(3, "3", true), transition);
        Assert.AreEqual(new ResolvedGear(2, "2", false), second);
    }

    [TestMethod]
    public void StopsHoldingGearWhenUnknownCodePersistsOrDrivingStops()
    {
        var stabilizer = new GearDisplayStabilizer(TimeSpan.FromMilliseconds(350));
        var start = DateTimeOffset.UnixEpoch;

        stabilizer.Resolve(4, start, true);
        var expired = stabilizer.Resolve(255, start.AddMilliseconds(351), true);
        var paused = stabilizer.Resolve(4, start.AddMilliseconds(400), false);
        var unknownAfterPause = stabilizer.Resolve(255, start.AddMilliseconds(450), true);

        Assert.AreEqual(new ResolvedGear(null, "—", false), expired);
        Assert.AreEqual(new ResolvedGear(null, "—", false), paused);
        Assert.AreEqual(new ResolvedGear(null, "—", false), unknownAfterPause);
    }
}
