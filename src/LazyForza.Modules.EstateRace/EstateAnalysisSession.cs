using System.Security.Cryptography;
using System.Text;
using LazyForza.Domain;

namespace LazyForza.Modules.EstateRace;

internal static class EstateAnalysisSession
{
    public static LapSessionInfo? Resolve(EstateRaceSession session, Guid? participantId,
        LapSessionInfo? previous, bool changed)
    {
        var phase = session.Phase == RaceSessionPhase.Suspended ? session.SuspendedFromPhase : session.Phase;
        var kind = phase switch
        {
            RaceSessionPhase.Practice => LapSessionKind.EstatePractice,
            RaceSessionPhase.Qualifying => LapSessionKind.EstateQualifying,
            RaceSessionPhase.Race => LapSessionKind.EstateRace,
            _ => (LapSessionKind?)null
        };
        if (kind is null || participantId is null) return previous;
        // Stable server stage + driver identity survives reconnects and app restarts.
        // Older servers can only provide a local, in-process session boundary.
        var id = session.StageId is Guid stage && stage != Guid.Empty
            ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"lap-analysis:{session.EventId}:{stage}:{participantId}:{kind}"))[..16])
            : !changed && previous?.Kind == kind ? previous.Id : Guid.NewGuid();
        return new LapSessionInfo(id, kind.Value, session.SessionName,
            phase == RaceSessionPhase.Practice ? session.PracticeSessionNumber :
            phase == RaceSessionPhase.Qualifying ? session.QualifyingSessionNumber : 0);
    }
}
