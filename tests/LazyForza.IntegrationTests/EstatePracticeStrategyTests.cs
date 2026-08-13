using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstatePracticeStrategyTests
{
    [TestMethod]
    public void LongRunArmsAtNextLineAndSavesOnlyTrackAdjustedCompleteCleanLaps()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var targetLaps = EstatePracticeTestManager.CalculateLongRunTargetLaps(context);
        var local = Participant();
        var session = Session(local);
        manager.Start(
            EstatePracticeTestKind.LongRun,
            session,
            local,
            context,
            Vehicle(),
            EstatePitServiceState.Empty);

        for (var lapNumber = 1; lapNumber <= targetLaps + 1; lapNumber++)
        {
            var completed = Lap(lapNumber, 90 + lapNumber * 0.2);
            local = local with { CompletedLaps = lapNumber, LastLapSeconds = completed.LapSeconds };
            session = Session(local) with { Revision = lapNumber };
            context = context with { CompletedLaps = lapNumber, LastCompletedLap = completed };
            manager.Observe(
                session,
                local.Id,
                context,
                EstatePitServiceState.Empty,
                RaceGripCondition.Unknown,
                Vehicle(),
                false);
        }

        Assert.IsNull(manager.Current.ActiveKind);
        Assert.AreEqual(
            EstatePracticeTestStatus.Completed,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.LongRun).Status);
        var sample = manager.DrainSamples().Single();
        Assert.AreEqual(EstateStrategySampleKind.Stint, sample.Kind);
        Assert.AreEqual(EstateStrategySampleSource.PracticeLongRun, sample.Source);
        Assert.AreEqual(targetLaps, sample.LapCount);
        Assert.IsTrue(sample.DegradationPerLapSeconds >= 0);
    }

    [TestMethod]
    public void LongRunTargetUsesReferenceLapAndMakesLazyCircuitTenLaps()
    {
        var context = Context(referenceLapSeconds: 79.375);

        Assert.AreEqual(10, EstatePracticeTestManager.CalculateLongRunTargetLaps(context));
        Assert.AreEqual(12, EstatePracticeTestManager.CalculateLongRunTargetLaps(
            Context(referenceLapSeconds: 50)));
        Assert.AreEqual(5, EstatePracticeTestManager.CalculateLongRunTargetLaps(
            Context(referenceLapSeconds: 180)));
    }

    [TestMethod]
    public void BoundaryIncidentImmediatelyClosesPracticeProjectWithoutSaving()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant();
        var session = Session(local);
        manager.Start(
            EstatePracticeTestKind.LongRun,
            session,
            local,
            context,
            Vehicle(),
            EstatePitServiceState.Empty);

        local = local with { TrackLimitWarnings = 1 };
        manager.Observe(
            Session(local),
            local.Id,
            context,
            EstatePitServiceState.Empty,
            RaceGripCondition.Unknown,
            Vehicle(),
            false);

        Assert.IsNull(manager.Current.ActiveKind);
        Assert.AreEqual(
            EstatePracticeTestStatus.Failed,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.LongRun).Status);
        Assert.HasCount(0, manager.DrainSamples());
    }

    [TestMethod]
    public void BoundaryIncidentAfterCleanLongRunLapsKeepsCompletedPartialStint()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant();
        manager.Start(
            EstatePracticeTestKind.LongRun,
            Session(local),
            local,
            context,
            Vehicle(),
            EstatePitServiceState.Empty);

        for (var lapNumber = 1; lapNumber <= 3; lapNumber++)
        {
            var completed = Lap(lapNumber, 90 + lapNumber * 0.2);
            local = local with { CompletedLaps = lapNumber, LastLapSeconds = completed.LapSeconds };
            context = context with { CompletedLaps = lapNumber, LastCompletedLap = completed };
            manager.Observe(
                Session(local) with { Revision = lapNumber },
                local.Id,
                context,
                EstatePitServiceState.Empty,
                RaceGripCondition.Unknown,
                Vehicle(),
                false);
        }

        local = local with { TrackLimitWarnings = 1 };
        manager.Observe(
            Session(local) with { Revision = 4 },
            local.Id,
            context,
            EstatePitServiceState.Empty,
            RaceGripCondition.Unknown,
            Vehicle(),
            false);

        var item = manager.Current.Items.Single(
            candidate => candidate.Kind == EstatePracticeTestKind.LongRun);
        Assert.AreEqual(EstatePracticeTestStatus.Failed, item.Status);
        StringAssert.Contains(item.LastResult, "已保存终止前的 2 个完整干净圈");
        var sample = manager.DrainSamples().Single();
        Assert.AreEqual(EstateStrategySampleKind.Stint, sample.Kind);
        Assert.AreEqual(EstateStrategySampleSource.PracticeLongRun, sample.Source);
        Assert.AreEqual(2, sample.LapCount);
        Assert.AreEqual(90.5, sample.RepresentativeLapSeconds!.Value, 0.001);
    }

    [TestMethod]
    public void IncompletePitSimulationDoesNotCreateMisleadingPartialPitSample()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant() with { IsInPitLane = true };
        var inPit = EstatePitServiceState.Empty with { IsInPitLane = true, IsOnPitRoute = true };
        manager.Start(
            EstatePracticeTestKind.PitStopSimulation,
            Session(local),
            local,
            context,
            Vehicle(),
            inPit);

        local = local with { IsInPitLane = false };
        manager.Observe(
            Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);
        local = local with { TrackLimitWarnings = 1 };
        manager.Observe(
            Session(local) with { Revision = 2 }, local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var item = manager.Current.Items.Single(
            candidate => candidate.Kind == EstatePracticeTestKind.PitStopSimulation);
        Assert.AreEqual(EstatePracticeTestStatus.Failed, item.Status);
        StringAssert.Contains(item.LastResult, "尚未形成可用于策略计算的完整数据");
        Assert.HasCount(0, manager.DrainSamples());
    }

    [TestMethod]
    public void PitSimulationRequiresOutLapServiceAndCleanExitBeforeSaving()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant() with { IsInPitLane = true };
        var session = Session(local);
        var inPit = EstatePitServiceState.Empty with { IsInPitLane = true, IsOnPitRoute = true };
        manager.Start(
            EstatePracticeTestKind.PitStopSimulation,
            session,
            local,
            context,
            Vehicle(),
            inPit);

        local = local with { IsInPitLane = false };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var outLap = Lap(1, 96);
        local = local with { CompletedLaps = 1, LastLapSeconds = 96 };
        context = context with { CompletedLaps = 1, LastCompletedLap = outLap };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        local = local with { IsInPitLane = true, PitLaneElapsedSeconds = 2 };
        inPit = inPit with { PitLaneElapsedSeconds = 2 };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);

        manager.NotifyDriverIntervention(inPit with { IsInServiceZone = true });
        local = local with { CompletedPitServices = 1, PitLaneElapsedSeconds = 8.4 };
        inPit = inPit with
        {
            CompletedServices = 1,
            PitLaneElapsedSeconds = 8.4,
            RequirementMet = true,
            IsInServiceZone = true
        };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);

        local = local with { IsInPitLane = false, PitLaneElapsedSeconds = 0 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var sample = manager.DrainSamples().Single();
        Assert.AreEqual(EstateStrategySampleKind.PitStop, sample.Kind);
        Assert.IsNotNull(sample.PitLaneElapsedSeconds);
        Assert.AreEqual(8.4, sample.PitLaneElapsedSeconds.Value, 0.01);
        Assert.AreEqual(
            EstatePracticeTestStatus.Completed,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.PitStopSimulation).Status);
    }

    [TestMethod]
    public void PitSimulationAcceptsFinishLineInsideSamePitVisit()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        context = context with
        {
            Definition = context.Definition with
            {
                Pit = context.Definition.Pit! with { StartFinishGate = Gate(50) }
            }
        };
        var local = Participant() with { IsInPitLane = true };
        var inPit = EstatePitServiceState.Empty with { IsInPitLane = true, IsOnPitRoute = true };
        manager.Start(
            EstatePracticeTestKind.PitStopSimulation,
            Session(local),
            local,
            context,
            Vehicle(),
            inPit);

        local = local with { IsInPitLane = false };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        local = local with { IsInPitLane = true, PitLaneElapsedSeconds = 2 };
        inPit = inPit with { PitLaneElapsedSeconds = 2 };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);

        local = local with { CompletedPitServices = 1, PitLaneElapsedSeconds = 7.8 };
        inPit = inPit with
        {
            CompletedServices = 1,
            PitLaneElapsedSeconds = 7.8,
            RequirementMet = true,
            IsInServiceZone = true
        };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);

        var outLap = Lap(1, 94.2);
        local = local with { CompletedLaps = 1, LastLapSeconds = outLap.LapSeconds };
        context = context with { CompletedLaps = 1, LastCompletedLap = outLap };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);

        local = local with { IsInPitLane = false, PitLaneElapsedSeconds = 0 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var sample = manager.DrainSamples().Single();
        Assert.AreEqual(EstateStrategySampleKind.PitStop, sample.Kind);
        Assert.AreEqual(7.8, sample.PitLaneElapsedSeconds!.Value, 0.01);
        Assert.AreEqual(
            EstatePracticeTestStatus.Completed,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.PitStopSimulation).Status);
    }

    [TestMethod]
    public void PitSimulationUsesFullTrackTravelWhenFinishEventIsUnavailableInsidePit()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        context = context with
        {
            Definition = context.Definition with
            {
                Pit = context.Definition.Pit! with { StartFinishGate = Gate(50) }
            }
        };
        var local = Participant() with { IsInPitLane = true, TrackProgress = .225 };
        var inPit = EstatePitServiceState.Empty with { IsInPitLane = true, IsOnPitRoute = true };
        manager.Start(EstatePracticeTestKind.PitStopSimulation, Session(local), local,
            context, Vehicle(), inPit);

        local = local with { IsInPitLane = false, TrackProgress = .23 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);
        foreach (var progress in new[] { .45, .68, .90 })
        {
            local = local with { TrackProgress = progress };
            manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
                RaceGripCondition.Unknown, Vehicle(), false);
        }

        local = local with
        {
            IsInPitLane = true,
            TrackProgress = .02,
            PitLaneElapsedSeconds = 8.4,
            CompletedPitServices = 1,
            PitServiceRequirementMet = true
        };
        inPit = inPit with
        {
            PitLaneElapsedSeconds = 8.4,
            CompletedServices = 1,
            RequirementMet = true,
            IsInServiceZone = true
        };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);

        local = local with { IsInPitLane = false, PitLaneElapsedSeconds = 0 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var sample = manager.DrainSamples().Single();
        Assert.AreEqual(EstateStrategySampleKind.PitStop, sample.Kind);
        Assert.AreEqual(8.4, sample.PitLaneElapsedSeconds!.Value, 0.01);
        Assert.AreEqual(EstatePracticeTestStatus.Completed,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.PitStopSimulation).Status);
    }

    [TestMethod]
    public void PitSimulationRejectsImmediateShortcutBackToPitWithoutFullTrackTravel()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant() with { IsInPitLane = true, TrackProgress = .225 };
        var inPit = EstatePitServiceState.Empty with { IsInPitLane = true, IsOnPitRoute = true };
        manager.Start(EstatePracticeTestKind.PitStopSimulation, Session(local), local,
            context, Vehicle(), inPit);

        local = local with { IsInPitLane = false, TrackProgress = .23 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);
        local = local with { TrackProgress = .35 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);
        local = local with
        {
            IsInPitLane = true,
            CompletedPitServices = 1,
            PitServiceRequirementMet = true,
            PitLaneElapsedSeconds = 7
        };
        inPit = inPit with
        {
            CompletedServices = 1,
            RequirementMet = true,
            PitLaneElapsedSeconds = 7
        };
        manager.Observe(Session(local), local.Id, context, inPit,
            RaceGripCondition.Unknown, Vehicle(), false);
        local = local with { IsInPitLane = false };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        Assert.HasCount(0, manager.DrainSamples());
        var item = manager.Current.Items.Single(candidate =>
            candidate.Kind == EstatePracticeTestKind.PitStopSimulation);
        Assert.AreEqual(EstatePracticeTestStatus.Failed, item.Status);
        StringAssert.Contains(item.LastResult, "没有完成一整圈");
    }

    [TestMethod]
    public void LongTerminalPracticeGuidanceExtendsHudUntilMarqueeCanFinish()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant();
        manager.Start(EstatePracticeTestKind.PitStopSimulation, Session(local), local,
            context, Vehicle(), EstatePitServiceState.Empty);

        local = local with { TrackLimitWarnings = 1 };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var item = manager.Current.Items.Single(candidate =>
            candidate.Kind == EstatePracticeTestKind.PitStopSimulation);
        Assert.IsNotNull(item.HudVisibleFrom);
        Assert.IsNotNull(item.HudVisibleUntil);
        Assert.IsTrue(
            item.HudVisibleUntil!.Value - item.HudVisibleFrom!.Value > TimeSpan.FromSeconds(5),
            "长文本应为完整滚动预留超过最低五秒的显示时间。 ");
    }

    [TestMethod]
    public void QualifyingSimulationUsesPreparationLapThenSavesOneFlyingLap()
    {
        var manager = new EstatePracticeTestManager();
        var context = Context();
        var local = Participant();
        manager.Start(
            EstatePracticeTestKind.QualifyingSimulation,
            Session(local),
            local,
            context,
            Vehicle(),
            EstatePitServiceState.Empty);

        var preparation = Lap(1, 98);
        local = local with { CompletedLaps = 1, LastLapSeconds = 98 };
        context = context with { CompletedLaps = 1, LastCompletedLap = preparation };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);
        Assert.AreEqual(
            EstatePracticeTestStatus.Active,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.QualifyingSimulation).Status);

        var flying = Lap(2, 89.321);
        local = local with { CompletedLaps = 2, LastLapSeconds = flying.LapSeconds };
        context = context with { CompletedLaps = 2, LastCompletedLap = flying };
        manager.Observe(Session(local), local.Id, context, EstatePitServiceState.Empty,
            RaceGripCondition.Unknown, Vehicle(), false);

        var sample = manager.DrainSamples().Single();
        Assert.AreEqual(EstateStrategySampleKind.FlyingLap, sample.Kind);
        Assert.AreEqual(89.321, sample.RepresentativeLapSeconds!.Value, 0.001);
        Assert.AreEqual(
            EstatePracticeTestStatus.Completed,
            manager.Current.Items.Single(item => item.Kind == EstatePracticeTestKind.QualifyingSimulation).Status);
    }

    [TestMethod]
    public void MatchingStopsAtBestTierOnceEvidenceIsSufficient()
    {
        var current = Vehicle();
        var track = new EstateStrategyTrackIdentity("track", "1", "hash");
        EstateStrategySample Stint(Guid id, VehicleProfileFingerprint vehicle, int laps) => new(
            id, track, EstateStrategySampleKind.Stint, EstateStrategySampleSource.Race,
            DateTimeOffset.UtcNow, vehicle, laps, 90, 91, 0.3, 0.2, null);
        var exact = Stint(Guid.NewGuid(), current, 8);
        var sameCar = Stint(Guid.NewGuid(), current with
        {
            GearSlopeSignature = "g2_300-g3_210",
            CurveSignature = "p99_t70_r8000"
        }, 8);
        var samePi = Stint(Guid.NewGuid(), current with { CarOrdinal = 999 }, 8);

        var selected = EstateStrategySampleMatcher.Select(
            [samePi, sameCar, exact], current, EstateStrategySampleKind.Stint, 8);

        Assert.HasCount(1, selected);
        Assert.AreEqual(exact.Id, selected[0].Sample.Id);
        Assert.AreEqual(EstateStrategyMatchTier.SameCarAndTune, selected[0].Tier);
    }

    [TestMethod]
    public void HistoricalStintAndPitSamplesCanSeedRacePredictionBeforeThreeCurrentLaps()
    {
        var context = Context();
        var track = new EstateStrategyTrackIdentity(
            context.Definition.TrackId.ToString("D"), "1", "TEST-TRACK-HASH");
        var predictor = new EstatePitStrategyPredictor();
        predictor.SetHistoricalSamples(
        [
            new EstateStrategySample(
                Guid.NewGuid(), track, EstateStrategySampleKind.Stint,
                EstateStrategySampleSource.PracticeLongRun, DateTimeOffset.UtcNow,
                Vehicle(), 6, 89.5, 91.5, 0.45, 0.3, null),
            new EstateStrategySample(
                Guid.NewGuid(), track, EstateStrategySampleKind.PitStop,
                EstateStrategySampleSource.PracticePitSimulation, DateTimeOffset.UtcNow,
                Vehicle(), 0, null, null, null, null, 30)
        ]);
        var local = Participant();
        var race = Session(local) with
        {
            Phase = RaceSessionPhase.Race,
            TotalRaceLaps = 20,
            TrackId = context.Definition.TrackId.ToString("D")
        };

        var prediction = predictor.Observe(
            race, local.Id, context, RaceGripCondition.Unknown, false, Vehicle());

        Assert.AreNotEqual(EstatePitStrategyDecision.Collecting, prediction.Decision);
        Assert.IsTrue(prediction.UsesHistoricalPace);
        Assert.AreEqual(EstatePitLossSource.Historical, prediction.PitLossSource);
        Assert.AreEqual(2, prediction.HistoricalSampleCount);
        Assert.IsNotNull(prediction.RepresentativeLapSeconds);
    }

    [TestMethod]
    public void CrossClassHistoryContributesRelativeTrendButNeverForeignAbsolutePace()
    {
        var context = Context();
        var track = new EstateStrategyTrackIdentity(
            context.Definition.TrackId.ToString("D"), "1", "TEST-TRACK-HASH");
        var predictor = new EstatePitStrategyPredictor();
        var foreignVehicle = Vehicle() with
        {
            CarOrdinal = 999,
            CarClass = 5,
            PerformanceIndex = 820
        };
        predictor.SetHistoricalSamples(
        [
            new EstateStrategySample(
                Guid.NewGuid(), track, EstateStrategySampleKind.Stint,
                EstateStrategySampleSource.Race, DateTimeOffset.UtcNow,
                foreignVehicle, 8, 198, 200, 2, 1, null)
        ]);
        var local = Participant();
        var race = Session(local) with
        {
            Phase = RaceSessionPhase.Race,
            TotalRaceLaps = 20,
            TrackId = context.Definition.TrackId.ToString("D")
        };

        var initial = predictor.Observe(
            race, local.Id, context, RaceGripCondition.Unknown, false, Vehicle());
        Assert.AreEqual(EstatePitStrategyDecision.Collecting, initial.Decision);
        Assert.IsFalse(initial.UsesHistoricalPace,
            "其他车型的绝对圈速不能在本机尚无圈速时直接成为代表配速。 ");

        for (var lapNumber = 1; lapNumber <= 4; lapNumber++)
        {
            var seconds = lapNumber == 1 ? 96 : 90 + (lapNumber - 2) * 0.2;
            local = local with
            {
                CompletedLaps = lapNumber,
                LastLapSeconds = seconds,
                BestLapSeconds = Math.Min(local.BestLapSeconds ?? seconds, seconds)
            };
            race = race with { Revision = lapNumber + 1, Participants = [local] };
            _ = predictor.Observe(
                race, local.Id, context, RaceGripCondition.Unknown, false, Vehicle());
        }

        Assert.IsNotNull(predictor.Current.RepresentativeLapSeconds);
        Assert.IsTrue(predictor.Current.RepresentativeLapSeconds < 100);
        Assert.IsNotNull(predictor.Current.DegradationPerLapSeconds);
        Assert.IsTrue(predictor.Current.DegradationPerLapSeconds < 1.2,
            "跨等级样本应按相对衰退比例缩放，不能直接套用对方每圈秒数。 ");
    }

    [TestMethod]
    public void PracticeHudIsVisibleOnlyWhileDriverHasAnActivePracticeProject()
    {
        var local = Participant();
        var active = new EstatePracticeTestPanelState(
            true,
            EstatePracticeTestKind.LongRun,
            [new EstatePracticeTestItemState(
                EstatePracticeTestKind.LongRun,
                "长距离轮胎管理",
                "desc",
                EstatePracticeTestStatus.Active,
                "保持正常比赛节奏。",
                2,
                6)]);
        var state = new EstateRaceHudState(
            DateTimeOffset.UtcNow,
            EstateRaceConnectionState.Connected,
            "connected",
            local.Id,
            Session(local),
            [],
            RaceGripCondition.Unknown,
            string.Empty,
            EstatePitServiceState.Empty,
            PracticeTests: active);

        Assert.IsTrue(EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(state));
        Assert.IsFalse(EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(
            state with { IsObserver = true }));
        Assert.IsFalse(EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(
            state with { PracticeTests = active with { ActiveKind = null, Items = [] } }));
        Assert.IsFalse(EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(
            state with { Session = Session(local) with { Phase = RaceSessionPhase.Race } }));
    }

    [TestMethod]
    public void PracticeHudKeepsSuccessAndFailureVisibleForTheirTerminalWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var local = Participant();
        var terminal = new EstatePracticeTestPanelState(
            true,
            null,
            [new EstatePracticeTestItemState(
                EstatePracticeTestKind.PitStopSimulation,
                "进站换胎模拟",
                "desc",
                EstatePracticeTestStatus.Failed,
                "项目失败：维修区超速。请返回维修区。",
                0,
                6,
                "维修区超速",
                now.AddSeconds(5))]);
        var state = new EstateRaceHudState(
            now,
            EstateRaceConnectionState.Connected,
            "connected",
            local.Id,
            Session(local),
            [],
            RaceGripCondition.Unknown,
            string.Empty,
            EstatePitServiceState.Empty,
            PracticeTests: terminal);

        Assert.IsTrue(EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(state, now.AddSeconds(4.9)));
        Assert.IsFalse(EstateRaceHudVisibilityPolicy.ShouldShowPracticeProgram(state, now.AddSeconds(5.1)));
    }

    private static EstateCompletedLapEvent Lap(int number, double seconds) => new(
        Guid.NewGuid(), number, seconds, [], true, null, true);

    private static EstateRaceSession Session(EstateRaceParticipant local) => new(
        1,
        "练习测试",
        RaceSessionPhase.Practice,
        RaceControlFlag.Green,
        null,
        local.Id.ToString("D"),
        "1",
        "TEST-TRACK-HASH",
        20,
        null,
        null,
        null,
        null,
        [],
        null,
        [local],
        DateTimeOffset.UtcNow);

    private static EstateRaceParticipant Participant() => new(
        Guid.NewGuid(), 1, "测试车手", "#42D7E8", null,
        RaceParticipantStatus.OnTrack, true, false,
        0, 0, 0, 0.5, 0.5, 100, 0,
        null, null, null, null,
        false, false, 0, false, 0,
        RaceGripCondition.Unknown, [], [], DateTimeOffset.UtcNow);

    private static VehicleProfileFingerprint Vehicle() => new(
        2038, 4, 800, 2, 8, 8_500,
        "g2_250-g3_180",
        "p70_t55_r7200");

    private static EstateRaceTrackContext Context(double referenceLapSeconds = 90)
    {
        var track = TrackAlgorithms.BuildTemplate("练习策略环道",
        [
            new TrackPoint(0, 0, 0, 0, 0, 0),
            new TrackPoint(100, 0, 0, 0, 0, 0),
            new TrackPoint(100, 0, 100, 0, 0, 0),
            new TrackPoint(0, 0, 100, 0, 0, 0),
            new TrackPoint(0, 0, 0, 0, 0, 0)
        ]);
        var pit = new EstatePitDefinition(
            Gate(10), Gate(90),
            [new EstateGatePoint(10, 0, 0), new EstateGatePoint(50, 0, -120), new EstateGatePoint(90, 0, 0)],
            new EstateGatePoint(50, 0, -120),
            5, 40, 5, 4);
        var definition = new EstateTrackDefinition(
            track.Id, track.Name, "test", null, "1", Gate(0),
            EstateTrackAlgorithms.CreateCheckpoints(track, 3), pit,
            referenceLapSeconds, referenceLapSeconds, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return new EstateRaceTrackContext(
            track, definition, 0, 0, 0, true, null, 3, "TEST-TRACK-HASH");
    }

    private static EstateTimingGate Gate(double x) => new(
        new EstateGatePoint(x, 0, -4),
        new EstateGatePoint(x, 0, 4),
        1, 0, 0, 0, 0);
}
