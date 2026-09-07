using System.Text.Json;
using LazyForza.Modules.EstateRace;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class LapEventSendQueueTests
{
    [TestMethod]
    public void RetryHintsAreOptionalAndDoNotChangeLegacyErrorMeaning()
    {
        var options = EstateRaceWireProtocol.JsonOptions;
        var legacy = JsonSerializer.Deserialize<RaceLoginRejected>(
            "{\"code\":\"invalidPassword\",\"message\":\"Try again\"}", options)!;
        Assert.IsNull(legacy.RetryAfterSeconds);
        var current = new RaceLoginRejected("rateLimited", "Retry in 30 seconds.", 30);
        var json = JsonSerializer.Serialize(current, options);
        Assert.AreEqual(30, JsonSerializer.Deserialize<RaceLoginRejected>(json, options)!.RetryAfterSeconds);
        var oldReader = JsonSerializer.Deserialize<LegacyError>(json, options)!;
        Assert.AreEqual("rateLimited", oldReader.Code);
        Assert.AreEqual(current.Message, oldReader.Message);
        Assert.IsNull(JsonSerializer.Deserialize<RaceErrorPayload>(
            "{\"code\":\"invalidMessage\",\"message\":\"Invalid\"}", options)!.RetryAfterSeconds);
    }

    [TestMethod]
    public void OptionalValidationFieldsRemainCompatibleWithLegacyV2Messages()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { eventId = id, isAccepted = true }, EstateRaceWireProtocol.JsonOptions);
        var current = JsonSerializer.Deserialize<RaceLapAcknowledgement>(json, EstateRaceWireProtocol.JsonOptions)!;
        Assert.IsNull(current.ValidationStatus);
        var extended = JsonSerializer.Serialize(current with { ValidationStatus = RaceLapValidationStatus.PendingReview },
            EstateRaceWireProtocol.JsonOptions);
        var legacy = JsonSerializer.Deserialize<LegacyAcknowledgement>(extended, EstateRaceWireProtocol.JsonOptions)!;
        Assert.AreEqual(id, legacy.EventId);
        Assert.IsTrue(legacy.IsAccepted);
        var lapJson = JsonSerializer.Serialize(new
        {
            eventId = id, lapNumber = 1, lapSeconds = 60, sectorSeconds = new[] { 20, 20, 20 },
            isValid = true, clientMonotonicMilliseconds = 60000
        }, EstateRaceWireProtocol.JsonOptions);
        Assert.IsNull(JsonSerializer.Deserialize<RaceLapCompleted>(lapJson, EstateRaceWireProtocol.JsonOptions)!.StageId);
    }

    [TestMethod]
    public void StableStageSurvivesRecoveryClockRebaseButNewStageClearsPendingLaps()
    {
        var queue = new LapEventSendQueue();
        var session = Session() with { StageId = Guid.NewGuid() };
        queue.ApplySession(session);
        var lap = Lap() with { StageId = queue.StageId };
        queue.Enqueue(lap);
        Assert.IsFalse(queue.ApplySession(session with
        {
            Phase = RaceSessionPhase.Suspended, SuspendedFromPhase = RaceSessionPhase.Race,
            StartsAt = session.StartsAt!.Value.AddHours(1)
        }));
        queue.Reconnect();
        Assert.AreEqual(lap, queue.TakeDue(0));
        Assert.IsFalse(queue.ApplySession(session with { StartsAt = session.StartsAt.Value.AddHours(2) }));
        Assert.AreEqual(session.StageId, queue.StageId);
        Assert.IsTrue(queue.ApplySession(session with { StageId = Guid.NewGuid() }));
        Assert.IsNull(queue.TakeDue(5000));
        Assert.IsFalse(queue.Acknowledge(lap.EventId));
    }

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

    private sealed record LegacyAcknowledgement(Guid EventId, bool IsAccepted);
    private sealed record LegacyError(string Code, string Message);

    private static RaceLapCompleted Lap() => new(Guid.NewGuid(), 1, 60, [20, 20, 20], true, null, 100);

    private static EstateRaceSession Session() => new(
        1, "Queue test", RaceSessionPhase.Race, RaceControlFlag.Green, null,
        "track", "revision", null, 5, DateTimeOffset.UnixEpoch, null, null, null,
        [], null, [], DateTimeOffset.UnixEpoch);
}
