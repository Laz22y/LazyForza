namespace LazyForza.Modules.EstateRace;

internal enum LapEventEnqueueResult { Added, Duplicate, RecoveryDisabled, Full }

/// <summary>Reliable commands retain their original payload until a terminal acknowledgement.</summary>
internal sealed class LapEventSendQueue(int capacity = 12)
{
    private readonly object sync = new();
    private readonly List<Pending> pending = [];
    private readonly HashSet<Guid> observed = [];
    private Stage? stage;
    private bool recoveryEnabled;
    private bool overflowed;

    public bool Overflowed { get { lock (sync) return overflowed; } }
    public Guid? StageId { get { lock (sync) return stage?.Id; } }

    public bool ApplySession(EstateRaceSession session, bool reset = false)
    {
        lock (sync)
        {
            var phase = session.Phase == RaceSessionPhase.Suspended
                ? session.SuspendedFromPhase ?? stage?.Phase ?? session.Phase
                : session.Phase == RaceSessionPhase.Finished
                    ? stage?.Phase ?? RaceSessionPhase.Race : session.Phase;
            // Timed sessions can restart at the same session number while offline.
            // Their deadline is cleared on expiry; that is not a new stage.
            var timingIdentity = phase switch
            {
                RaceSessionPhase.Race => session.StartsAt,
                RaceSessionPhase.Practice => session.PracticeEndsAt ?? (stage?.Phase == phase ? stage.TimingIdentity : null),
                RaceSessionPhase.Qualifying => session.QualifyingEndsAt ?? (stage?.Phase == phase ? stage.TimingIdentity : null),
                _ => null
            };
            var next = new Stage(phase, session.TrackId, session.TrackRevision,
                session.TrackPackageHash,
                session.StageId is null ? timingIdentity : null,
                phase == RaceSessionPhase.Practice ? session.PracticeSessionNumber :
                phase == RaceSessionPhase.Qualifying ? session.QualifyingSessionNumber : 0,
                session.StageId, session.EventId);
            var changed = reset || stage != next;
            if (changed) Clear();
            stage = next;
            recoveryEnabled = session.DisconnectedLapRecoveryEnabled;
            if (!recoveryEnabled) pending.RemoveAll(item => item.Lap.IsRecoveredAfterDisconnect);
            return changed;
        }
    }

    public LapEventEnqueueResult Enqueue(RaceLapCompleted lap)
    {
        lock (sync)
        {
            if (!observed.Add(lap.EventId)) return LapEventEnqueueResult.Duplicate;
            if (lap.IsRecoveredAfterDisconnect && !recoveryEnabled)
                return LapEventEnqueueResult.RecoveryDisabled;
            if (pending.Count >= capacity)
            {
                overflowed = true;
                return LapEventEnqueueResult.Full;
            }
            pending.Add(new Pending(lap, null));
            return LapEventEnqueueResult.Added;
        }
    }

    public RaceLapCompleted? TakeDue(long now)
    {
        lock (sync)
        {
            var index = pending.FindIndex(item => item.LastAttempt is null || now - item.LastAttempt >= 2000);
            if (index < 0) return null;
            var item = pending[index];
            pending[index] = item with { LastAttempt = now };
            return item.Lap;
        }
    }

    public bool Contains(Guid eventId)
    {
        lock (sync) return pending.Any(item => item.Lap.EventId == eventId);
    }

    public bool Acknowledge(Guid eventId)
    {
        lock (sync) return pending.RemoveAll(item => item.Lap.EventId == eventId) != 0;
    }

    public void Reconnect()
    {
        lock (sync)
            for (var i = 0; i < pending.Count; i++)
                pending[i] = pending[i] with { LastAttempt = null };
    }

    public void Clear()
    {
        lock (sync)
        {
            pending.Clear();
            observed.Clear();
            overflowed = false;
            stage = null;
        }
    }

    private sealed record Pending(RaceLapCompleted Lap, long? LastAttempt);
    private sealed record Stage(RaceSessionPhase Phase, string? TrackId, string? TrackRevision,
        string? TrackPackageHash, DateTimeOffset? TimingIdentity, int Number, Guid? Id, Guid? EventId);
}
