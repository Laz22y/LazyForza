using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LazyForza.EstatePeer;

public sealed record PeerEndpoint(IPAddress Address, int Port)
{
    public Uri HttpsUri => new UriBuilder("https", Address.ToString(), Port).Uri;
}

/// <summary>Self-contained addressing; no resolver, directory, or external discovery service.</summary>
public sealed class PeerInvitation
{
    public const string Prefix = "LFZP2-";
    private const string LegacyPrefix = "LFZP1-";
    public const int MaximumCandidates = 8;
    public const int MaximumCodeLength = 1024;
    public required Guid RoomId { get; init; }
    public required int Generation { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string PublicKeySha256 { get; init; }
    public required IReadOnlyList<PeerEndpoint> Candidates { get; init; }
    public bool SupportsUdp { get; init; }
    public string RoomLabel => RoomId.ToString("N")[..8].ToUpperInvariant();
    public string Header => $"{RoomId:N}:{Generation}";

    public string Encode()
    {
        Validate();
        return PeerCode.Encode(Prefix, writer =>
        {
            writer.Write(RoomId.ToByteArray());
            writer.Write7BitEncodedInt(Generation);
            writer.Write(checked((uint)ExpiresAt.ToUnixTimeSeconds()));
            writer.Write(Convert.FromHexString(PublicKeySha256));
            writer.Write((byte)(SupportsUdp ? 1 : 0));
            PeerCode.WriteEndpoints(writer, Candidates);
        });
    }

    private static PeerInvitation ParseCompact(string code, DateTimeOffset? now)
    {
        using var reader = PeerCode.Decode(code, Prefix);
        var room = new Guid(reader.ReadBytes(16));
        var generation = reader.Read7BitEncodedInt();
        var expires = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32());
        var fingerprint = Convert.ToHexString(reader.ReadBytes(32));
        var flags = reader.ReadByte();
        if (flags > 1) throw InvalidCode();
        var candidates = PeerCode.ReadEndpoints(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw InvalidCode();
        var invitation = new PeerInvitation { RoomId = room, Generation = generation, ExpiresAt = expires,
            PublicKeySha256 = fingerprint, SupportsUdp = flags == 1, Candidates = candidates };
        invitation.Validate();
        if (expires <= (now ?? DateTimeOffset.UtcNow)) throw new InvalidDataException("邀请代码已过期，请房主重新分享。");
        return invitation;
    }

    public static PeerInvitation Parse(string code, DateTimeOffset? now = null)
    {
        if (code is null || code.Length > MaximumCodeLength) throw InvalidCode();
        code = code.Trim();
        try
        {
            if (code.StartsWith(Prefix, StringComparison.Ordinal)) return ParseCompact(code, now);
            if (!code.StartsWith(LegacyPrefix, StringComparison.Ordinal)) throw InvalidCode();
            var encoded = code[LegacyPrefix.Length..];
            if (encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw InvalidCode();
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
            if (bytes.Length < 76 || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes.AsSpan(0, bytes.Length - 8)).AsSpan(0, 8), bytes.AsSpan(bytes.Length - 8))) throw InvalidCode();
            using var stream = new MemoryStream(bytes, 0, bytes.Length - 8, writable: false);
            using var reader = new BinaryReader(stream);
            var room = new Guid(reader.ReadBytes(16));
            var generation = reader.ReadInt32();
            var expires = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            var fingerprint = Convert.ToHexString(reader.ReadBytes(32));
            var count = reader.ReadByte();
            if (count is < 1 or > MaximumCandidates) throw InvalidCode();
            var candidates = new List<PeerEndpoint>();
            for (var i = 0; i < count; i++)
            {
                var length = reader.ReadByte();
                if (length is not (4 or 16)) throw InvalidCode();
                candidates.Add(new PeerEndpoint(new IPAddress(reader.ReadBytes(length)), reader.ReadUInt16()));
            }
            if (stream.Position != stream.Length) throw InvalidCode();
            var invitation = new PeerInvitation
            {
                RoomId = room, Generation = generation, ExpiresAt = expires,
                PublicKeySha256 = fingerprint, Candidates = candidates
            };
            invitation.Validate();
            if (expires <= (now ?? DateTimeOffset.UtcNow)) throw new InvalidDataException("邀请代码已过期，请房主重新分享。");
            return invitation;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or EndOfStreamException or OverflowException)
        {
            throw InvalidCode();
        }
    }

    private void Validate()
    {
        if (RoomId == Guid.Empty || Generation < 1 || PublicKeySha256 is not { Length: 64 } ||
            !PublicKeySha256.All(char.IsAsciiHexDigit) || Candidates.Count is < 1 or > MaximumCandidates ||
            Candidates.Distinct().Count() != Candidates.Count ||
            Candidates.Any(endpoint => endpoint.Port is < 1 or > 65535 || !IsAllowedAddress(endpoint.Address))) throw InvalidCode();
    }

    public static bool IsAllowedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6 || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] is > 0 and < 224 &&
                !address.Equals(IPAddress.Broadcast) && !(bytes[0] == 169 && bytes[1] == 254),
            AddressFamily.InterNetworkV6 => (bytes[0] is >= 0x20 and <= 0x3f or 0xfc or 0xfd) && address.ScopeId == 0,
            _ => false
        };
    }

    private static InvalidDataException InvalidCode() => new("邀请代码无效或损坏，请复制完整代码。");
}
