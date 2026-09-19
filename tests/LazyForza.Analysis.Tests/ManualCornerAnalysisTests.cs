using LazyForza.Analysis;
using LazyForza.Domain;

namespace LazyForza.Analysis.Tests;

[TestClass]
public sealed class ManualCornerAnalysisTests
{
    private static readonly Guid TrackId = Guid.Parse("19c38bfc-a5e1-48d1-8be7-77efb6cc7d2c");
    private static readonly DateTimeOffset Revision = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ManualCorner Corner = new("T1", 80, 400);

    [TestMethod]
    public void LegacyAndInvalidLapsCanBeInspectedWithoutComparisonMetadata()
    {
        var original = Lap();
        var legacy = original with { TrackRevision = null, IsValid = false,
            Vehicle = original.Vehicle with { DrivetrainType = -1, NumCylinders = -1, GearSlopeSignature = "", CurveSignature = "" } };
        var result = ManualCornerAnalyzer.Analyze(legacy, Corner);
        Assert.AreEqual(CornerEvidence.Sufficient, result.Evidence);
        Assert.AreEqual(6.4, result.Selected!.Seconds, 1e-9);
        Assert.AreEqual(72, result.Selected.MinimumSpeedKph, 1e-9);
        Assert.IsNotNull(result.Selected.BrakeStartS);
        Assert.IsNotNull(result.Selected.ThrottleRecoveryS);
        Assert.IsNull(result.Reference);
        Assert.IsEmpty(ManualCornerAnalyzer.Describe([result]), "A single lap must not invent comparison advice.");
        Assert.AreEqual(CornerEvidence.Incompatible, ManualCornerAnalyzer.Compare(Track(), legacy, Lap(), Corner).Evidence);
        Assert.IsNull(legacy.TrackRevision, "Inspection must not manufacture missing historical context.");
    }

    [TestMethod]
    public void SingleLapAnalysisKeepsUnknownInputsPartialAndRejectsOnlyUnusableIntervals()
    {
        var lap = Lap();
        var constantInputs = lap with { Samples = lap.Samples.Select(sample => sample with { Brake = 0, Accel = 1 }).ToArray() };
        var partial = ManualCornerAnalyzer.Analyze(constantInputs, Corner);
        Assert.AreEqual(CornerEvidence.Partial, partial.Evidence);
        Assert.AreEqual(6.4, partial.Selected!.Seconds, 1e-9);
        Assert.IsNull(partial.Selected.BrakeStartS);
        Assert.IsNull(partial.Selected.ThrottleRecoveryS);
        var gap = lap with { Samples = lap.Samples.Where(sample => sample.S < 150 || sample.S > 200).ToArray() };
        Assert.AreEqual(CornerEvidence.Insufficient, ManualCornerAnalyzer.Analyze(gap, Corner).Evidence);
        Assert.IsNotNull(ManualCornerAnalyzer.Analyze(gap, new("T2", 250, 400)).Selected);
        foreach (var missing in new[] { lap with { Samples = [] }, Lap(step: 50), lap with { TotalSeconds = double.NaN } })
            Assert.IsNull(ManualCornerAnalyzer.Analyze(missing, Corner).Selected);
        Assert.IsNull(ManualCornerAnalyzer.Analyze(lap, new("T", 0, 501)).Selected);
    }

