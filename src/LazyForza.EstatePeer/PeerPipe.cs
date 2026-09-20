using System.Buffers.Binary;
using System.Text.Json;

namespace LazyForza.EstatePeer;

public static class PeerPipe
{
    public const int MaximumFrameBytes = 16 * 1024;
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Invalid local control frame.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty local control frame.");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Local control frame too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
