using System.Security.Cryptography;

namespace LazyForza.EstatePeer;

/// <summary>A short-lived, one-room UDP admission ticket exchanged directly between the two people.</summary>
public sealed record PeerReceipt(Guid RoomId, int Generation, DateTimeOffset ExpiresAt, string Nonce,
    IReadOnlyList<PeerEndpoint> Candidates)
{
    public const string Prefix = "LFZR1-";
    public static PeerReceipt Create(PeerInvitation invitation, IReadOnlyList<PeerEndpoint> candidates) =>
        new(invitation.RoomId, invitation.Generation, DateTimeOffset.UtcNow.AddMinutes(10),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), candidates);

    public string Encode() => PeerCode.Encode(Prefix, writer =>
    {
        writer.Write(RoomId.ToByteArray());
        writer.Write7BitEncodedInt(Generation);
        writer.Write(checked((uint)ExpiresAt.ToUnixTimeSeconds()));
        writer.Write(Convert.FromHexString(Nonce));
        PeerCode.WriteEndpoints(writer, Candidates);
    });

    public static PeerReceipt Parse(string code, PeerInvitation invitation, DateTimeOffset? now = null)
    {
        try
        {
            using var reader = PeerCode.Decode(code.Trim(), Prefix);
            var receipt = new PeerReceipt(new Guid(reader.ReadBytes(16)), reader.Read7BitEncodedInt(),
                DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32()), Convert.ToHexString(reader.ReadBytes(16)),
                PeerCode.ReadEndpoints(reader));
            var time = now ?? DateTimeOffset.UtcNow;
            if (reader.BaseStream.Position != reader.BaseStream.Length || receipt.RoomId != invitation.RoomId ||
                receipt.Generation != invitation.Generation || receipt.ExpiresAt <= time || receipt.ExpiresAt > time.AddMinutes(11) ||
                invitation.ExpiresAt <= time || receipt.Nonce.Length != 32) throw PeerCode.Invalid();
            return receipt;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or EndOfStreamException or OverflowException)
        { throw PeerCode.Invalid(); }
    }
}