    [TestMethod]
    public void ComparisonReasonsDistinguishMissingRouteVehicleAndChangedConfiguration()
    {
        var lap = Lap();
        static LapSummary Summary(LapRecord value) => LapSummary.FromRecord(value);
        StringAssert.Contains(ManualCornerAnalyzer.ComparisonEligibility(null, LapSummary.FromRecord(lap))!, "缺少保存的赛道信息");
        StringAssert.Contains(ManualCornerAnalyzer.Compatibility(Track(), Summary(lap with { TrackRevision = null }), Summary(Lap()))!, "缺少路线修订信息");
        StringAssert.Contains(ManualCornerAnalyzer.Compatibility(Track(), Summary(lap), Summary(Lap() with { TrackRevision = "old" }))!, "路线修订与当前赛道不一致");
        StringAssert.Contains(ManualCornerAnalyzer.Compatibility(Track(), Summary(lap), Summary(Lap() with { Vehicle = lap.Vehicle with { DrivetrainType = -1 } }))!, "缺少完整车辆信息");
        StringAssert.Contains(ManualCornerAnalyzer.Compatibility(Track(), Summary(lap), Summary(Lap() with { Vehicle = lap.Vehicle with { PerformanceIndex = 900 } }))!, "车辆型号");
    }

    [TestMethod]
    public void DistanceAlignmentIgnoresAbsoluteTimeOffsetAndDifferentSamplingRates()
    {
        var reference = Lap(step: 5);
        var selected = Lap(step: 10, timeOffset: 3);
        var result = ManualCornerAnalyzer.Compare(Track(), selected, reference, Corner);
        Assert.AreEqual(CornerEvidence.Sufficient, result.Evidence);
        Assert.AreEqual(6.4, result.Selected!.Seconds, 1e-9);
        Assert.AreEqual(result.Reference!.Seconds, result.Selected.Seconds, 1e-9);
        Assert.AreEqual(102.5, result.Selected.BrakeStartS!.Value, 1e-9);
        Assert.AreEqual(307, result.Selected.ThrottleRecoveryS!.Value, 1e-9);
        Assert.AreEqual(72, result.Selected.MinimumSpeedKph, 1e-9);
        Assert.IsEmpty(ManualCornerAnalyzer.Describe([result]));
    }

    [TestMethod]
    public void ComputesLocalDurationBrakeMinimumAndThrottleDifferencesWithThreeChineseNotes()
    {
        var result = ManualCornerAnalyzer.Compare(Track(),
            Lap(step: 10, timeScale: 1.1, brakeShift: 10, throttleShift: 20, speedOffset: -2), Lap(), Corner);
        Assert.AreEqual(CornerEvidence.Sufficient, result.Evidence);
        var a = result.Selected!;
        var b = result.Reference!;
        Assert.AreEqual(.64, a.Seconds - b.Seconds, 1e-9);
        Assert.AreEqual(10, a.BrakeStartS!.Value - b.BrakeStartS!.Value, 1e-9);
        Assert.AreEqual(20, a.ThrottleRecoveryS!.Value - b.ThrottleRecoveryS!.Value, 1e-9);
        Assert.AreEqual(-7.2, a.MinimumSpeedKph - b.MinimumSpeedKph, 1e-9);
        var notes = ManualCornerAnalyzer.Describe([result, result with { Corner = Corner with { Name = "T2" } }]);
        Assert.HasCount(3, notes);
        StringAssert.Contains(notes[0].Text, "慢 0.640 秒");
        StringAssert.Contains(notes[0].Text, "不代表因果关系");
        StringAssert.Contains(notes[1].Text, "不能据此断定更快");
        Assert.AreEqual(a.MinimumSpeedS, notes[0].ProgressMeters);
        Assert.AreEqual(a.BrakeStartS, notes[1].ProgressMeters);
        Assert.AreEqual(a.ThrottleRecoveryS, notes[2].ProgressMeters);
        Assert.IsTrue(notes.All(note => note.ProgressMeters >= Corner.StartS && note.ProgressMeters <= Corner.EndS));
    }

    [TestMethod]
    public void InterpolatesIntervalEdgesWithoutExtrapolatingOrUsingWholeLapDelta()
    {
        var selected = Lap(timeScale: 1.1) with { TotalSeconds = 100 };
        var result = ManualCornerAnalyzer.Compare(Track(), selected, Lap(), new("T", 83, 397));
        Assert.AreEqual(314 / 50d * 1.1, result.Selected!.Seconds, 1e-9);
        var missingStart = selected with { Samples = selected.Samples.Where(sample => sample.S > 85).ToArray() };
        AssertNoConclusion(missingStart, Lap());
        var missingEnd = selected with { Samples = selected.Samples.Where(sample => sample.S < 395).ToArray() };
        AssertNoConclusion(missingEnd, Lap());
    }

