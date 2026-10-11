using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LazyForza.EstatePeer;

public enum PeerStage { Parse, Capabilities, Tcp, ReverseTcp, Receipt, Udp, Quic, Identity, Login, Connected, Mapping, Network, Path, Closed }
public enum PeerOutcome { Started, Succeeded, Unavailable, Timeout, Cancelled, Invalid, Unsupported, Expired, Rejected, Failed, Changed }
public sealed record PeerDiagnosticEntry(long Sequence, DateTimeOffset At, Guid Attempt, PeerStage Stage,
    PeerOutcome Outcome, string Family, string AddressClass, double? Milliseconds, int? CodeFormat = null, PeerReceiptTransport? Transports = null);
public sealed record PeerDiagnosticSnapshot(int FormatVersion, long Dropped, IReadOnlyList<PeerDiagnosticEntry> Events, string BuildVersion = "unknown", bool? QuicSupported = null);

/// <summary>Only typed, non-identifying facts enter this buffer. Never pass exception text or codes.</summary>
public static class PeerDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Queue<PeerDiagnosticEntry> Events = new();
    private static readonly Queue<PeerDiagnosticEntry> HostEvents = new();
    private static long sequence, dropped, hostDropped;
    private static string hostBuild = "unknown";
    private static bool? hostQuic;
    private static string SafeVersion(string? version) => version is { Length: > 0 and <= 100 } &&
        version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? version : "unknown";
    private static string BuildVersion => SafeVersion(System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion);
    public static Guid Start(PeerStage stage)
    {
        var id = Guid.NewGuid(); Record(stage, PeerOutcome.Started, id); return id;
    }
    public static void Record(PeerStage stage, PeerOutcome outcome, Guid attempt = default,
        IPAddress? address = null, double? milliseconds = null, int? codeFormat = null, PeerReceiptTransport? transports = null)
    {
        lock (Gate)
        {
            if (Events.Count == 500) { Events.Dequeue(); dropped++; }
            Events.Enqueue(new(++sequence, DateTimeOffset.UtcNow, attempt, stage, outcome,
                address is null ? "unknown" : address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6",
                Classify(address), milliseconds is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 86_400_000) : null, codeFormat is >= 1 and <= 99 ? codeFormat : null, transports));
        }
    }
    public static PeerOutcome Failure(Exception error, bool cancelled = false) => error switch
    {
        OperationCanceledException => cancelled ? PeerOutcome.Cancelled : PeerOutcome.Timeout,
        TimeoutException => PeerOutcome.Timeout,
        IOException { InnerException: TimeoutException } => PeerOutcome.Timeout,
        AuthenticationException => PeerOutcome.Rejected,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => PeerOutcome.Rejected,
        PlatformNotSupportedException => PeerOutcome.Unsupported,
        _ => PeerOutcome.Failed
    };
    public static PeerDiagnosticSnapshot Snapshot(long after = 0, int limit = 500)
    { lock (Gate) return new(1, dropped, Events.Where(e => e.Sequence > after).Take(Math.Clamp(limit, 1, 500)).ToArray(), BuildVersion, OperatingSystem.IsWindows() && PeerQuicHost.IsSupported); }
    public static PeerDiagnosticSnapshot HostSnapshot()
    { lock (Gate) return new(1, hostDropped, HostEvents.ToArray(), hostBuild, hostQuic); }
    public static void ImportHost(PeerDiagnosticSnapshot snapshot)
    {
        lock (Gate)
        {
            if (snapshot.FormatVersion != 1) return;
            hostBuild = SafeVersion(snapshot.BuildVersion); hostQuic = snapshot.QuicSupported;
            hostDropped = Math.Max(hostDropped, snapshot.Dropped);
            foreach (var entry in snapshot.Events.Take(32))
            {
                if (!Enum.IsDefined(entry.Stage) || !Enum.IsDefined(entry.Outcome)) continue;
                if (HostEvents.Count == 500) { HostEvents.Dequeue(); hostDropped++; }
                HostEvents.Enqueue(entry with { CodeFormat = entry.CodeFormat is >= 1 and <= 99 ? entry.CodeFormat : null,
                    Transports = entry.Transports is >= PeerReceiptTransport.Udp and <= (PeerReceiptTransport.Udp | PeerReceiptTransport.ReverseTcp) ? entry.Transports : null, Family = entry.Family is "IPv4" or "IPv6" ? entry.Family : "unknown",
                    AddressClass = entry.AddressClass is "local" or "private" or "shared" or "global" ? entry.AddressClass : "unknown",
                    Milliseconds = entry.Milliseconds is double ms && double.IsFinite(ms) ? Math.Clamp(ms, 0, 86_400_000) : null });
            }
        }
    }
    internal static string Classify(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (IPAddress.IsLoopback(address)) return "local";
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal ? "private" : "global";
        if (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168) return "private";
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return "shared";
        return "global";
    }
}
