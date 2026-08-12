using LazyForza.Analysis;
using LazyForza.Domain;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstatePitStrategyPredictorTests
{
    [TestMethod]
    public void ExcludesBoundaryIncidentsAndUnmarkedExtremeSlowLapsFromPaceTrend()
    {
        var predictor = new EstatePitStrategyPredictor();
        var context = Context();
        var localId = Guid.NewGuid();
        var participant = Participant(localId);
        _ = predictor.Observe(Session(participant, 0), localId, context, RaceGripCondition.Unknown, false);

        participant = Complete(participant, 1, 96.0, 0); // 发车圈
        _ = predictor.Observe(Session(participant, 1), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 2, 90.0, 0);
        _ = predictor.Observe(Session(participant, 2), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 3, 90.4, 0);
        _ = predictor.Observe(Session(participant, 3), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 4, 90.8, 0);
        _ = predictor.Observe(Session(participant, 4), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 5, 142.0, 0); // 没有标记，但显然不是代表配速
        _ = predictor.Observe(Session(participant, 5), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 6, 133.0, 1); // 明确发生赛道边界事件
        var prediction = predictor.Observe(
            Session(participant, 6), localId, context, RaceGripCondition.Unknown, false);

        Assert.AreEqual(3, prediction.CleanLapCount);
        Assert.AreEqual(1, prediction.AnomalousLapCount,
            "未标记的失控超慢圈也必须由稳健离群检测排除。 ");
        Assert.AreEqual(1, prediction.BoundaryIncidentLapCount);
        Assert.IsNotNull(prediction.RepresentativeLapSeconds);
        Assert.AreEqual(90.4, prediction.RepresentativeLapSeconds.Value, 0.01);
        Assert.IsNotNull(prediction.DegradationPerLapSeconds);
        Assert.IsTrue(prediction.DegradationPerLapSeconds.Value < 1,
            "失控损时不能被拟合成夸张的轮胎衰退。 ");
    }

    [TestMethod]
    public void UsesCompletedObservedPitVisitBeforeConfiguredGeometryEstimate()
    {
        var predictor = new EstatePitStrategyPredictor();
        var context = Context();
        var localId = Guid.NewGuid();
        var participant = Participant(localId);
        var practice = Session(participant, 0) with { Phase = RaceSessionPhase.Practice };
        _ = predictor.Observe(practice, localId, context, RaceGripCondition.Unknown, false);
        participant = participant with { IsInPitLane = true, PitLaneElapsedSeconds = 5 };
        _ = predictor.Observe(practice with { Revision = 2, Participants = [participant] },
            localId, context, RaceGripCondition.Unknown, false);
        participant = participant with
        {
            PitLaneElapsedSeconds = 31.5,
            CompletedPitServices = 1,
            PitServiceRequirementMet = true
        };
        _ = predictor.Observe(practice with { Revision = 3, Participants = [participant] },
            localId, context, RaceGripCondition.Unknown, false);
        participant = participant with { IsInPitLane = false, PitServiceRequirementMet = false };
        _ = predictor.Observe(practice with { Revision = 4, Participants = [participant] },
            localId, context, RaceGripCondition.Unknown, false);

        participant = Participant(localId);
        _ = predictor.Observe(Session(participant, 10), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 1, 96, 0);
        _ = predictor.Observe(Session(participant, 11), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 2, 90, 0);
        _ = predictor.Observe(Session(participant, 12), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 3, 90.3, 0);
        _ = predictor.Observe(Session(participant, 13), localId, context, RaceGripCondition.Unknown, false);
        participant = Complete(participant, 4, 90.6, 0);
        var prediction = predictor.Observe(
            Session(participant, 14), localId, context, RaceGripCondition.Unknown, false);

        Assert.IsTrue(prediction.UsesObservedPitLoss);
        Assert.AreEqual(1, prediction.ObservedPitStopCount);
        Assert.IsNotNull(prediction.EstimatedPitLossSeconds);
        Assert.IsTrue(prediction.EstimatedPitLossSeconds > 5);
    }

    [TestMethod]
    public void ObserverNeverReceivesDriverStrategyInstruction()
    {
        var predictor = new EstatePitStrategyPredictor();
        var localId = Guid.NewGuid();

        var prediction = predictor.Observe(
            Session(Participant(localId), 0), localId, Context(), RaceGripCondition.AtLimit, true);

        Assert.AreEqual(EstatePitStrategyDecision.Unavailable, prediction.Decision);
        StringAssert.Contains(prediction.Summary, "OB");
    }

    [TestMethod]
    public void RecommendsAStopOnlyWhenProjectedGainExceedsUncertaintyMargin()
    {
        var predictor = new EstatePitStrategyPredictor();
        var context = Context();
        var localId = Guid.NewGuid();
        var participant = Participant(localId);
        _ = predictor.Observe(Session(participant, 0), localId, context, RaceGripCondition.Unknown, false);
        foreach (var (lap, seconds) in new[]
                 {
                     (1, 97d),
                     (2, 90d),
                     (3, 93d),
                     (4, 96d),
                     (5, 99d)
                 })
        {
            participant = Complete(participant, lap, seconds, 0);
            _ = predictor.Observe(Session(participant, lap), localId, context, RaceGripCondition.Unknown, false);
        }

        var prediction = predictor.Current;
        Assert.IsTrue(prediction.Decision is EstatePitStrategyDecision.PitThisLap or
            EstatePitStrategyDecision.PitWindow);
        Assert.IsNotNull(prediction.ProjectedAdvantageSeconds);
        Assert.IsTrue(prediction.ProjectedAdvantageSeconds.Value > 0);
        Assert.IsNotNull(prediction.PitWindowStartLap);
    }

    [TestMethod]
    public void RedFlagSuspensionDoesNotDiscardEstablishedRacePace()
    {
        var predictor = new EstatePitStrategyPredictor();
        var context = Context();
        var localId = Guid.NewGuid();
        var participant = Participant(localId);
        _ = predictor.Observe(Session(participant, 0), localId, context, RaceGripCondition.Unknown, false);
        foreach (var (lap, seconds) in new[] { (1, 96d), (2, 90d), (3, 90.2d), (4, 90.4d) })
        {
            participant = Complete(participant, lap, seconds, 0);
            _ = predictor.Observe(Session(participant, lap), localId, context, RaceGripCondition.Unknown, false);
        }
        Assert.IsNotNull(predictor.Current.RepresentativeLapSeconds);

        var suspended = Session(participant, 10) with
        {
            Phase = RaceSessionPhase.Suspended,
            SuspendedFromPhase = RaceSessionPhase.Race,
            Flag = RaceControlFlag.Red
        };
        var paused = predictor.Observe(suspended, localId, context, RaceGripCondition.Unknown, false);
        Assert.AreEqual(EstatePitStrategyDecision.Unavailable, paused.Decision);

        var resumed = predictor.Observe(
            Session(participant, 11), localId, context, RaceGripCondition.Unknown, false);
        Assert.IsNotNull(resumed.RepresentativeLapSeconds,
            "红旗恢复后应沿用暂停前的干净圈，而不是重新收集三圈。 ");
        Assert.AreNotEqual(EstatePitStrategyDecision.Collecting, resumed.Decision);
    }

    [TestMethod]
    public void RequiredPitStopsBecomeAHardStrategyConstraintNearTheFinish()
    {
        var predictor = new EstatePitStrategyPredictor();
        var localId = Guid.NewGuid();
        var participant = Participant(localId) with { CompletedLaps = 4 };
        var session = Session(participant, 1) with
        {
            TotalRaceLaps = 5,
            MinimumRequiredPitStops = 1
        };

        var prediction = predictor.Observe(
            session,
            localId,
            Context(),
            RaceGripCondition.Unknown,
            false);

        Assert.AreEqual(EstatePitStrategyDecision.PitThisLap, prediction.Decision);
        Assert.AreEqual(1, prediction.MinimumRequiredPitStops);
        Assert.AreEqual(1, prediction.RemainingRequiredPitStops);
        StringAssert.Contains(prediction.Summary, "有效维修停留");
    }

    private static EstateRaceParticipant Complete(
        EstateRaceParticipant participant,
        int completedLaps,
        double lapSeconds,
        int warnings) => participant with
    {
        CompletedLaps = completedLaps,
        LastLapSeconds = lapSeconds,
        BestLapSeconds = participant.BestLapSeconds is double best
            ? Math.Min(best, lapSeconds)
            : lapSeconds,
        TrackLimitWarnings = warnings,
        CurrentLapSeconds = 0
    };

    private static EstateRaceSession Session(EstateRaceParticipant participant, long revision) => new(
        revision,
        "策略预测测试",
        RaceSessionPhase.Race,
        RaceControlFlag.Green,
        null,
        participant.Id.ToString("D"),
        "1",
        "TEST-TRACK-HASH",
        20,
        DateTimeOffset.UnixEpoch,
        null,
        participant.Id,
        participant.BestLapSeconds,
        [],
        null,
        [participant],
        DateTimeOffset.UnixEpoch.AddSeconds(revision))
    {
        MinimumRequiredPitStops = 0
    };

    private static EstateRaceParticipant Participant(Guid id) => new(
        id,
        1,
        "策略车手",
        "#42D7E8",
        null,
        RaceParticipantStatus.OnTrack,
        true,
        false,
        0,
        0,
        0.2,
        0.5,
        0.5,
        120,
        30,
        null,
        null,
        null,
        null,
        false,
        false,
        0,
        false,
        0,
        RaceGripCondition.Unknown,
        [],
        [],
        DateTimeOffset.UnixEpoch);

    private static EstateRaceTrackContext Context()
    {
        var track = TrackAlgorithms.BuildTemplate("策略环道",
        [
            new TrackPoint(0, 0, 0, 0, 0, 0),
            new TrackPoint(100, 0, 0, 0, 0, 0),
            new TrackPoint(100, 0, 100, 0, 0, 0),
            new TrackPoint(0, 0, 100, 0, 0, 0),
            new TrackPoint(0, 0, 0, 0, 0, 0)
        ]);
        var entry = Gate(10);
        var exit = Gate(90);
        var pit = new EstatePitDefinition(
            entry,
            exit,
            [
                new EstateGatePoint(10, 0, 0),
                new EstateGatePoint(50, 0, -150),
                new EstateGatePoint(90, 0, 0)
            ],
            new EstateGatePoint(50, 0, -150),
            5,
            40,
            5,
            4);
        var definition = new EstateTrackDefinition(
            track.Id,
            track.Name,
            "test",
            null,
            "1",
            Gate(0),
            EstateTrackAlgorithms.CreateCheckpoints(track, 3),
            pit,
            90,
            90,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return new EstateRaceTrackContext(track, definition, 0, 0, 0, true, null, 3, "TEST-TRACK-HASH");
    }

    private static EstateTimingGate Gate(double x) => new(
        new EstateGatePoint(x, 0, -4),
        new EstateGatePoint(x, 0, 4),
        1,
        0,
        0,
        0,
        0);
}