    [TestMethod]
    public void InsufficientSparseInvalidAndNonMonotonicSamplesProduceNoMetricsOrNotes()
    {
        var lap = Lap();
        AssertNoConclusion(lap with { Samples = lap.Samples.Where(sample => sample.S < 150 || sample.S > 200).ToArray() }, Lap());
        AssertNoConclusion(Lap(step: 50), Lap());
        AssertNoConclusion(lap with { Samples = lap.Samples.Select(sample => sample.S == 150 ? sample with { SpeedMps = double.NaN } : sample).ToArray() }, Lap());
        AssertNoConclusion(lap with { Samples = lap.Samples.Select(sample => sample.S == 150 ? sample with { Accel = 2 } : sample).ToArray() }, Lap());
        AssertNoConclusion(lap with { Samples = lap.Samples.Select(sample => sample.S >= 150 ? sample with { ElapsedSeconds = sample.ElapsedSeconds + 1 } : sample).ToArray() }, Lap());
        var reversed = lap.Samples.ToArray();
        (reversed[30], reversed[31]) = (reversed[31], reversed[30]);
        AssertNoConclusion(lap with { Samples = reversed }, Lap());
        var duplicate = lap.Samples.ToList();
        duplicate.Insert(30, duplicate[30]);
        AssertNoConclusion(lap with { Samples = duplicate }, Lap());
    }

    [TestMethod]
    public void UnknownTransitionsAndBriefInputSpikesRemainPartialInsteadOfInventingPositions()
    {
        var lap = Lap();
        var alreadyBraking = lap with { Samples = lap.Samples.Select(sample => sample with { Brake = 1, Accel = 1 }).ToArray() };
        var result = ManualCornerAnalyzer.Compare(Track(), alreadyBraking, Lap(), Corner);
        Assert.AreEqual(CornerEvidence.Partial, result.Evidence);
        Assert.IsNull(result.Selected!.BrakeStartS);
        Assert.IsNull(result.Selected.ThrottleRecoveryS);
        Assert.IsEmpty(ManualCornerAnalyzer.Describe([result]));
        var spike = lap with { Samples = lap.Samples.Select(sample => sample with
        {
            Brake = sample.S == 150 ? 1 : 0, Accel = sample.S == 310 ? 1 : 0
        }).ToArray() };
        result = ManualCornerAnalyzer.Compare(Track(), spike, Lap(), Corner);
        Assert.IsNull(result.Selected!.BrakeStartS);
        Assert.IsNull(result.Selected.ThrottleRecoveryS);
    }

    [TestMethod]
    public void RejectsSelfInvalidLapsTrackVersionDirectionAndIncompatibleVehicles()
    {
        var lap = Lap();
        var alternatives = new[]
        {
            lap, Lap() with { IsValid = false }, Lap() with { TrackId = Guid.NewGuid() },
            Lap() with { Direction = -1 }, Lap() with { SectorSchemaVersion = 999 },
            Lap() with { TrackRevision = null }, Lap() with { TrackRevision = "older-revision" },
            Lap() with { Vehicle = lap.Vehicle with { CarOrdinal = 456 } },
            Lap() with { Vehicle = lap.Vehicle with { PerformanceIndex = 999 } },
            Lap() with { Vehicle = lap.Vehicle with { DrivetrainType = 1 } },
            Lap() with { Vehicle = lap.Vehicle with { GearSlopeSignature = "g3_100" } },
            Lap() with { Vehicle = lap.Vehicle with { CarOrdinal = 0 } },
            Lap() with { Vehicle = lap.Vehicle with { DrivetrainType = -1, NumCylinders = -1 } }
        };
        foreach (var reference in alternatives)
        {
            var result = ManualCornerAnalyzer.Compare(Track(), lap, reference, Corner);
            Assert.AreEqual(CornerEvidence.Incompatible, result.Evidence, result.Message);
            Assert.IsNull(result.Selected);
            Assert.IsEmpty(ManualCornerAnalyzer.Describe([result]));
        }
    }

