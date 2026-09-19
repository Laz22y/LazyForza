using System.Text;
using System.Threading.Channels;
using LazyForza.Modules.EstateRace;
using LazyForza.Speech;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class RaceEngineerTests
{
    private static EngineerMessage Message(string key, EngineerPriority priority = EngineerPriority.Information,
        string? category = null, int cooldown = 0) => new(key, category ?? key, key, priority,
            TimeSpan.FromSeconds(cooldown), DateTimeOffset.UtcNow.AddMinutes(1));

    [TestMethod]
    public async Task DensityAndCategoryPreferencesFilterAndCancelWithoutDelayingUrgentCallouts()
    {
        var now = DateTimeOffset.UtcNow;
        var output = new ControlledSpeech();
        using var engineer = new RaceEngineer(output, () => now);
        engineer.SetStage("race");
        engineer.Configure(true, false, 70, new(EngineerDensity.Essential));
        Assert.IsFalse(engineer.Enqueue(Message("best", category: "best")));
        Assert.IsFalse(engineer.Enqueue(Message("pit", category: "pit")));
        Assert.IsTrue(engineer.Enqueue(Message("red", EngineerPriority.Emergency, "flag")));
        var red = await output.Next();
        engineer.Enqueue(Message("penalty", EngineerPriority.Important, "penalty:1"));
        engineer.Configure(true, false, 70, new(Flags: false, Penalties: false));
        Assert.IsTrue(red.Token.IsCancellationRequested);
        Assert.IsFalse(engineer.Enqueue(Message("yellow", EngineerPriority.Important, "flag")));
        Assert.IsFalse(engineer.Enqueue(Message("another-penalty", EngineerPriority.Important, "penalty:2")));
        engineer.Enqueue(Message("pit1", category: "pit", cooldown: 60));
        var pit = await output.Next();
        Assert.AreEqual("pit1", pit.Text);
        await Finish(engineer, pit);
        now = now.AddSeconds(31);
        Assert.IsFalse(engineer.Enqueue(Message("pit2", category: "pit", cooldown: 60)));
        engineer.Configure(true, false, 70, new(EngineerDensity.Detailed));
        Assert.IsTrue(engineer.Enqueue(Message("pit3", category: "pit", cooldown: 60)));
        Assert.AreEqual("pit3", (await output.Next()).Text);
        Assert.IsTrue(engineer.Enqueue(Message("urgent", EngineerPriority.Emergency, "flag")));
        Assert.AreEqual("urgent", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task HistoryIsBoundedExcludesPreviewsAndRepeatsWithoutExtendingExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var output = new ControlledSpeech();
        using var engineer = new RaceEngineer(output, () => now);
        engineer.Configure(true, false, 70); engineer.SetStage("race");
        var preview = engineer.PreviewAsync("sample");
        (await output.Next()).Finish.TrySetResult();
        Assert.IsTrue(await preview);
        Assert.HasCount(0, engineer.History);
        Assert.IsFalse(engineer.RepeatLast());
        for (var i = 0; i < 23; i++)
        {
            engineer.Enqueue(Message($"message-{i}", category: "flag") with { ExpiresAt = now.AddSeconds(30) });
            var speaking = await output.Next();
            Assert.AreEqual(EngineerDelivery.Speaking, engineer.History[0].Delivery);
            Assert.IsFalse(engineer.RepeatLast());
            await Finish(engineer, speaking);
        }
        Assert.HasCount(20, engineer.History);
        Assert.AreEqual("message-3", engineer.History[^1].Text);
        engineer.Configure(true, false, 43);
        Assert.IsTrue(engineer.RepeatLast());
        Assert.IsFalse(engineer.RepeatLast(), "Double clicks must not enqueue duplicate repeats.");
        var repeated = await output.Next();
        Assert.AreEqual("message-22", repeated.Text);
        Assert.AreEqual(43, repeated.Volume);
        await Finish(engineer, repeated);
        Assert.IsTrue(engineer.History[0].IsRepeat);
        now = now.AddSeconds(31);
        Assert.AreEqual(EngineerRepeatState.Expired, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast());
    }

    [TestMethod]
    public async Task ManualRepeatsRespectMuteCategoriesStagesAndGiveWayToLiveMessages()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("yellow", EngineerPriority.Important, "flag"));
        await Finish(engineer, await output.Next());
        engineer.Configure(true, true, 70);
        Assert.AreEqual(EngineerRepeatState.Muted, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast());
        engineer.Configure(true, false, 70, new(Flags: false));
        Assert.AreEqual(EngineerRepeatState.CategoryDisabled, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast());
        engineer.Configure(true, false, 70, new());
        Assert.IsTrue(engineer.RepeatLast());
        var repeated = await output.Next();
        engineer.Enqueue(Message("new-penalty", EngineerPriority.Important, "penalty:1"));
        Assert.IsTrue(repeated.Token.IsCancellationRequested);
        var penalty = await output.Next();
        Assert.AreEqual("new-penalty", penalty.Text);
        Assert.AreEqual(EngineerDelivery.Interrupted, engineer.History.Single(item => item.IsRepeat).Delivery);
        await Finish(engineer, penalty);
        engineer.SetStage("other-race");
        Assert.AreEqual(EngineerRepeatState.StateChanged, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast());
        engineer.SetStage(null);
        Assert.AreEqual(EngineerRepeatState.NoSession, engineer.RepeatState);
    }

    [TestMethod]
    public async Task ChangedFlagsInvalidateCompletedHistoryEvenWhenNewCategoryIsDisabled()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var observer = new RaceEngineerObserver(engineer);
        var state = State();
        state = state with { Session = state.Session! with { Flag = RaceControlFlag.Yellow } };
        observer.Observe(state);
        await Finish(engineer, await output.Next());
        engineer.Configure(true, false, 70, new(Flags: false));
        observer.Observe(state with { Session = state.Session! with { Flag = RaceControlFlag.Green } });
        engineer.Configure(true, false, 70, new());
        Assert.AreEqual(EngineerRepeatState.StateChanged, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast());
        Assert.AreEqual(EngineerRepeatState.StateChanged, engineer.History[0].Validity);
    }

    [TestMethod]
    [DataRow("served")]
    [DataRow("revoked")]
    [DataRow("removed")]
    [DataRow("changed")]
    [DataRow("disconnect")]
    public async Task PenaltyStateChangesCancelRepeatsAndNeverResurrectOldInstructions(string change)
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var observer = new RaceEngineerObserver(engineer);
        var state = State(); observer.Observe(state);
        var penalty = new EstateRacePenalty(Guid.NewGuid(), RacePenaltyKind.Time, 5, null, "test", false, false);
        state = state with { Session = state.Session! with { Participants = [state.Session.Participants[0] with { Penalties = [penalty] }] } };
        observer.Observe(state);
        await Finish(engineer, await output.Next());
        Assert.IsTrue(engineer.RepeatLast());
        var repeat = await output.Next();
        var changed = change switch
        {
            "served" => penalty with { IsServed = true },
            "revoked" => penalty with { IsRevoked = true },
            _ => penalty with { ValueSeconds = 10 }
        };
        var next = state with { Session = state.Session! with { Participants = [state.Session.Participants[0] with
            { Penalties = change == "removed" ? [] : [changed] }] } };
        if (change == "disconnect") observer.Disconnect(); else observer.Observe(next);
        Assert.IsTrue(repeat.Token.IsCancellationRequested);
        Assert.IsFalse(engineer.RepeatLast());
        if (change == "changed")
        {
            var updated = await output.Next();
            Assert.AreEqual("罚时 10 秒。", updated.Text);
            await Finish(engineer, updated);
            Assert.AreEqual(EngineerRepeatState.StateChanged, engineer.History.Single(item => item.Text == "罚时 5 秒。" && !item.IsRepeat).Validity);
        }
        else await WaitUntil(() => engineer.History[0].Delivery == EngineerDelivery.Interrupted);
    }

    private static async Task Finish(RaceEngineer engineer, Utterance utterance)
    {
        utterance.Finish.TrySetResult();
        await WaitUntil(() => engineer.History[0].Delivery == EngineerDelivery.Completed);
    }

    [TestMethod]
    public async Task LatestAuthorityIsRecheckedBeforeAndDuringRepeatWithoutWaitingForUiRefresh()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var initial = State();
        var latest = initial with { Session = initial.Session! with { Flag = RaceControlFlag.Yellow } };
        var observer = new RaceEngineerObserver(engineer, latestState: () => Volatile.Read(ref latest));
        observer.Observe(latest);
        await Finish(engineer, await output.Next());
        Volatile.Write(ref latest, latest with { Session = latest.Session! with { Flag = RaceControlFlag.Green } });
        Assert.AreEqual(EngineerRepeatState.StateChanged, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast(), "A newer snapshot must block replay before the UI observer runs.");
        observer.Observe(latest);
        await Finish(engineer, await output.Next());
        Assert.IsTrue(engineer.RepeatLast());
        var repeat = await output.Next();
        Volatile.Write(ref latest, latest with { Session = latest.Session! with { Flag = RaceControlFlag.Red } });
        await WaitUntil(() => repeat.Token.IsCancellationRequested);
        await WaitUntil(() => engineer.History[0].Delivery == EngineerDelivery.Interrupted);
        Assert.IsFalse(engineer.RepeatLast());
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    [TestMethod]
    public async Task DisabledByDefaultAndDuplicateEventsNeverRepeat()
    {
        var output = new ControlledSpeech();
        using var engineer = new RaceEngineer(output);
        engineer.SetStage("race");
        Assert.IsFalse(engineer.Enqueue(Message("disabled")));
        engineer.Configure(true, false, 70);
        Assert.IsTrue(engineer.Enqueue(Message("lap")));
        var first = await output.Next();
        Assert.IsFalse(engineer.Enqueue(Message("lap")));
        first.Finish.TrySetResult();
        Assert.IsFalse(engineer.Enqueue(Message("lap")));
        engineer.Enqueue(Message("barrier"));
        Assert.AreEqual("barrier", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task EmergencyInterruptsSpeechAndPrecedesPendingImportantMessage()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("best"));
        var first = await output.Next();
        engineer.Enqueue(Message("penalty", EngineerPriority.Important));
        engineer.Enqueue(Message("red", EngineerPriority.Emergency));
        var urgent = await output.Next();
        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.AreEqual("red", urgent.Text);
        urgent.Finish.TrySetResult();
        Assert.AreEqual("penalty", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task StageSwitchCancelsSpeechClearsQueueAndResetsDeduplication()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("same-id"));
        var old = await output.Next();
        engineer.Enqueue(Message("old-pending", EngineerPriority.Important));
        engineer.SetStage("next-stage");
        Assert.IsTrue(old.Token.IsCancellationRequested);
        Assert.IsTrue(engineer.Enqueue(Message("same-id")));
        Assert.AreEqual("same-id", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task MuteAndZeroVolumeCancelAndDiscardQueuedMessages()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("speaking"));
        var first = await output.Next();
        engineer.Enqueue(Message("stale", EngineerPriority.Important));
        engineer.Configure(true, true, 70);
        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.IsFalse(engineer.Enqueue(Message("muted")));
        engineer.Configure(true, false, 120);
        engineer.Enqueue(Message("fresh"));
        var fresh = await output.Next();
        Assert.AreEqual("fresh", fresh.Text);
        Assert.AreEqual(100, fresh.Volume);
        engineer.Configure(true, false, 0);
        Assert.IsTrue(fresh.Token.IsCancellationRequested);
        Assert.IsFalse(engineer.Enqueue(Message("silent")));
        engineer.Dispose();
        await engineer.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task CooldownAndExpiryUseClockAndWithdrawCancelsOutdatedPrediction()
    {
        var now = DateTimeOffset.UtcNow;
        var output = new ControlledSpeech();
        using var engineer = new RaceEngineer(output, () => now);
        engineer.Configure(true, false, 70);
        engineer.SetStage("race");
        engineer.Enqueue(Message("pit1", category: "pit", cooldown: 60));
        var first = await output.Next();
        Assert.IsFalse(engineer.Enqueue(Message("pit2", category: "pit", cooldown: 60)));
        engineer.Withdraw("pit");
        Assert.IsTrue(first.Token.IsCancellationRequested);
        now = now.AddSeconds(61);
        Assert.IsTrue(engineer.Enqueue(Message("pit3", category: "pit", cooldown: 60) with { ExpiresAt = now.AddMinutes(1) }));
        var next = await output.Next();
        engineer.Enqueue(Message("expires") with { ExpiresAt = now.AddSeconds(1) });
        now = now.AddSeconds(2);
        engineer.Enqueue(Message("barrier") with { ExpiresAt = now.AddMinutes(1) });
        next.Finish.TrySetResult();
        Assert.AreEqual("barrier", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task AudioFailureIsContainedAndExplicitReenableRecovers()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("failure"));
        (await output.Next()).Finish.TrySetException(new InvalidOperationException("No voice"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (engineer.Error is null) await Task.Delay(5, timeout.Token);
        Assert.IsFalse(engineer.Enqueue(Message("unavailable")));
        Assert.IsFalse(engineer.Completion.IsCompleted);
        engineer.Configure(false, false, 70);
        engineer.Configure(true, false, 70);
        Assert.IsNull(engineer.Error);
        await WaitUntil(() => engineer.History[0].Delivery == EngineerDelivery.Failed);
        Assert.AreEqual(EngineerRepeatState.Incomplete, engineer.RepeatState);
        Assert.IsFalse(engineer.RepeatLast(), "Re-enabling speech must not make a failed transmission repeatable.");
        Assert.IsTrue(engineer.Enqueue(Message("recovered")));
        Assert.AreEqual("recovered", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task ExpiredInFlightSpeechIsCancelledAndNextMessageStillPlays()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        Assert.IsTrue(engineer.Enqueue(Message("expiring") with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(300) }));
        var expiring = await output.Next();
        engineer.Enqueue(Message("fresh"));
        Assert.AreEqual("fresh", (await output.Next()).Text);
        Assert.IsTrue(expiring.Token.IsCancellationRequested);
        Assert.IsNull(engineer.Error);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShutdownDisposesOutputOnceAndContainsCleanupFailure(bool failCleanup)
    {
        var output = new ControlledSpeech { FailCleanup = failCleanup };
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("speaking"));
        var speaking = await output.Next();
        engineer.Dispose();
        engineer.Dispose();
        await engineer.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(speaking.Token.IsCancellationRequested);
        Assert.AreEqual(1, output.DisposeCount);
        Assert.IsFalse(engineer.Enqueue(Message("after")));
    }

    [TestMethod]
    public async Task QueueIsBoundedAndPreservesUrgentMessages()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("block", EngineerPriority.Emergency));
        var block = await output.Next();
        for (var i = 0; i < 16; i++) Assert.IsTrue(engineer.Enqueue(Message($"info{i}")));
        Assert.IsFalse(engineer.Enqueue(Message("overflow")));
        Assert.IsTrue(engineer.Enqueue(Message("urgent", EngineerPriority.Emergency)));
        block.Finish.TrySetResult();
        Assert.AreEqual("urgent", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task RepeatedSnapshotsAnnounceOnlyNewPenaltyAndFlagTransitions()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var observer = new RaceEngineerObserver(engineer);
        var state = State();
        observer.Observe(state);
        var driver = state.Session!.Participants[0] with
        {
            Penalties = [new(Guid.NewGuid(), RacePenaltyKind.Time, 5, null, "test", false, false)]
        };
        state = state with { Session = state.Session with { Participants = [driver] } };
        observer.Observe(state);
        Assert.AreEqual("罚时 5 秒。", (await output.Next()).Text);
        observer.Observe(state);
        observer.Observe(state with { Session = state.Session with { Flag = RaceControlFlag.Red } });
        Assert.AreEqual("红旗，比赛暂停。", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task PredictionRetainsUncertaintyAndIsWithdrawnWhenNoLongerApplicable()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var observer = new RaceEngineerObserver(engineer);
        var state = State() with { PitStrategy = new(EstatePitStrategyDecision.PitWindow, "", "",
            2, 3, 20, false, 60, null, null, EstatePitStrategyConfidence.Low, 1, 0, 0, 0, 0, 0) };
        observer.Observe(state);
        var advice = await output.Next();
        StringAssert.Contains(advice.Text, "样本不足");
        StringAssert.Contains(advice.Text, "可能");
        observer.Observe(state with { PitStrategy = state.PitStrategy with { Decision = EstatePitStrategyDecision.InPit } });
        Assert.IsTrue(advice.Token.IsCancellationRequested);
        engineer.Enqueue(Message("barrier"));
        Assert.AreEqual("barrier", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task ObserverClearsOldStageMessagesAndUsesExistingBestLap()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var observer = new RaceEngineerObserver(engineer);
        var state = State();
        observer.Observe(state);
        var session = state.Session!;
        observer.Observe(state with { Session = session with
        {
            Participants = [session.Participants[0] with { BestLapSeconds = 64, CompletedLaps = 2 }]
        } });
        var best = await output.Next();
        StringAssert.Contains(best.Text, "个人最快圈");
        engineer.Enqueue(Message("old-pending", EngineerPriority.Important));
        observer.Observe(state with { Session = session with { StageId = Guid.NewGuid(), Flag = RaceControlFlag.Yellow } });
        Assert.IsTrue(best.Token.IsCancellationRequested);
        Assert.AreEqual("黄旗，注意减速。", (await output.Next()).Text);
    }

    [TestMethod]
    public async Task RedFlagDiscardsQueuedPitAdviceAndMissingPredictionStopsSpeech()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var observer = new RaceEngineerObserver(engineer);
        var state = State();
        observer.Observe(state);
        engineer.Enqueue(Message("pit", category: "pit"));
        var pit = await output.Next();
        observer.Observe(state);
        Assert.IsTrue(pit.Token.IsCancellationRequested);
        engineer.Enqueue(Message("block", EngineerPriority.Emergency));
        var block = await output.Next();
        engineer.Enqueue(Message("old-pit", category: "pit"));
        observer.Observe(state with { Session = state.Session! with { Flag = RaceControlFlag.Red } });
        block.Finish.TrySetResult();
        var red = await output.Next();
        Assert.AreEqual("红旗，比赛暂停。", red.Text);
        engineer.Enqueue(Message("barrier"));
        red.Finish.TrySetResult();
        Assert.AreEqual("barrier", (await output.Next()).Text);
    }

    [TestMethod]
    public void RadioCuesAreDistinctLocalPcmWithVolumeAndSilence()
    {
        var start = RadioCues.Connect.ToWave(70);
        var end = RadioCues.Disconnect.ToWave(70);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(start, 0, 4));
        Assert.AreEqual("WAVE", Encoding.ASCII.GetString(start, 8, 4));
        Assert.AreEqual(24000, BitConverter.ToInt32(start, 24));
        Assert.AreEqual(start.Length - 44, BitConverter.ToInt32(start, 40));
        Assert.IsFalse(start.SequenceEqual(end));
        Assert.IsTrue(RadioCues.Connect.ToWave(0).Skip(44).All(value => value == 0));
        Assert.IsTrue(start.Skip(44).Any(value => value != 0));
    }

    private static RaceEngineer Enabled(ControlledSpeech output)
    {
        var engineer = new RaceEngineer(output);
        engineer.Configure(true, false, 70);
        engineer.SetStage("race");
        return engineer;
    }

    [TestMethod]
    public async Task PreviewWorksOfflineWithAutomaticSpeechDisabledAndUsesCurrentVolume()
    {
        var output = new ControlledSpeech();
        using var engineer = new RaceEngineer(output);
        engineer.Configure(false, false, 35);
        var preview = engineer.PreviewAsync("黄旗，注意减速。");
        var spoken = await output.Next();
        Assert.AreEqual(35, spoken.Volume);
        Assert.IsTrue(engineer.IsPreviewing);
        Assert.IsFalse(await engineer.PreviewAsync("must not queue"));
        spoken.Finish.TrySetResult();
        Assert.IsTrue(await preview.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsFalse(engineer.IsPreviewing);
        Assert.IsFalse(engineer.Enqueue(Message("still disabled")));
    }

    [TestMethod]
    public async Task PreviewHonorsMuteAndZeroVolumeAndDoesNotConsumeRealEventIds()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Configure(true, true, 70);
        Assert.IsFalse(await engineer.PreviewAsync("sample"));
        engineer.Configure(true, false, 0);
        Assert.IsFalse(await engineer.PreviewAsync("sample"));
        engineer.Configure(true, false, 65);
        var preview = engineer.PreviewAsync("preview");
        (await output.Next()).Finish.TrySetResult();
        Assert.IsTrue(await preview);
        Assert.IsTrue(engineer.Enqueue(Message("preview")));
        Assert.AreEqual("preview", (await output.Next()).Text);
    }

    [TestMethod]
    [DataRow("race")]
    [DataRow("stage")]
    [DataRow("mute")]
    [DataRow("stop")]
    [DataRow("dispose")]
    public async Task PreviewCanBeInterruptedWithoutBlockingTheRace(string cause)
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        var preview = engineer.PreviewAsync("sample");
        var spoken = await output.Next();
        switch (cause)
        {
            case "race": engineer.Enqueue(Message("real", EngineerPriority.Emergency)); break;
            case "stage": engineer.SetStage("next"); break;
            case "mute": engineer.Configure(true, true, 70); break;
            case "stop": engineer.StopPreview(); break;
            case "dispose": engineer.Dispose(); break;
        }
        Assert.IsFalse(await preview.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(spoken.Token.IsCancellationRequested);
        if (cause == "race") Assert.AreEqual("real", (await output.Next()).Text);
        Assert.IsNull(engineer.Error);
    }

    [TestMethod]
    public async Task PreviewDoesNotInterruptOngoingRaceMessageAndFailuresAreContained()
    {
        var output = new ControlledSpeech();
        using var engineer = Enabled(output);
        engineer.Enqueue(Message("race"));
        var race = await output.Next();
        Assert.IsFalse(await engineer.PreviewAsync("sample"));
        Assert.IsFalse(race.Token.IsCancellationRequested);
        engineer.SetStage("next");
        // Wait for the cancelled transmission to release the shared output.
        engineer.Dispose();
        await engineer.Completion;
        var failing = new ControlledSpeech();
        using var next = new RaceEngineer(failing);
        var preview = next.PreviewAsync("failure");
        (await failing.Next()).Finish.TrySetException(new InvalidOperationException("Missing voice"));
        Assert.IsFalse(await preview);
        Assert.IsNotNull(next.Error);
        Assert.IsFalse(next.Completion.IsCompleted);
    }

    [TestMethod]
    public void PreviewPhrasesUseBothLanguagesAndAvoidConsecutiveRepeats()
    {
        foreach (var english in new[] { false, true })
        {
            var phrases = new RaceEngineerPreviewPhrases();
            var random = new Random(734);
            var seen = new HashSet<string>();
            string? previous = null;
            for (var i = 0; i < 60; i++)
            {
                var text = phrases.Next(english, random);
                Assert.AreNotEqual(previous, text);
                Assert.AreEqual(english, text.All(character => character < 128));
                seen.Add(text); previous = text;
            }
            Assert.AreEqual(6, seen.Count);
            Assert.IsTrue(seen.Any(text => text.Contains(english ? "prediction" : "不确定性", StringComparison.Ordinal)));
        }
    }

    private static EstateRaceHudState State()
    {
        var id = Guid.NewGuid();
        EstateRaceParticipant driver = new(id, 1, "driver", "#FFFFFF", null,
            RaceParticipantStatus.OnTrack, true, false, 0, 0, .5, .5, .5, 120, 30,
            null, 65, null, null, false, false, 0, false, 0, RaceGripCondition.Unknown, [], [], DateTimeOffset.UtcNow);
        EstateRaceSession session = new(1, "race", RaceSessionPhase.Race, RaceControlFlag.Green,
            null, "track", "revision", null, 5, DateTimeOffset.UtcNow, null, null, null,
            [], null, [driver], DateTimeOffset.UtcNow, StageId: Guid.NewGuid());
        return new(DateTimeOffset.UtcNow, EstateRaceConnectionState.Connected, "", id, session,
            [], RaceGripCondition.Unknown, "", EstatePitServiceState.Empty);
    }

    private sealed record Utterance(string Text, int Volume, CancellationToken Token, TaskCompletionSource Finish);
    private sealed class ControlledSpeech : ISpeechOutput
    {
        public int DisposeCount { get; private set; }
        public bool FailCleanup { get; init; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return FailCleanup ? ValueTask.FromException(new InvalidOperationException("Cleanup failed")) : ValueTask.CompletedTask;
        }
        private readonly Channel<Utterance> started = Channel.CreateUnbounded<Utterance>();
        public async Task SpeakAsync(string text, int volume, CancellationToken cancellationToken)
        {
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            started.Writer.TryWrite(new(text, volume, cancellationToken, finish));
            await finish.Task.WaitAsync(cancellationToken);
        }
        public async Task<Utterance> Next() => await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}
