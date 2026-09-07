using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class LapEventSendQueueTests
{
    [TestMethod]
    public void OnlineLapRetriesWithoutRecoveryAndSurvivesSnapshotsAndReconnect()
    {
        var queue = new LapEventSendQueue();
        var session = Session();
        queue.ApplySession(session);
        var lap = Lap();
        Assert.AreEqual(LapEventEnqueueResult.Added, queue.Enqueue(lap));
        Assert.AreEqual(lap, queue.TakeDue(0));
        Assert.IsNull(queue.TakeDue(0));
        Assert.IsNull(queue.TakeDue(1999));
        Assert.IsFalse(queue.ApplySession(session with { Revision = 2 }));
        Assert.AreEqual(lap, queue.TakeDue(2000));
        queue.Reconnect();
        Assert.AreEqual(lap, queue.TakeDue(2001));
        Assert.IsFalse(lap.IsRecoveredAfterDisconnect);
        Assert.IsTrue(queue.Acknowledge(lap.EventId));
        Assert.IsNull(queue.TakeDue(5000));
    }

    [TestMethod]
    public void OfflinePolicyNeverClearsOnlineCommandsOrReclassifiesIgnoredLaps()
    {
        var queue = new LapEventSendQueue();
        queue.ApplySession(Session());
        var online = Lap();
        var offline = Lap() with { IsRecoveredAfterDisconnect = true };
        queue.Enqueue(online);
        Assert.AreEqual(LapEventEnqueueResult.RecoveryDisabled, queue.Enqueue(offline));
        Assert.AreEqual(LapEventEnqueueResult.Duplicate,
            queue.Enqueue(offline with { IsRecoveredAfterDisconnect = false }));
        Assert.AreEqual(online, queue.TakeDue(0));
        queue.ApplySession(Session() with { DisconnectedLapRecoveryEnabled = true });
        var recoverable = Lap() with { IsRecoveredAfterDisconnect = true };
        queue.Enqueue(recoverable);
        queue.Reconnect();
        Assert.AreEqual(online, queue.TakeDue(1));
        Assert.AreEqual(recoverable, queue.TakeDue(1));
        queue.ApplySession(Session());
        Assert.IsFalse(queue.Contains(recoverable.EventId));
        Assert.IsTrue(queue.Contains(online.EventId));
    }

    [TestMethod]
    public void DuplicateAndOutOfOrderAcknowledgementsDoNotResubmitOrRemoveOtherLaps()
    {
        var queue = new LapEventSendQueue();
        queue.ApplySession(Session());
        var first = Lap();
        var second = Lap();
        queue.Enqueue(first);
        queue.Enqueue(second);
        Assert.AreEqual(LapEventEnqueueResult.Duplicate, queue.Enqueue(first));
        Assert.IsTrue(queue.Acknowledge(second.EventId));
        Assert.IsFalse(queue.Acknowledge(second.EventId));
        Assert.IsFalse(queue.Acknowledge(Guid.NewGuid()));
        Assert.AreEqual(LapEventEnqueueResult.Duplicate, queue.Enqueue(second));
        Assert.AreEqual(first, queue.TakeDue(0));
        Assert.IsTrue(queue.Acknowledge(first.EventId));
        Assert.IsNull(queue.TakeDue(5000));
    }

    [TestMethod]
    public void FullQueueReportsLossOnceAndRetainsUnacknowledgedCommands()
    {
        var queue = new LapEventSendQueue(1);
        queue.ApplySession(Session());
        var first = Lap();
        var overflow = Lap();
        queue.Enqueue(first);
        Assert.AreEqual(LapEventEnqueueResult.Full, queue.Enqueue(overflow));
        Assert.IsTrue(queue.Overflowed);
        Assert.AreEqual(LapEventEnqueueResult.Duplicate, queue.Enqueue(overflow));
        Assert.AreEqual(first, queue.TakeDue(0));
        queue.Acknowledge(first.EventId);
        Assert.AreEqual(LapEventEnqueueResult.Added, queue.Enqueue(Lap()));
        Assert.IsTrue(queue.Overflowed);
        queue.ApplySession(Session() with { Phase = RaceSessionPhase.Lobby });
        Assert.IsFalse(queue.Overflowed);
    }

    [TestMethod]
    public void StageChangesDiscardOldCommandsAndIgnoreLateAcknowledgements()
    {
        var queue = new LapEventSendQueue();
        var session = Session();
        queue.ApplySession(session);
        foreach (var next in new[]
        {
            session with { StartsAt = DateTimeOffset.UnixEpoch.AddMinutes(1) },
            session with { Phase = RaceSessionPhase.Practice, PracticeSessionNumber = 1 },
            session with { Phase = RaceSessionPhase.Practice, PracticeSessionNumber = 2 },
            session with { Phase = RaceSessionPhase.Qualifying, QualifyingSessionNumber = 1 },
            session with { Phase = RaceSessionPhase.Qualifying, QualifyingSessionNumber = 2 },
            session with { TrackRevision = "new" }
        })
        {
            var old = Lap();
            queue.Enqueue(old);
            Assert.IsTrue(queue.ApplySession(next));
            Assert.IsNull(queue.TakeDue(5000));
            Assert.IsFalse(queue.Contains(old.EventId));
            Assert.IsFalse(queue.Acknowledge(old.EventId));
        }
    }

    [TestMethod]
    public void TimedStageExpiryRetainsReceiptsAndSameNumberRestartClearsThem()
    {
        var queue = new LapEventSendQueue();
        var session = Session() with
        {
            Phase = RaceSessionPhase.Practice,
            PracticeSessionNumber = 1,
            PracticeEndsAt = DateTimeOffset.UnixEpoch.AddMinutes(10)
        };
        queue.ApplySession(session);
        var lap = Lap();
        queue.Enqueue(lap);
        Assert.IsFalse(queue.ApplySession(session with { PracticeEndsAt = null, PracticeTimeExpired = true }));
        Assert.IsTrue(queue.Contains(lap.EventId));
        Assert.IsTrue(queue.ApplySession(session with { PracticeEndsAt = session.PracticeEndsAt.Value.AddHours(1) }));
        Assert.IsNull(queue.TakeDue(0));
    }

    [TestMethod]
    public void SuspensionAndFinishRetainReceiptsButFreshConnectionDoesNot()
    {
        var queue = new LapEventSendQueue();
        var session = Session();
        queue.ApplySession(session);
        var lap = Lap();
        queue.Enqueue(lap);
        Assert.IsFalse(queue.ApplySession(session with
        { Phase = RaceSessionPhase.Suspended, SuspendedFromPhase = RaceSessionPhase.Race }));
        Assert.IsFalse(queue.ApplySession(session));
        Assert.IsFalse(queue.ApplySession(session with { Phase = RaceSessionPhase.Finished }));
        Assert.AreEqual(lap, queue.TakeDue(0));
        Assert.IsTrue(queue.ApplySession(session, reset: true));
        Assert.IsNull(queue.TakeDue(5000));
    }

    private static RaceLapCompleted Lap() => new(Guid.NewGuid(), 1, 60, [20, 20, 20], true, null, 100);

    private static EstateRaceSession Session() => new(
        1, "Queue test", RaceSessionPhase.Race, RaceControlFlag.Green, null,
        "track", "revision", null, 5, DateTimeOffset.UnixEpoch, null, null, null,
        [], null, [], DateTimeOffset.UnixEpoch);
}
