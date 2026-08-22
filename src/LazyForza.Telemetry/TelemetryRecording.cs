using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Telemetry;

public enum TelemetryRecordingContentKind
{
    RawFrames,
    SingleLap
}

public sealed record RecordingMetadata(
    string Product,
    int FormatVersion,
    TelemetrySourceKind Source,
    DateTimeOffset CreatedAt,
    string Note,
    TelemetryRecordingContentKind ContentKind = TelemetryRecordingContentKind.RawFrames,
    string? TrackName = null,
    string? PlayerCode = null);

public sealed record SingleLapTelemetryRecording(
    RecordingMetadata Metadata,
    string TrackName,
    LapRecord Lap);

public sealed class TelemetryRecordingWriter : IAsyncDisposable
{
    private static readonly byte[] Magic = "LFZT"u8.ToArray();
    private readonly FileStream stream;
    private readonly byte[] recordHeader = new byte[10];
    private bool disposed;

    private TelemetryRecordingWriter(FileStream stream, long initialBytes)
    {
        this.stream = stream;
        BytesWritten = initialBytes;
    }

    public long BytesWritten { get; private set; }

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
        return new TelemetryRecordingWriter(stream, header.Length + metadataBytes.Length);
    }

    public async ValueTask WriteAsync(TelemetryFrame frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.RawPacket.Length != Fh6PacketParser.PacketLength)
        {
            throw new InvalidDataException("Only complete 324-byte FH6 packets can be recorded.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(recordHeader, frame.ArrivalTime.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt16LittleEndian(recordHeader.AsSpan(8), Fh6PacketParser.PacketLength);
        await stream.WriteAsync(recordHeader, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame.RawPacket, cancellationToken).ConfigureAwait(false);
        BytesWritten += recordHeader.Length + frame.RawPacket.Length;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await stream.FlushAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}

public static class TelemetryRecordingReader
{
    private static readonly byte[] Magic = "LFZT"u8.ToArray();

    public static async Task<RecordingMetadata> ReadAsync(
        string path,
        Func<TelemetryFrame, ValueTask> onFrame,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(onFrame);
        var parser = new Fh6PacketParser();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported or corrupt .lfztelemetry header.");
        var containerVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (containerVersion == SingleLapTelemetryRecordingFile.ContainerVersion)
            throw new InvalidDataException(
                "This .lfztelemetry file contains a single-lap analysis export. Open it in the LazyForza Replay Workbench.");
        if (containerVersion != 1)
            throw new InvalidDataException($"Unsupported .lfztelemetry container version {containerVersion}.");

        var metadataLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var metadataBytes = new byte[metadataLength];
        await stream.ReadExactlyAsync(metadataBytes, cancellationToken).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(metadataBytes) ??
                       throw new InvalidDataException("Missing recording metadata.");
        if (metadata.ContentKind != TelemetryRecordingContentKind.RawFrames)
            throw new InvalidDataException("The recording does not contain raw 324-byte telemetry frames.");

        long sequence = 0;
        var recordHeader = new byte[10];
        while (stream.Position < stream.Length)
        {
            await stream.ReadExactlyAsync(recordHeader, cancellationToken).ConfigureAwait(false);
            var arrival = BinaryPrimitives.ReadInt64LittleEndian(recordHeader);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(recordHeader.AsSpan(8));
            if (length != Fh6PacketParser.PacketLength)
                throw new InvalidDataException($"Replay record length {length} is not 324.");

            var packet = new byte[length];
            await stream.ReadExactlyAsync(packet, cancellationToken).ConfigureAwait(false);
            if (!parser.TryParse(
                    packet,
                    sequence++,
                    DateTimeOffset.FromUnixTimeMilliseconds(arrival),
                    TelemetrySourceKind.Replay,
                    out var frame,
                    out var error))
                throw new InvalidDataException(error ?? "Invalid replay packet.");
            await onFrame(frame!).ConfigureAwait(false);
        }

        return metadata;
    }
}

public static class SingleLapTelemetryRecordingFile
{
    internal const ushort ContainerVersion = 2;
    private const int PayloadSchemaVersion = 1;
    private const int MaximumCompressedPayloadBytes = 128 * 1024 * 1024;
    private const int MaximumUncompressedPayloadBytes = 64 * 1024 * 1024;
    private static readonly byte[] Magic = "LFZT"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static async Task WriteAsync(
        string path,
        string trackName,
        LapRecord lap,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lap);
        ValidateLap(lap);
        var normalizedTrackName = string.IsNullOrWhiteSpace(trackName)
            ? "未知赛道"
            : trackName.Trim();
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            new SingleLapPayload(PayloadSchemaVersion, normalizedTrackName, lap),
            JsonOptions);
        byte[] compressedPayload;
        await using (var compressed = new MemoryStream())
        {
            await using (var gzip = new GZipStream(
                             compressed,
                             CompressionLevel.SmallestSize,
                             leaveOpen: true))
            {
                await gzip.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
            }
            compressedPayload = compressed.ToArray();
        }
        if (compressedPayload.Length > MaximumCompressedPayloadBytes)
            throw new InvalidDataException("The single-lap telemetry payload is too large.");

        var metadata = new RecordingMetadata(
            "LazyForza",
            ContainerVersion,
            TelemetrySourceKind.Replay,
            DateTimeOffset.UtcNow,
            "Single-lap analysis export; no raw FH6 packets are fabricated.",
            TelemetryRecordingContentKind.SingleLap,
            normalizedTrackName,
            PlayerIdentitySettings.Normalize(lap.PlayerCode) is { Length: > 0 } playerCode
                ? playerCode
                : null);
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        if (metadataBytes.Length > ushort.MaxValue)
            throw new InvalidDataException("Recording metadata is too large.");
        var checksum = SHA256.HashData(compressedPayload);
        var header = new byte[8];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)metadataBytes.Length);
        var payloadHeader = new byte[4 + checksum.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payloadHeader, compressedPayload.Length);
        checksum.CopyTo(payloadHeader, 4);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(metadataBytes, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payloadHeader, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(compressedPayload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public static async Task<SingleLapTelemetryRecording?> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported or corrupt .lfztelemetry header.");
        var containerVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (containerVersion == 1) return null;
        if (containerVersion != ContainerVersion)
            throw new InvalidDataException($"Unsupported .lfztelemetry container version {containerVersion}.");

        var metadataLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var metadataBytes = new byte[metadataLength];
        await stream.ReadExactlyAsync(metadataBytes, cancellationToken).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(metadataBytes, JsonOptions) ??
                       throw new InvalidDataException("Missing recording metadata.");
        if (metadata.ContentKind != TelemetryRecordingContentKind.SingleLap)
            throw new InvalidDataException("The version 2 recording is not a single-lap analysis export.");

        var payloadHeader = new byte[36];
        await stream.ReadExactlyAsync(payloadHeader, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(payloadHeader);
        if (payloadLength <= 0 || payloadLength > MaximumCompressedPayloadBytes)
            throw new InvalidDataException("The single-lap telemetry payload length is invalid.");
        if (stream.Length - stream.Position != payloadLength)
            throw new InvalidDataException("The single-lap telemetry payload is truncated or has trailing data.");
        var expectedChecksum = payloadHeader.AsSpan(4, 32).ToArray();
        var compressedPayload = new byte[payloadLength];
        await stream.ReadExactlyAsync(compressedPayload, cancellationToken).ConfigureAwait(false);
        var actualChecksum = SHA256.HashData(compressedPayload);
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, actualChecksum))
            throw new InvalidDataException("The single-lap telemetry payload checksum does not match.");

        await using var compressed = new MemoryStream(compressedPayload, writable: false);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        await using var payloadStream = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (payloadStream.Length + read > MaximumUncompressedPayloadBytes)
                throw new InvalidDataException("The expanded single-lap telemetry payload is too large.");
            await payloadStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var payload = JsonSerializer.Deserialize<SingleLapPayload>(
                          payloadStream.ToArray(),
                          JsonOptions) ??
                      throw new InvalidDataException("The single-lap telemetry payload is missing.");
        if (payload.SchemaVersion != PayloadSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported single-lap payload schema version {payload.SchemaVersion}.");
        ValidateLap(payload.Lap);
        var resolvedTrackName = string.IsNullOrWhiteSpace(payload.TrackName)
            ? metadata.TrackName ?? "未知赛道"
            : payload.TrackName;
        var resolvedPlayerCode = PlayerIdentitySettings.Normalize(
            payload.Lap.PlayerCode ?? metadata.PlayerCode);
        return new SingleLapTelemetryRecording(
            metadata with
            {
                TrackName = resolvedTrackName,
                PlayerCode = resolvedPlayerCode.Length == 0 ? null : resolvedPlayerCode
            },
            resolvedTrackName,
            payload.Lap with
            {
                PlayerCode = resolvedPlayerCode.Length == 0 ? null : resolvedPlayerCode
            });
    }

    private static void ValidateLap(LapRecord lap)
    {
        if (lap.Samples.Count < 2)
            throw new InvalidDataException("A single-lap telemetry export requires at least two samples.");
        if (!double.IsFinite(lap.TotalSeconds) || lap.TotalSeconds <= 0)
            throw new InvalidDataException("The lap duration is invalid.");
        if (lap.Samples.Any(sample =>
                !double.IsFinite(sample.S) ||
                !double.IsFinite(sample.ElapsedSeconds) ||
                !double.IsFinite(sample.SpeedMps) ||
                !double.IsFinite(sample.X) ||
                !double.IsFinite(sample.Y) ||
                !double.IsFinite(sample.Z)))
            throw new InvalidDataException("The lap contains non-finite telemetry values.");
    }

    private sealed record SingleLapPayload(
        int SchemaVersion,
        string TrackName,
        LapRecord Lap);
}

public sealed class TelemetryReplaySource(string path, double speed = 1, bool loop = false) : ITelemetrySource
{
    public TelemetrySourceKind Kind => TelemetrySourceKind.Replay;
    public string Description => $"Replay: {Path.GetFileName(path)}";

    public async Task RunAsync(Func<TelemetryFrame, ValueTask> publish, Action<string> onInvalid, CancellationToken cancellationToken)
    {
        do
        {
            long? previousArrival = null;
            try
            {
                await TelemetryRecordingReader.ReadAsync(
                    path,
                    async frame =>
                    {
                        var arrival = frame.ArrivalTime.ToUnixTimeMilliseconds();
                        if (speed > 0 && previousArrival is long previous && arrival > previous)
                        {
                            var delay = TimeSpan.FromMilliseconds(Math.Min(1000, (arrival - previous) / speed));
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        }

                        previousArrival = arrival;
                        var replayFrame = loop
                            ? frame with { ArrivalTime = DateTimeOffset.UtcNow }
                            : frame;
                        await publish(replayFrame).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidDataException exception)
            {
                onInvalid(exception.Message);
                throw;
            }
        }
        while (loop && !cancellationToken.IsCancellationRequested);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
