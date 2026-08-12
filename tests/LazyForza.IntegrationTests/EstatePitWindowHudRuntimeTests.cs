using LazyForza.Modules.EstateRace;
using LazyForza.Overlay;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class EstatePitWindowHudRuntimeTests
{
    [TestMethod]
    public void SuggestionAppearsAtFinishLineFromTwoLapsBeforeWindowAndHoldsForSixSeconds()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var state = OverlayLayoutPreviewState.EstateRace(now);
        var participantId = state.LocalParticipantId!.Value;
        var session = SetLocal(state.Session!, participantId, completedLaps: 3);
        var runtime = new EstatePitWindowHudRuntime();
        var prediction = Prediction(startLap: 7, endLap: 9);

        Assert.IsFalse(runtime.Update(session, participantId, prediction, now).IsVisible);

        session = SetLocal(session, participantId, completedLaps: 4);
        var shown = runtime.Update(session, participantId, prediction, now.AddSeconds(1));
        Assert.IsTrue(shown.IsVisible);
        Assert.AreEqual(2, shown.LapsUntilWindow);
        Assert.IsFalse(shown.WindowOpen);
        Assert.AreEqual(0.38, shown.DegradationPerLapSeconds);

        Assert.IsTrue(runtime.Update(session, participantId, prediction, now.AddSeconds(6.5)).IsVisible,
            "The suggestion must remain readable for at least five seconds after crossing the line.");
        Assert.IsFalse(runtime.Update(session, participantId, prediction, now.AddSeconds(7.1)).IsVisible);
    }

    [TestMethod]
    public void SuggestionDoesNotShowEarlyAndRetriggersOnEachEligibleLap()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var state = OverlayLayoutPreviewState.EstateRace(now);
        var participantId = state.LocalParticipantId!.Value;
        var session = SetLocal(state.Session!, participantId, completedLaps: 3);
        var runtime = new EstatePitWindowHudRuntime();
        var prediction = Prediction(startLap: 8, endLap: 10);
        _ = runtime.Update(session, participantId, prediction, now);

        session = SetLocal(session, participantId, completedLaps: 4);
        Assert.IsFalse(runtime.Update(session, participantId, prediction, now.AddSeconds(1)).IsVisible);

        session = SetLocal(session, participantId, completedLaps: 5);
        var twoLaps = runtime.Update(session, participantId, prediction, now.AddSeconds(10));
        Assert.IsTrue(twoLaps.IsVisible);
        Assert.AreEqual(2, twoLaps.LapsUntilWindow);

        session = SetLocal(session, participantId, completedLaps: 6);
        var oneLap = runtime.Update(session, participantId, prediction, now.AddSeconds(20));
        Assert.IsTrue(oneLap.IsVisible);
        Assert.AreEqual(1, oneLap.LapsUntilWindow);

        session = SetLocal(session, participantId, completedLaps: 7);
        var open = runtime.Update(session, participantId, prediction, now.AddSeconds(30));
        Assert.IsTrue(open.IsVisible);
        Assert.IsTrue(open.WindowOpen);
    }

    [TestMethod]
    public void EnteringPitOrLeavingRaceClearsSuggestionImmediately()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var state = OverlayLayoutPreviewState.EstateRace(now);
        var participantId = state.LocalParticipantId!.Value;
        var session = SetLocal(state.Session!, participantId, completedLaps: 3);
        var runtime = new EstatePitWindowHudRuntime();
        var prediction = Prediction(startLap: 7, endLap: 9);
        _ = runtime.Update(session, participantId, prediction, now);

        session = SetLocal(session, participantId, completedLaps: 4);
        Assert.IsTrue(runtime.Update(session, participantId, prediction, now.AddSeconds(1)).IsVisible);

        session = SetLocal(session, participantId, completedLaps: 4, inPit: true);
        Assert.IsFalse(runtime.Update(session, participantId, prediction, now.AddSeconds(2)).IsVisible);
        Assert.IsFalse(runtime.Update(
            session with { Phase = RaceSessionPhase.Finished },
            participantId,
            prediction,
            now.AddSeconds(3)).IsVisible);
    }

    private static EstateRaceSession SetLocal(
        EstateRaceSession session,
        Guid participantId,
        int completedLaps,
        bool inPit = false) => session with
    {
        Phase = RaceSessionPhase.Race,
        Flag = RaceControlFlag.Green,
        Participants = session.Participants
            .Select(participant => participant.Id == participantId
                ? participant with
                {
                    CompletedLaps = completedLaps,
                    IsInPitLane = inPit,
                    IsInServiceZone = false,
                    Status = inPit ? RaceParticipantStatus.InPitLane : RaceParticipantStatus.OnTrack
                }
                : participant)
            .ToArray()
    };

    private static EstatePitStrategyPrediction Prediction(int startLap, int endLap) => new(
        EstatePitStrategyDecision.PitWindow,
        "进站窗口",
        "测试",
        startLap,
        endLap,
        18.4,
        true,
        68.4,
        0.38,
        4.2,
        EstatePitStrategyConfidence.High,
        8,
        0,
        0,
        0,
        0,
        2);
}
