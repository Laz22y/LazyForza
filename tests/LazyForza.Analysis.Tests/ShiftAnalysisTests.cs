using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Telemetry;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class ShiftAnalysisTests
{
    [TestMethod]
    public void ComputesIndependentPerGearCrossingsAndCueAdvance()
    {
        var curve = Enumerable.Range(0, 27).Select(index =>
        {
            var rpm = 2000 + index * 250;
            var torque = Math.Max(250, 800 - 0.08 * Math.Max(0, rpm - 3000));
            return new EngineCurveBin(rpm, 20, torque * rpm * Math.PI / 30, torque, 12, 2, 0.95);
        }).ToArray();
        var gears = new[] { new GearModel(2, 250, 50, 0.9), new GearModel(3, 180, 50, 0.9), new GearModel(4, 135, 50, 0.9) };
        var targets = ShiftPointCalculator.Calculate(curve, gears, 8500, rpmRiseRate: 2000, totalLatencySeconds: 0.2);
        Assert.AreEqual(2, targets.Count);
        Assert.IsTrue(targets[0].TargetRpm is > 6000 and < 8500);
        Assert.AreNotEqual(targets[0].TargetRpm, targets[1].TargetRpm, 1);
        Assert.AreEqual(400, targets[0].TargetRpm - targets[0].CueRpm, 2);
        Assert.IsFalse(targets[0].UsedLimiterFallback);
    }

    [TestMethod]
    public void UsesSafeLimiterFallbackWhenNoCrossingExists()
    {
        var curve = Enumerable.Range(0, 25).Select(index => new EngineCurveBin(2500 + index * 250, 20, 300000, 500, 0, 0, 1)).ToArray();
        var targets = ShiftPointCalculator.Calculate(curve, [new GearModel(3, 180, 30, 1), new GearModel(4, 130, 30, 1)], 8500);
        Assert.AreEqual(1, targets.Count);
        Assert.IsTrue(targets[0].UsedLimiterFallback);
        Assert.IsTrue(targets[0].TargetRpm < 8500);
    }

    [TestMethod]
    public void LearnerRejectsSlipAndImmediatelySwitchesChangedVehicleConfiguration()
    {
        var learner = new ShiftLearner(new ShiftLearnerOptions(MinimumSamplesPerBin: 1, MinimumReadyBins: 2, MinimumGearSamples: 2));
        var parser = new Fh6PacketParser();
        for (var index = 0; index < 30; index++)
        {
            var packet = StablePacket(index, gear: index < 15 ? 3 : 4, rpm: 3500 + index * 100);
            Assert.IsTrue(parser.TryParse(packet, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        var slipped = StablePacket(31, 4, 6500);
        for (var offset = 180; offset <= 192; offset += 4) Fh6PacketBuilder.WriteFloat(slipped, offset, 1.4f);
        Assert.IsTrue(parser.TryParse(slipped, 31, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var slipFrame, out _));
        learner.Observe(slipFrame!);
        Assert.IsTrue(learner.Snapshot.RejectedSamples.ContainsKey("slip"));

        var changed = StablePacket(32, 4, 6600);
        Fh6PacketBuilder.WriteInt32(changed, 212, 918);
        Assert.IsTrue(parser.TryParse(changed, 32, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var changedFrame, out _));
        learner.Observe(changedFrame!);
        Assert.AreEqual(LearningState.Collecting, learner.Snapshot.State);
        Assert.AreEqual(918, learner.Snapshot.Fingerprint?.CarOrdinal);
        Assert.AreEqual(0, learner.Snapshot.AcceptedSamples);
        Assert.IsTrue(learner.Snapshot.RejectedSamples.ContainsKey("configuration-changed"));

        var next = StablePacket(33, 4, 6700);
        Fh6PacketBuilder.WriteInt32(next, 212, 918);
        Assert.IsTrue(parser.TryParse(next, 33, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var nextFrame, out _));
        learner.Observe(nextFrame!);
        Assert.AreEqual(918, learner.Snapshot.Fingerprint?.CarOrdinal);
        Assert.AreNotEqual(LearningState.Stale, learner.Snapshot.State);
    }

    [TestMethod]
    public void MenuZeroFramesDoNotCreateAZeroVehicleFingerprint()
    {
        var learner = new ShiftLearner();
        var parser = new Fh6PacketParser();
        var menu = Fh6PacketBuilder.BuildDemoPacket(0);
        Fh6PacketBuilder.WriteInt32(menu, 0, 0);
        Fh6PacketBuilder.WriteInt32(menu, 212, 0);
        Fh6PacketBuilder.WriteInt32(menu, 220, 0);
        Assert.IsTrue(parser.TryParse(menu, 0, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out var menuFrame, out _));
        learner.Observe(menuFrame!);
        Assert.IsNull(learner.Snapshot.Fingerprint);

        var driving = StablePacket(1, 1, 3000);
        Assert.IsTrue(parser.TryParse(driving, 1, DateTimeOffset.UtcNow, TelemetrySourceKind.Live, out var drivingFrame, out _));
        learner.Observe(drivingFrame!);
        Assert.AreEqual(6001, learner.Snapshot.Fingerprint?.CarOrdinal);
    }

    [TestMethod]
    public void SameCarOrdinalAndPiWithChangedGearRatioStartsANewTuneProfile()
    {
        var learner = new ShiftLearner(new ShiftLearnerOptions(
            MinimumSamplesPerBin: 2,
            MinimumReadyBins: 2,
            MinimumGearSamples: 3,
            TuneMismatchSamples: 3));
        var parser = new Fh6PacketParser();

        for (var index = 0; index < 9; index++)
        {
            var packet = StablePacket(index, 3, 4_000);
            Assert.IsTrue(parser.TryParse(
                packet, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        var original = learner.Snapshot;
        Assert.IsTrue(original.AcceptedSamples >= 3);
        var originalOrdinal = original.Fingerprint?.CarOrdinal;
        var originalPi = original.Fingerprint?.PerformanceIndex;

        for (var index = 9; index < 12; index++)
        {
            var changedRatio = StablePacket(index, 3, 4_800);
            Assert.IsTrue(parser.TryParse(
                changedRatio, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        var changed = learner.Snapshot;
        Assert.IsTrue(changed.ConfigurationRevision > original.ConfigurationRevision);
        Assert.AreEqual(originalOrdinal, changed.Fingerprint?.CarOrdinal);
        Assert.AreEqual(originalPi, changed.Fingerprint?.PerformanceIndex);
        Assert.AreEqual(0, changed.AcceptedSamples);
        Assert.IsTrue(changed.RejectedSamples.ContainsKey("tune-signature-changed"));
    }

    [TestMethod]
    public void SameCarOrdinalAndPiWithChangedPowerCurveStartsANewTuneProfile()
    {
        var learner = new ShiftLearner(new ShiftLearnerOptions(
            MinimumSamplesPerBin: 3,
            MinimumReadyBins: 2,
            MinimumGearSamples: 100,
            TuneMismatchSamples: 3));
        var parser = new Fh6PacketParser();

        for (var index = 0; index < 8; index++)
        {
            var packet = StablePacket(index, 3, 4_000);
            Fh6PacketBuilder.WriteFloat(packet, 260, 200_000);
            Assert.IsTrue(parser.TryParse(
                packet, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        var original = learner.Snapshot;
        for (var index = 8; index < 11; index++)
        {
            var changedPower = StablePacket(index, 3, 4_000);
            Fh6PacketBuilder.WriteFloat(changedPower, 260, 260_000);
            Assert.IsTrue(parser.TryParse(
                changedPower, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        Assert.IsTrue(learner.Snapshot.ConfigurationRevision > original.ConfigurationRevision);
        Assert.IsTrue(learner.Snapshot.RejectedSamples.ContainsKey("tune-signature-changed"));
    }

    [TestMethod]
    public void ReadyLearnerEmitsStablePersistableTuneSignatures()
    {
        var learner = new ShiftLearner(new ShiftLearnerOptions(
            MinimumSamplesPerBin: 1,
            MinimumReadyBins: 2,
            MinimumGearSamples: 2));
        var parser = new Fh6PacketParser();
        var observations = new[]
        {
            (Gear: 3, Rpm: 4_000f, Speed: 32f),
            (Gear: 3, Rpm: 4_200f, Speed: 33.6f),
            (Gear: 3, Rpm: 4_400f, Speed: 35.2f),
            (Gear: 4, Rpm: 4_000f, Speed: 40f),
            (Gear: 4, Rpm: 4_200f, Speed: 42f),
            (Gear: 4, Rpm: 4_400f, Speed: 44f)
        };

        for (var index = 0; index < observations.Length; index++)
        {
            var observation = observations[index];
            var packet = StablePacket(index, observation.Gear, observation.Rpm);
            Fh6PacketBuilder.WriteFloat(packet, 256, observation.Speed);
            Assert.IsTrue(parser.TryParse(
                packet, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        var first = learner.Snapshot.Fingerprint;
        Assert.IsTrue(VehicleProfileIdentity.IsResolved(first));
        var firstId = VehicleProfileIdentity.Create(first!);

        for (var index = observations.Length; index < observations.Length + 4; index++)
        {
            var packet = StablePacket(index, 4, 4_600);
            Fh6PacketBuilder.WriteFloat(packet, 256, 46);
            Assert.IsTrue(parser.TryParse(
                packet, index, DateTimeOffset.UtcNow, TelemetrySourceKind.Replay, out var frame, out _));
            learner.Observe(frame!);
        }

        Assert.AreEqual(firstId, VehicleProfileIdentity.Create(learner.Snapshot.Fingerprint!),
            "同一学习会话达到门槛后，档案 ID 不应随样本继续增加而漂移。");
    }

    private static byte[] StablePacket(int index, int gear, float rpm)
    {
        var packet = Fh6PacketBuilder.BuildDemoPacket(index);
        Fh6PacketBuilder.WriteUInt32(packet, 4, (uint)(index * 16));
        Fh6PacketBuilder.WriteFloat(packet, 16, rpm);
        Fh6PacketBuilder.WriteFloat(packet, 256, 32);
        Fh6PacketBuilder.WriteFloat(packet, 244, index * 0.5f);
        Fh6PacketBuilder.WriteFloat(packet, 252, 0);
        packet[315] = 255;
        packet[317] = 0;
        packet[319] = (byte)gear;
        for (var offset = 84; offset <= 96; offset += 4) Fh6PacketBuilder.WriteFloat(packet, offset, 0.03f);
        for (var offset = 180; offset <= 192; offset += 4) Fh6PacketBuilder.WriteFloat(packet, offset, 0.05f);
        return packet;
    }
}
