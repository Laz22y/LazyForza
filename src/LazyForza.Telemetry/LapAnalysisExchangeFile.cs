using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Telemetry;

public sealed record LapAnalysisExchangePackage(
    DateTimeOffset CreatedAt,
    Guid TrackId,
    string TrackName,
    int Direction,
    int SectorSchemaVersion,
    string? ExportedByPlayerCode,
    IReadOnlyList<LapRecord> Laps);

public static class LapAnalysisExchangeFile
{
    private const ushort ContainerVersion = 1;
    private const int PayloadSchemaVersion = 1;
    private const int MaximumLapCount = 4;
    private const int MaximumCompressedPayloadBytes = 64 * 1024 * 1024;
    private const int MaximumUncompressedPayloadBytes = 128 * 1024 * 1024;
    private static readonly byte[] Magic = "LFZL"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static async Task WriteAsync(
        string path,
        Guid trackId,
        string trackName,
        int direction,
        int sectorSchemaVersion,
        string? exportedByPlayerCode,
        IReadOnlyList<LapRecord> laps,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(laps);
        var exportedBy = NullIfWhiteSpace(PlayerIdentitySettings.Normalize(exportedByPlayerCode));
        var normalizedLaps = laps
            .Select(lap => lap with
            {
                PlayerCode = NullIfWhiteSpace(PlayerIdentitySettings.Normalize(
                    lap.PlayerCode ?? exportedBy))
            })
            .ToArray();
        Validate(trackId, direction, sectorSchemaVersion, normalizedLaps);

        var payload = new LapAnalysisPayload(
            PayloadSchemaVersion,
            DateTimeOffset.UtcNow,
            trackId,
            string.IsNullOrWhiteSpace(trackName) ? "未知赛道" : trackName.Trim(),
            direction,
            sectorSchemaVersion,
            exportedBy,
            normalizedLaps);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
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
            throw new InvalidDataException("圈速分析文件超过大小限制。");

        var checksum = SHA256.HashData(compressedPayload);
        var header = new byte[10 + checksum.Length];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), ContainerVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6), compressedPayload.Length);
        checksum.CopyTo(header, 10);

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

    public static async Task<LapAnalysisExchangePackage> ReadAsync(
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
        var header = new byte[42];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("这不是有效的 LazyForza 圈速分析文件。");
        var containerVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (containerVersion != ContainerVersion)
            throw new InvalidDataException($"不支持的圈速分析文件版本：{containerVersion}。");
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(6));
        if (payloadLength <= 0 || payloadLength > MaximumCompressedPayloadBytes)
            throw new InvalidDataException("圈速分析文件长度无效。");
        if (stream.Length - stream.Position != payloadLength)
            throw new InvalidDataException("圈速分析文件不完整或包含多余数据。");

        var compressedPayload = new byte[payloadLength];
        await stream.ReadExactlyAsync(compressedPayload, cancellationToken).ConfigureAwait(false);
        var actualChecksum = SHA256.HashData(compressedPayload);
        if (!CryptographicOperations.FixedTimeEquals(header.AsSpan(10, 32), actualChecksum))
            throw new InvalidDataException("圈速分析文件校验失败。");

        await using var compressed = new MemoryStream(compressedPayload, writable: false);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        await using var payloadStream = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (payloadStream.Length + read > MaximumUncompressedPayloadBytes)
                throw new InvalidDataException("圈速分析文件解压后超过大小限制。");
            await payloadStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var payload = JsonSerializer.Deserialize<LapAnalysisPayload>(
                          payloadStream.ToArray(),
                          JsonOptions) ??
                      throw new InvalidDataException("圈速分析文件缺少数据。");
        if (payload.SchemaVersion != PayloadSchemaVersion)
            throw new InvalidDataException($"不支持的圈速分析数据版本：{payload.SchemaVersion}。");
        var exportedBy = NullIfWhiteSpace(PlayerIdentitySettings.Normalize(payload.ExportedByPlayerCode));
        var laps = payload.Laps
            .Select(lap => lap with
            {
                PlayerCode = NullIfWhiteSpace(PlayerIdentitySettings.Normalize(
                    lap.PlayerCode ?? exportedBy))
            })
            .ToArray();
        Validate(payload.TrackId, payload.Direction, payload.SectorSchemaVersion, laps);
        return new LapAnalysisExchangePackage(
            payload.CreatedAt,
            payload.TrackId,
            string.IsNullOrWhiteSpace(payload.TrackName) ? "未知赛道" : payload.TrackName.Trim(),
            payload.Direction,
            payload.SectorSchemaVersion,
            exportedBy,
            laps);
    }

    private static void Validate(
        Guid trackId,
        int direction,
        int sectorSchemaVersion,
        IReadOnlyList<LapRecord> laps)
    {
        if (trackId == Guid.Empty)
            throw new InvalidDataException("圈速分析文件缺少赛道标识。");
        if (laps.Count is < 1 or > MaximumLapCount)
            throw new InvalidDataException($"圈速分析文件必须包含 1–{MaximumLapCount} 圈。");
        if (laps.Select(lap => lap.Id).Distinct().Count() != laps.Count)
            throw new InvalidDataException("圈速分析文件包含重复圈速。");
        foreach (var lap in laps)
        {
            if (lap.TrackId != trackId || lap.Direction != direction ||
                lap.SectorSchemaVersion != sectorSchemaVersion)
                throw new InvalidDataException("圈速与文件中的赛道标识不一致。");
            if (!double.IsFinite(lap.TotalSeconds) || lap.TotalSeconds <= 0 || lap.Samples.Count < 2)
                throw new InvalidDataException("圈速分析数据无效。");
            if (lap.Segments.Any(segment =>
                    !double.IsFinite(segment.TimeSeconds) || segment.TimeSeconds <= 0) ||
                lap.Samples.Any(sample =>
                    !double.IsFinite(sample.S) ||
                    !double.IsFinite(sample.ElapsedSeconds) ||
                    !double.IsFinite(sample.SpeedMps) ||
                    !double.IsFinite(sample.X) ||
                    !double.IsFinite(sample.Y) ||
                    !double.IsFinite(sample.Z)))
                throw new InvalidDataException("圈速分析文件包含无效遥测值。");
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record LapAnalysisPayload(
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        Guid TrackId,
        string TrackName,
        int Direction,
        int SectorSchemaVersion,
        string? ExportedByPlayerCode,
        IReadOnlyList<LapRecord> Laps);
}