    [TestMethod]
    public void UnsupportedIntervalsDoNotCrossFinishOrSilentlyClampToCoverage()
    {
        foreach (var corner in new[] { new ManualCorner("T", -1, 300), new("T", 480, 30),
                     new("T", 0, 501), new("T", 0, 20), new("", 0, 300), new("T", 0, double.NaN) })
        {
            var result = ManualCornerAnalyzer.Compare(Track(), Lap(), Lap(), corner);
            Assert.AreEqual(CornerEvidence.Insufficient, result.Evidence);
            Assert.IsNull(result.Selected);
        }
    }

    [TestMethod]
    public void GeometryRevisionRejectsChangedRouteEvenWhenIdAndTimestampWereReused()
    {
        var original = Track();
        var changed = original with { Points = [new(1, 0, 0, 0, 1, 0)] };
        Assert.AreNotEqual(LapTrackRevision.Create(original), LapTrackRevision.Create(changed));
        var result = ManualCornerAnalyzer.Compare(changed, Lap(), Lap(), Corner);
        Assert.AreEqual(CornerEvidence.Incompatible, result.Evidence);
        Assert.AreEqual(LapTrackRevision.Create(original), LapTrackRevision.Create(original with { Name = "Renamed" }));
    }

    [TestMethod]
    public void EqualTimeDifferentMinimumProducesObservationWithoutClaimingBetterDriving()
    {
        var result = ManualCornerAnalyzer.Compare(Track(), Lap(speedOffset: 2), Lap(), Corner);
        var notes = ManualCornerAnalyzer.Describe([result]);
        Assert.HasCount(1, notes);
        StringAssert.Contains(notes[0].Text, "不据此评价跑法优劣");
    }

    private static void AssertNoConclusion(LapRecord selected, LapRecord reference)
    {
        var result = ManualCornerAnalyzer.Compare(Track(), selected, reference, Corner);
        Assert.AreEqual(CornerEvidence.Insufficient, result.Evidence);
        Assert.IsNull(result.Selected);
        Assert.IsNull(result.Reference);
        Assert.IsEmpty(ManualCornerAnalyzer.Describe([result]));
    }

    private static TrackTemplate Track() => new(TrackId, "test", 1, "fh6_udp_live", null,
        [], 500, 0, 0, 0, 500, 0, 0, 10, 1, 1, Revision, Revision);

    private static LapRecord Lap(int step = 5, double timeOffset = 0, double timeScale = 1,
        double brakeShift = 0, double throttleShift = 0, double speedOffset = 0)
    {
        var samples = new List<LapSample>();
        for (var s = 0; s <= 500; s += step)
        {
            var brake = s < 220 ? Math.Clamp((s - 100 - brakeShift) / 10, 0, 1) : 0;
            var throttle = s < 80 ? 1 : Math.Clamp((s - 300 - throttleShift) / 10, 0, 1);
            samples.Add(new(s, s / 50d * timeScale + timeOffset, 20 + Math.Abs(s - 250) * .1 + speedOffset,
                5000, 3, throttle, brake, 0, s, 0, 0));
        }
        return new(Guid.NewGuid(), TrackId, 1, TrackAlgorithms.SectorSchemaVersion, Guid.NewGuid(),
            new(123, 5, 850, 2, 8, 8000, "g3_200", "p300_t400_r7000"),
            Revision.AddDays(1), 30, true, null, [], samples) { TrackRevision = LapTrackRevision.Create(Track()) };
    }
}
