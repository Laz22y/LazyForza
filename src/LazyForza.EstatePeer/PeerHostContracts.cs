namespace LazyForza.EstatePeer;

// Local control contract. Never exposed on HTTP/WebSocket or placed in process arguments.
public sealed record PeerHostStart(
    string DataDirectory,
    string RoomName,
    string Password,
    int Port,
    string? ExternalAddress,
    int? ExternalPort,
    string TrackId,
    string TrackName,
    string TrackRevision,
    string TrackPackageHash,
    string TrackPackagePath,
    int SectorCount,
    int RaceLaps,
    bool Resume, bool AutomaticPortMapping = false);

public sealed record PeerHostReply(bool Success, string? Error = null, string? Invitation = null, int Port = 0,
    string? ControlUrl = null, string? ControlPassword = null, int ConnectivityVersion = 0,
    PeerDiagnosticSnapshot? Diagnostics = null, int MappedEndpoints = 0);
public sealed record PeerHostCommand(string Action, string? Value = null, bool Force = false);
