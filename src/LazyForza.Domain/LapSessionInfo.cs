namespace LazyForza.Domain;

public enum LapSessionKind
{
    GameRace,
    EstateTiming,
    EstatePractice,
    EstateQualifying,
    EstateRace
}

/// <summary>Local analysis identity; ordinary game sessions are inferred, not official event IDs.</summary>
public sealed record LapSessionInfo(Guid Id, LapSessionKind Kind, string? Name = null, int Number = 0);

public readonly record struct LapSessionKey(
    Guid SessionId, Guid TrackId, int Direction, int SectorSchemaVersion,
    string? TrackRevision, string PlayerCode, LapSessionKind? Kind)
{
    public static LapSessionKey FromLap(LapSummary lap) => new(
        lap.SessionId == Guid.Empty ? lap.Id : lap.SessionId,
        lap.TrackId, lap.Direction, lap.SectorSchemaVersion, lap.TrackRevision,
        PlayerIdentitySettings.Normalize(lap.PlayerCode), lap.SessionInfo?.Kind);
}
