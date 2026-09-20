using System.Net;
using System.Security.Cryptography;

namespace LazyForza.EstatePeer;

// Checksums detect copying errors; peer identity is always authenticated with the full TLS key pin.
internal static class PeerCode
{
    internal static string Encode(string prefix, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) write(writer);
        stream.Write(SHA256.HashData(stream.ToArray()).AsSpan(0, 8));
        return prefix + Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static BinaryReader Decode(string code, string prefix)
    {
        if (code.Length > PeerInvitation.MaximumCodeLength || !code.StartsWith(prefix, StringComparison.Ordinal)) throw Invalid();
        var encoded = code[prefix.Length..];
        if (encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw Invalid();
        var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
        if (bytes.Length < 9 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes.AsSpan(0, bytes.Length - 8)).AsSpan(0, 8), bytes.AsSpan(bytes.Length - 8))) throw Invalid();
        return new BinaryReader(new MemoryStream(bytes, 0, bytes.Length - 8, false));
    }

    internal static void WriteEndpoints(BinaryWriter writer, IReadOnlyList<PeerEndpoint> endpoints)
    {
        writer.Write((byte)endpoints.Count);
        writer.Write((ushort)endpoints[0].Port);
        byte[] previous = [];
        foreach (var endpoint in endpoints)
        {
            var bytes = endpoint.Address.GetAddressBytes();
            var common = 0;
            if (bytes.Length == previous.Length)
                while (common < Math.Min(15, bytes.Length) && bytes[common] == previous[common]) common++;
            var differentPort = endpoint.Port != endpoints[0].Port;
            writer.Write((byte)(common | (bytes.Length == 16 ? 16 : 0) | (differentPort ? 32 : 0)));
            writer.Write(bytes.AsSpan(common));
            if (differentPort) writer.Write((ushort)endpoint.Port);
            previous = bytes;
        }
    }

    internal static IReadOnlyList<PeerEndpoint> ReadEndpoints(BinaryReader reader)
    {
        var count = reader.ReadByte();
        if (count is < 1 or > PeerInvitation.MaximumCandidates) throw Invalid();
        var port = reader.ReadUInt16();
        var result = new List<PeerEndpoint>();
        byte[] previous = [];
        for (var i = 0; i < count; i++)
        {
            var tag = reader.ReadByte();
            var length = (tag & 16) != 0 ? 16 : 4;
            var common = tag & 15;
            if (tag > 63 || common > length || (common > 0 && previous.Length != length)) throw Invalid();
            var bytes = new byte[length];
            previous.AsSpan(0, common).CopyTo(bytes);
            reader.BaseStream.ReadExactly(bytes.AsSpan(common));
            var endpoint = new PeerEndpoint(new IPAddress(bytes), (tag & 32) != 0 ? reader.ReadUInt16() : port);
            if (endpoint.Port < 1 || !PeerInvitation.IsAllowedAddress(endpoint.Address) || result.Contains(endpoint)) throw Invalid();
            result.Add(endpoint);
            previous = bytes;
        }
        return result;
    }

    internal static InvalidDataException Invalid() => new("连接代码无效或损坏，请复制完整代码。");
}
