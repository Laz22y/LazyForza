using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstateFullRaceStrategyHudTests
{
    [TestMethod]
    public void FullRaceStrategyAppearsTwentySecondsAfterFormationLapStartsAndHoldsEightSeconds()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var startsAt = DateTimeOffset.UtcNow;
        var participant = Participant();
        var session = Session(participant, startsAt) with
        {
            Phase = RaceSessionPhase.FormationLap,
            StartsAt = null,
            Banner = new EstateRaceBanner(
                Guid.NewGuid(),
                RaceBannerKind.Information,
                "暖胎圈",
                "保持队列",
                null,
                startsAt,
                startsAt.AddSeconds(7))
        };

        Assert.IsFalse(runtime.Update(
            session, participant.Id, Prediction(), startsAt.AddSeconds(19.999)).IsVisible);
        Assert.IsTrue(runtime.Update(
            session, participant.Id, Prediction(), startsAt.AddSeconds(20)).IsVisible);
        Assert.IsTrue(runtime.Update(
            session, participant.Id, Prediction(), startsAt.AddSeconds(27.999)).IsVisible);
        Assert.IsFalse(runtime.Update(
            session, participant.Id, Prediction(), startsAt.AddSeconds(28)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyFinishesItsHoldWhenStartProcedureBegins()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var startsAt = DateTimeOffset.UtcNow;
        var participant = Participant();
        var formation = Session(participant, startsAt) with
        {
            Phase = RaceSessionPhase.FormationLap,
            StartsAt = null,
            Banner = new EstateRaceBanner(
                Guid.NewGuid(), RaceBannerKind.Information, "暖胎圈", null,
                null, startsAt, startsAt.AddSeconds(7))
        };
        _ = runtime.Update(formation, participant.Id, Prediction(), startsAt);

        var countdown = formation with
        {
            Phase = RaceSessionPhase.Countdown,
            Banner = null,
            StartSequenceAt = startsAt.AddSeconds(25)
        };

        Assert.IsTrue(runtime.Update(
            countdown, participant.Id, Prediction(), startsAt.AddSeconds(22)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategySurvivesShortFormationLapAndAppearsDuringRace()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var startsAt = DateTimeOffset.UtcNow;
        var participant = Participant();
        var formation = Session(participant, startsAt) with
        {
            Phase = RaceSessionPhase.FormationLap,
            StartsAt = null,
            Banner = new EstateRaceBanner(
                Guid.NewGuid(), RaceBannerKind.Information, "暖胎圈", null,
                null, startsAt, startsAt.AddSeconds(7))
        };
        _ = runtime.Update(formation, participant.Id, Prediction(), startsAt);

        var race = formation with
        {
            Phase = RaceSessionPhase.Race,
            StartsAt = startsAt.AddSeconds(12),
            Banner = null
        };

        Assert.IsTrue(runtime.Update(
            race, participant.Id, Prediction(), startsAt.AddSeconds(20)).IsVisible);
        Assert.IsFalse(runtime.Update(
            race, participant.Id, Prediction(), startsAt.AddSeconds(28)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyKeepsFormationClockWhileLocalRowIsTemporarilyDisconnected()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var startsAt = DateTimeOffset.UtcNow;
        var disconnected = Participant() with
        {
            Status = RaceParticipantStatus.Disconnected,
            IsConnected = false
        };
        var formation = Session(disconnected, startsAt) with
        {
            Phase = RaceSessionPhase.FormationLap,
            StartsAt = null,
            Banner = new EstateRaceBanner(
                Guid.NewGuid(), RaceBannerKind.Information, "暖胎圈", null,
                null, startsAt, startsAt.AddSeconds(7))
        };

        Assert.IsFalse(runtime.Update(
            formation, disconnected.Id, Prediction(), startsAt).IsVisible);

        var restored = disconnected with
        {
            Status = RaceParticipantStatus.OnTrack,
            IsConnected = true
        };
        Assert.IsTrue(runtime.Update(
            formation with { Participants = [restored], Banner = null },
            restored.Id,
            Prediction(),
            startsAt.AddSeconds(21)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyUsesMonotonicElapsedTimeAcrossServerClockResync()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var serverStart = DateTimeOffset.UtcNow;
        var participant = Participant();
        var formation = Session(participant, serverStart) with
        {
            Phase = RaceSessionPhase.FormationLap,
            Banner = new EstateRaceBanner(
                Guid.NewGuid(), RaceBannerKind.Information, "暖胎圈", null,
                null, serverStart, serverStart.AddSeconds(7))
        };

        Assert.IsFalse(runtime.Update(
            formation, participant.Id, Prediction(), serverStart, 100).IsVisible);
        Assert.IsFalse(runtime.Update(
            formation, participant.Id, Prediction(), serverStart.AddHours(2), 119.999).IsVisible);
        Assert.IsTrue(runtime.Update(
            formation, participant.Id, Prediction(), serverStart.AddHours(-2), 120).IsVisible);
        Assert.IsFalse(runtime.Update(
            formation, participant.Id, Prediction(), serverStart.AddHours(4), 128).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyUsesFirstObservedFormationTimeWhenBannerAlreadyExpired()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var observedAt = DateTimeOffset.UtcNow;
        var participant = Participant();
        var formation = Session(participant, observedAt) with
        {
            Phase = RaceSessionPhase.FormationLap,
            StartsAt = null,
            Banner = null
        };

        Assert.IsFalse(runtime.Update(
            formation, participant.Id, null, observedAt).IsVisible);
        Assert.IsFalse(runtime.Update(
            formation, participant.Id, null, observedAt.AddSeconds(19.999)).IsVisible);
        Assert.IsTrue(runtime.Update(
            formation, participant.Id, null, observedAt.AddSeconds(20)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyDoesNotAppearWhenClientMissedFormationLap()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var startsAt = DateTimeOffset.UtcNow;
        var participant = Participant();
        var race = Session(participant, startsAt) with { Phase = RaceSessionPhase.Race };

        Assert.IsFalse(runtime.Update(
            race, participant.Id, Prediction(), startsAt.AddSeconds(20)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyDoesNotShowForObserverOrFinishedDriver()
    {
        var runtime = new EstateFullRaceStrategyHudRuntime();
        var startsAt = DateTimeOffset.UtcNow;
        var participant = Participant();
        var formation = Session(participant, startsAt) with
        {
            Phase = RaceSessionPhase.FormationLap,
            Banner = new EstateRaceBanner(
                Guid.NewGuid(), RaceBannerKind.Information, "暖胎圈", null,
                null, startsAt, startsAt.AddSeconds(7))
        };

        Assert.IsFalse(runtime.Update(
            formation, null, Prediction(), startsAt.AddSeconds(20)).IsVisible);
        var finished = participant with { Status = RaceParticipantStatus.Finished };
        Assert.IsFalse(runtime.Update(
            formation with { Participants = [finished] }, finished.Id,
            Prediction(), startsAt.AddSeconds(20)).IsVisible);
    }

    [TestMethod]
    public void FullRaceStrategyUsesLiveWindowAndPreservesAllTimelineLaps()
    {
        var participant = Participant();
        var session = Session(participant, DateTimeOffset.UtcNow);

        var snapshot = EstateFullRaceStrategyHudRuntime.CreateSnapshot(
            session,
            participant,
            Prediction());

        Assert.IsTrue(snapshot.IsVisible);
        Assert.AreEqual(20, snapshot.TotalLaps);
        Assert.HasCount(1, snapshot.StopWindows);
        Assert.AreEqual(7, snapshot.StopWindows[0].StartLap);
        Assert.AreEqual(9, snapshot.StopWindows[0].EndLap);
        Assert.AreEqual(8, snapshot.StopWindows[0].TargetLap);
        Assert.HasCount(2, snapshot.Stints);
        Assert.AreEqual((1, 8), (snapshot.Stints[0].StartLap, snapshot.Stints[0].EndLap));
        Assert.AreEqual((9, 20), (snapshot.Stints[1].StartLap, snapshot.Stints[1].EndLap));
        Assert.AreEqual(22.4, snapshot.EstimatedPitLossSeconds!.Value, 0.001);
        Assert.AreEqual(4.8, snapshot.ProjectedAdvantageSeconds!.Value, 0.001);
        Assert.AreEqual(EstatePitStrategyConfidence.Medium, snapshot.Confidence);
        Assert.IsTrue(snapshot.HasHistoricalEvidence);
    }

    [TestMethod]
    public void FullRaceStrategyBuildsOrderedFallbackForMultipleRequiredStops()
    {
        var participant = Participant();
        var session = Session(participant, DateTimeOffset.UtcNow) with
        {
            TotalRaceLaps = 24,
            MinimumRequiredPitStops = 2
        };
        var collecting = Prediction() with
        {
            Decision = EstatePitStrategyDecision.Collecting,
            PitWindowStartLap = null,
            PitWindowEndLap = null,
            MinimumRequiredPitStops = 2,
            RemainingRequiredPitStops = 2
        };

        var snapshot = EstateFullRaceStrategyHudRuntime.CreateSnapshot(
            session,
            participant,
            collecting);

        Assert.HasCount(2, snapshot.StopWindows);
        Assert.HasCount(3, snapshot.Stints);
        Assert.IsTrue(snapshot.StopWindows[0].TargetLap < snapshot.StopWindows[1].TargetLap);
        Assert.AreEqual(1, snapshot.Stints[0].StartLap);
        Assert.AreEqual(24, snapshot.Stints[^1].EndLap);
        Assert.AreEqual(EstatePitStrategyConfidence.Low, snapshot.Confidence);
        Assert.IsFalse(snapshot.HasLiveEvidence);
    }

    [TestMethod]
    public void FullRaceStrategyRendersHonestBaselineWhenPredictionIsUnavailable()
    {
        var participant = Participant();
        var snapshot = EstateFullRaceStrategyHudRuntime.CreateSnapshot(
            Session(participant, DateTimeOffset.UtcNow), participant, null);

        Assert.IsTrue(snapshot.IsVisible);
        Assert.HasCount(1, snapshot.StopWindows);
        Assert.IsNull(snapshot.EstimatedPitLossSeconds);
        Assert.IsNull(snapshot.ProjectedAdvantageSeconds);
        Assert.IsFalse(snapshot.HasLiveEvidence);
        Assert.IsFalse(snapshot.HasHistoricalEvidence);
        Assert.AreEqual(EstatePitStrategyConfidence.Low, snapshot.Confidence);
    }

    [TestMethod]
    public void FullRaceStrategySupportsOneLapRaceWithNoRequiredStop()
    {
        var participant = Participant();
        var session = Session(participant, DateTimeOffset.UtcNow) with
        {
            TotalRaceLaps = 1,
            MinimumRequiredPitStops = 0
        };

        var snapshot = EstateFullRaceStrategyHudRuntime.CreateSnapshot(
            session, participant, null);

        Assert.AreEqual(1, snapshot.TotalLaps);
        Assert.IsEmpty(snapshot.StopWindows);
        Assert.HasCount(1, snapshot.Stints);
        Assert.AreEqual((1, 1),
            (snapshot.Stints[0].StartLap, snapshot.Stints[0].EndLap));
    }

    [TestMethod]
    public void FullRaceStrategyProducesBoundedOrderedPlanAcrossSupportedRules()
    {
        for (var laps = 1; laps <= 200; laps++)
        {
            for (var requiredStops = 0; requiredStops <= 20; requiredStops++)
            {
                var participant = Participant();
                var session = Session(participant, DateTimeOffset.UtcNow) with
                {
                    TotalRaceLaps = laps,
                    MinimumRequiredPitStops = requiredStops
                };
                var snapshot = EstateFullRaceStrategyHudRuntime.CreateSnapshot(
                    session, participant, null);

                Assert.IsTrue(snapshot.StopWindows.Count <= Math.Max(0, laps - 1));
                Assert.AreEqual(snapshot.StopWindows.Count + 1, snapshot.Stints.Count);
                Assert.IsTrue(snapshot.StopWindows.All(window =>
                    window.StartLap >= 1 &&
                    window.StartLap <= window.TargetLap &&
                    window.TargetLap <= window.EndLap &&
                    window.EndLap < laps));
                Assert.IsTrue(snapshot.StopWindows
                    .Zip(snapshot.StopWindows.Skip(1))
                    .All(pair => pair.First.TargetLap < pair.Second.TargetLap));
                Assert.AreEqual(1, snapshot.Stints[0].StartLap);
                Assert.AreEqual(laps, snapshot.Stints[^1].EndLap);
            }
        }
    }

    [TestMethod]
    public void LegacyLayoutGetsIndependentFullRaceStrategyDefaults()
    {
        var legacy = new LazyForza.Domain.EstateRaceHudLayout(
            Leaderboard: new(false, .3, .4, 1.1, .5));

        var normalized = LazyForza.Domain.EstateRaceHudLayoutSettings.Normalize(legacy);

        Assert.IsFalse(normalized.Get(LazyForza.Domain.EstateRaceHudWidgetKind.Leaderboard).IsVisible);
        var strategy = normalized.Get(LazyForza.Domain.EstateRaceHudWidgetKind.FullRaceStrategy);
        Assert.IsTrue(strategy.IsVisible);
        Assert.AreEqual(.296, strategy.Left, 1e-12);
        Assert.AreEqual(.7735833333333334, strategy.Top, 1e-12);
        Assert.AreEqual(.60, strategy.Scale, 1e-12);
    }

    [TestMethod]
    public void FullRaceStrategyLayoutAllowsSixtyPercentMinimumScale()
    {
        const LazyForza.Domain.EstateRaceHudWidgetKind kind =
            LazyForza.Domain.EstateRaceHudWidgetKind.FullRaceStrategy;

        Assert.AreEqual(.60, LazyForza.Domain.EstateRaceHudLayoutSettings.MinimumScale(kind), 1e-12);
        Assert.AreEqual(.60, LazyForza.Domain.EstateRaceHudLayoutSettings.NormalizeScale(kind, .42), 1e-12);
        Assert.AreEqual(.65, LazyForza.Domain.EstateRaceHudLayoutSettings.NormalizeScale(kind, .65), 1e-12);
    }

    private static EstateRaceParticipant Participant() => new(
        Guid.NewGuid(), 1, "测试车手", "#27DBED", null,
        RaceParticipantStatus.OnTrack, true, false,
        0, 0, 0, .25, .50, 120, 40,
        null, null, null, null,
        false, false, 0, false, 0,
        RaceGripCondition.Unknown, [], [], DateTimeOffset.UtcNow);

    private static EstateRaceSession Session(
        EstateRaceParticipant participant,
        DateTimeOffset startsAt) => new(
        1,
        "整场策略测试",
        RaceSessionPhase.Race,
        RaceControlFlag.Green,
        null,
        "strategy-test",
        "1",
        null,
        20,
        startsAt,
        null,
        null,
        null,
        [],
        null,
        [participant],
        startsAt,
        MinimumRequiredPitStops: 1);

    private static EstatePitStrategyPrediction Prediction() => new(
        EstatePitStrategyDecision.PitWindow,
        "建议进站窗口",
        "实时策略测试",
        7,
        9,
        22.4,
        true,
        68.424,
        .38,
        4.8,
        EstatePitStrategyConfidence.Medium,
        6,
        1,
        0,
        0,
        1,
        1,
        HistoricalSampleCount: 12,
        UsesHistoricalPace: true,
        MinimumRequiredPitStops: 1,
        CompletedPitStops: 0,
        RemainingRequiredPitStops: 1);
}
