using System.Security.Cryptography;

namespace LazyForza.EstatePeer;

[Flags]
public enum PeerReceiptTransport : byte { Udp = 1, ReverseTcp = 2 }

/// <summary>A short-lived, one-room connection admission ticket exchanged directly between the two people.</summary>
public sealed record PeerReceipt(Guid RoomId, int Generation, DateTimeOffset ExpiresAt, string Nonce,
    IReadOnlyList<PeerEndpoint> Candidates)
{
    public const string Prefix = "LFZR1-";
    private const string AuthenticatedPrefix = "LFZR2-";
    public bool Authenticated { get; init; }
    public PeerReceiptTransport Transports { get; init; } = PeerReceiptTransport.Udp;
    public static PeerReceipt Create(PeerInvitation invitation, IReadOnlyList<PeerEndpoint> candidates,
        PeerReceiptTransport transports = PeerReceiptTransport.Udp) =>
        new(invitation.RoomId, invitation.Generation, DateTimeOffset.UtcNow.AddMinutes(10),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), candidates)
        { Authenticated = invitation.SupportsAssistedConnection, Transports = transports };

    public string Encode() => PeerCode.Encode(Authenticated ? AuthenticatedPrefix : Prefix, writer =>
    {
        writer.Write(RoomId.ToByteArray());
        writer.Write7BitEncodedInt(Generation);
        writer.Write(checked((uint)ExpiresAt.ToUnixTimeSeconds()));
        writer.Write(Convert.FromHexString(Nonce));
        if (Authenticated) writer.Write((byte)Transports);
        PeerCode.WriteEndpoints(writer, Candidates);
    });

    public static PeerReceipt Parse(string code, PeerInvitation invitation, DateTimeOffset? now = null)
    {
        try
        {
            var authenticated = code.Trim().StartsWith(AuthenticatedPrefix, StringComparison.Ordinal);
            using var reader = PeerCode.Decode(code.Trim(), authenticated ? AuthenticatedPrefix : Prefix);
            var room = new Guid(reader.ReadBytes(16));
            var generation = reader.Read7BitEncodedInt();
            var expires = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32());
            var nonce = Convert.ToHexString(reader.ReadBytes(16));
            var transports = authenticated ? (PeerReceiptTransport)reader.ReadByte() : PeerReceiptTransport.Udp;
            var receipt = new PeerReceipt(room, generation, expires, nonce, PeerCode.ReadEndpoints(reader))
            { Authenticated = authenticated, Transports = transports };
            var time = now ?? DateTimeOffset.UtcNow;
            if (reader.BaseStream.Position != reader.BaseStream.Length || receipt.RoomId != invitation.RoomId ||
                receipt.Generation != invitation.Generation || receipt.ExpiresAt <= time || receipt.ExpiresAt > time.AddMinutes(11) ||
                invitation.ExpiresAt <= time || receipt.Nonce.Length != 32 ||
                transports is < PeerReceiptTransport.Udp or > (PeerReceiptTransport.Udp | PeerReceiptTransport.ReverseTcp) ||
                authenticated && !invitation.SupportsAssistedConnection) throw PeerCode.Invalid();
            return receipt;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or EndOfStreamException or OverflowException)
        { throw PeerCode.Invalid(); }
    }
}
