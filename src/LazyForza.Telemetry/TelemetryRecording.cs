using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Telemetry;

public sealed record RecordingMetadata(string Product, int FormatVersion, TelemetrySourceKind Source, DateTimeOffset CreatedAt, string Note);

public sealed class TelemetryRecordingWriter : IAsyncDisposable
{
    private static readonly byte[] Magic = "LFZT"u8.ToArray();
    private readonly FileStream stream;
    private bool disposed;

    private TelemetryRecordingWriter(FileStream stream) => this.stream = stream;

    public static async Task<TelemetryRecordingWriter> CreateAsync(string path, RecordingMetadata metadata, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        if (metadataBytes.Length > ushort.MaxValue)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("Recording metadata is too large.");
        }

        var header = new byte[8];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)metadataBytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(metadataBytes, cancellationToken).ConfigureAwait(false);
        return new TelemetryRecordingWriter(stream);
    }

    public async ValueTask WriteAsync(TelemetryFrame frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.RawPacket.Length != Fh6PacketParser.PacketLength)
        {
            throw new InvalidDataException("Only complete 324-byte FH6 packets can be recorded.");
        }

        var recordHeader = new byte[10];
        BinaryPrimitives.WriteInt64LittleEndian(recordHeader, frame.ArrivalTime.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt16LittleEndian(recordHeader.AsSpan(8), Fh6PacketParser.PacketLength);
        await stream.WriteAsync(recordHeader, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame.RawPacket, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await stream.FlushAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class TelemetryReplaySource(string path, double speed = 1, bool loop = false) : ITelemetrySource
{
    private static readonly byte[] Magic = "LFZT"u8.ToArray();
    private readonly Fh6PacketParser parser = new();

    public TelemetrySourceKind Kind => TelemetrySourceKind.Replay;
    public string Description => $"Replay: {Path.GetFileName(path)}";

    public async Task RunAsync(Func<TelemetryFrame, ValueTask> publish, Action<string> onInvalid, CancellationToken cancellationToken)
    {
        long sequence = 0;
        do
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != 1)
            {
                throw new InvalidDataException("Unsupported or corrupt .lfztelemetry header.");
            }

            var metadataLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var metadata = new byte[metadataLength];
            await stream.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
            _ = JsonSerializer.Deserialize<RecordingMetadata>(metadata) ?? throw new InvalidDataException("Missing recording metadata.");

            long? previousArrival = null;
            var recordHeader = new byte[10];
            while (stream.Position < stream.Length)
            {
                await stream.ReadExactlyAsync(recordHeader, cancellationToken).ConfigureAwait(false);
                var arrival = BinaryPrimitives.ReadInt64LittleEndian(recordHeader);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(recordHeader.AsSpan(8));
                if (length != Fh6PacketParser.PacketLength)
                {
                    throw new InvalidDataException($"Replay record length {length} is not 324.");
                }

                var packet = new byte[length];
                await stream.ReadExactlyAsync(packet, cancellationToken).ConfigureAwait(false);
                if (speed > 0 && previousArrival is long previous && arrival > previous)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(1000, (arrival - previous) / speed));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                previousArrival = arrival;
                var frameArrival = loop ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeMilliseconds(arrival);
                if (parser.TryParse(packet, sequence++, frameArrival, Kind, out var frame, out var error))
                {
                    await publish(frame!).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested) return;
                }
                else
                {
                    onInvalid(error ?? "Invalid replay packet.");
                }
            }
        }
        while (loop && !cancellationToken.IsCancellationRequested);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
