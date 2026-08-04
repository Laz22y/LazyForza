using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Storage;

public sealed record EstateTrackPackageManifest(
    string Format,
    int FormatVersion,
    string ApplicationVersion,
    DateTimeOffset ExportedAt,
    Guid TrackId,
    string TrackName,
    string MapRevision,
    string PayloadSha256);

public sealed record EstateTrackPackagePreview(
    EstateTrackPackageManifest Manifest,
    TrackTemplate Track,
    IReadOnlyList<SectorDefinition> Sectors,
    EstateTrackDefinition Definition);

public sealed record EstateTrackImportResult(
    Guid TrackId,
    string TrackName,
    bool Imported,
    bool AlreadyExists);

public sealed record EstateTrackPackageIdentity(
    Guid TrackId,
    string TrackName,
    string MapRevision,
    string PayloadSha256,
    int SectorCount);

/// <summary>
/// Portable estate-circuit geometry. Lap records and other user data are
/// deliberately excluded so the package can become the shared track identity
/// for future race-control sessions.
/// </summary>
public sealed class EstateTrackPackageService
{
    public const string FileExtension = ".lfzestate";
    public const string PackageFormat = "lazyforza-estate-track";
    public const int CurrentFormatVersion = 1;
    private const long MaximumPackageBytes = 64L * 1024 * 1024;
    private const long MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPayloadBytes = 48L * 1024 * 1024;
    private const string ManifestEntryName = "manifest.json";
    private const string PayloadEntryName = "track.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LazyForzaStore store;
    private readonly string applicationVersion;

    public EstateTrackPackageService(LazyForzaStore store, string applicationVersion)
    {
        this.store = store;
        this.applicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? "unknown"
            : applicationVersion;
    }

    public EstateTrackPackageManifest Export(
        Guid trackId,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = store.LoadTrack(trackId) ??
                     throw new InvalidOperationException("没有找到要导出的地产环道。");
        var definition = store.LoadEstateTrackDefinition(trackId) ??
                         throw new InvalidOperationException("所选赛道缺少地产环道定义，不能导出。");
        var payload = new EstateTrackPackagePayload(loaded.Track, loaded.Sectors, definition);
        ValidatePayload(payload);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var manifest = new EstateTrackPackageManifest(
            PackageFormat,
            CurrentFormatVersion,
            applicationVersion,
            DateTimeOffset.UtcNow,
            loaded.Track.Id,
            loaded.Track.Name,
            definition.MapRevision,
            Convert.ToHexString(SHA256.HashData(payloadBytes)));

        var fullTargetPath = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(fullTargetPath) ??
                              throw new InvalidOperationException("导出路径无效。");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(targetDirectory, $".{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteJsonEntry(archive, ManifestEntryName, manifest);
                var entry = archive.CreateEntry(PayloadEntryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(payloadBytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullTargetPath, true);
            return manifest;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public EstateTrackPackageIdentity Identify(
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = store.LoadTrack(trackId) ??
                     throw new InvalidOperationException("没有找到要识别的地产环道。");
        var definition = store.LoadEstateTrackDefinition(trackId) ??
                         throw new InvalidOperationException("所选赛道缺少地产环道定义。");
        var payload = new EstateTrackPackagePayload(loaded.Track, loaded.Sectors, definition);
        ValidatePayload(payload);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return new EstateTrackPackageIdentity(
            loaded.Track.Id,
            loaded.Track.Name,
            definition.MapRevision,
            Convert.ToHexString(SHA256.HashData(payloadBytes)),
            loaded.Sectors.Count);
    }

    public EstateTrackPackagePreview Preview(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var archive = ReadArchive(packagePath, cancellationToken);
        return new EstateTrackPackagePreview(
            archive.Manifest,
            archive.Payload.Track,
            archive.Payload.Sectors,
            archive.Payload.Definition);
    }

    public EstateTrackImportResult Import(
        string packagePath,
        string targetSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSource);
        var archive = ReadArchive(packagePath, cancellationToken);
        if (store.LoadTrack(archive.Payload.Track.Id) is not null)
        {
            return new EstateTrackImportResult(
                archive.Payload.Track.Id,
                archive.Payload.Track.Name,
                Imported: false,
                AlreadyExists: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var track = archive.Payload.Track with
        {
            Source = targetSource,
            CatalogKind = TrackCatalogKind.UserCustom,
            TimingKind = TrackTimingKind.EstateGeometry,
            LayoutKind = TrackLayoutKind.Circuit
        };
        store.SaveTrack(track, archive.Payload.Sectors, archive.Payload.Definition);
        return new EstateTrackImportResult(track.Id, track.Name, Imported: true, AlreadyExists: false);
    }

    private static EstateTrackArchive ReadArchive(string packagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(packagePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("没有找到地产环道文件。", fullPath);
        if (file.Length <= 0 || file.Length > MaximumPackageBytes)
            throw new InvalidDataException("地产环道文件为空或超过 64 MiB 限制。");

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.ToArray();
        if (entries.Length != 2 ||
            entries.Count(entry => entry.FullName == ManifestEntryName) != 1 ||
            entries.Count(entry => entry.FullName == PayloadEntryName) != 1)
            throw new InvalidDataException("地产环道文件结构不正确，只应包含 manifest.json 和 track.json。");

        var manifestEntry = entries.Single(entry => entry.FullName == ManifestEntryName);
        var payloadEntry = entries.Single(entry => entry.FullName == PayloadEntryName);
        var manifestBytes = ReadEntry(manifestEntry, MaximumManifestBytes, cancellationToken);
        var payloadBytes = ReadEntry(payloadEntry, MaximumPayloadBytes, cancellationToken);
        EstateTrackPackageManifest manifest;
        EstateTrackPackagePayload payload;
        try
        {
            manifest = JsonSerializer.Deserialize<EstateTrackPackageManifest>(manifestBytes, JsonOptions) ??
                       throw new InvalidDataException("地产环道清单为空。");
            payload = JsonSerializer.Deserialize<EstateTrackPackagePayload>(payloadBytes, JsonOptions) ??
                      throw new InvalidDataException("地产环道数据为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("地产环道文件中的 JSON 无法读取。", exception);
        }

        if (!string.Equals(manifest.Format, PackageFormat, StringComparison.Ordinal) ||
            manifest.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException("这不是当前版本支持的 LazyForza 地产环道文件。");
        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(manifest.PayloadSha256); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("地产环道数据摘要格式无效。", exception);
        }
        if (expectedHash.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(payloadBytes)))
            throw new InvalidDataException("地产环道数据摘要不一致，文件可能已损坏或被修改。");

        ValidatePayload(payload);
        if (manifest.TrackId != payload.Track.Id ||
            !string.Equals(manifest.TrackName, payload.Track.Name, StringComparison.Ordinal) ||
            !string.Equals(manifest.MapRevision, payload.Definition.MapRevision, StringComparison.Ordinal))
            throw new InvalidDataException("地产环道清单与赛道数据不一致。");
        return new EstateTrackArchive(manifest, payload);
    }

    private static void ValidatePayload(EstateTrackPackagePayload payload)
    {
        var track = payload.Track;
        var definition = payload.Definition;
        if (track.Id == Guid.Empty || definition.TrackId != track.Id ||
            payload.Sectors.Any(sector => sector.TrackId != track.Id))
            throw new InvalidDataException("地产环道的赛道标识不一致。");
        if (track.TimingKind != TrackTimingKind.EstateGeometry || track.LayoutKind != TrackLayoutKind.Circuit)
            throw new InvalidDataException("文件不是地产环道赛道。");
        if (string.IsNullOrWhiteSpace(track.Name) || string.IsNullOrWhiteSpace(definition.MapName) ||
            string.IsNullOrWhiteSpace(definition.MapRevision))
            throw new InvalidDataException("地产环道缺少名称或地图修订号。");
        if (track.Points.Count < 4 || !IsFinite(track.LengthMeters) || track.LengthMeters <= 0)
            throw new InvalidDataException("地产环道路线数据不完整。");
        var previousS = double.NegativeInfinity;
        foreach (var point in track.Points)
        {
            if (!IsFinite(point.X, point.Y, point.Z, point.S, point.TangentX, point.TangentZ) || point.S < previousS)
                throw new InvalidDataException("地产环道路线上存在无效坐标或倒序里程。");
            previousS = point.S;
        }
        if (payload.Sectors.Count == 0 || payload.Sectors.Select(sector => sector.Index).Distinct().Count() != payload.Sectors.Count)
            throw new InvalidDataException("地产环道缺少有效分段。");
        var expectedSectorIndex = 0;
        var expectedSectorStart = 0d;
        foreach (var sector in payload.Sectors.OrderBy(sector => sector.Index))
        {
            if (sector.Index != expectedSectorIndex++ || !IsFinite(sector.StartS, sector.EndS) ||
                sector.StartS < 0 || sector.EndS <= sector.StartS || sector.EndS > track.LengthMeters + 1)
                throw new InvalidDataException("地产环道分段范围无效。");
            if (Math.Abs(sector.StartS - expectedSectorStart) > 0.01)
                throw new InvalidDataException("地产环道分段之间存在空缺或重叠。");
            expectedSectorStart = sector.EndS;
        }
        if (Math.Abs(expectedSectorStart - track.LengthMeters) > 0.01)
            throw new InvalidDataException("地产环道分段没有覆盖完整路线。");
        ValidateGate(definition.StartFinishGate, "终点门");
        if (definition.Checkpoints.Count == 0)
            throw new InvalidDataException("地产环道缺少检查点。");
        var checkpointIndex = 0;
        var checkpointProgress = double.NegativeInfinity;
        foreach (var checkpoint in definition.Checkpoints.OrderBy(checkpoint => checkpoint.Index))
        {
            if (checkpoint.Index != checkpointIndex++ ||
                !IsFinite(checkpoint.RouteProgressMeters) ||
                checkpoint.RouteProgressMeters < checkpointProgress ||
                checkpoint.RouteProgressMeters < 0 || checkpoint.RouteProgressMeters > track.LengthMeters)
                throw new InvalidDataException("地产环道检查点顺序无效。");
            ValidateGate(checkpoint.Gate, "检查点");
            checkpointProgress = checkpoint.RouteProgressMeters;
        }
        if (definition.Pit is { } pit)
        {
            ValidateGate(pit.EntryGate, "维修区入口");
            ValidateGate(pit.ExitGate, "维修区出口");
            if (pit.CenterLine.Count < 2 ||
                pit.CenterLine.Any(point => !IsFinite(point.X, point.Y, point.Z)) ||
                !IsFinite(pit.ServiceCenter.X, pit.ServiceCenter.Y, pit.ServiceCenter.Z,
                    pit.ServiceRadiusMeters, pit.SpeedLimitKph, pit.MinimumServiceSeconds) ||
                pit.ServiceRadiusMeters <= 0 || pit.SpeedLimitKph <= 0 || pit.MinimumServiceSeconds < 0)
                throw new InvalidDataException("地产环道维修区定义无效。");
        }
    }

    private static void ValidateGate(EstateTimingGate gate, string name)
    {
        if (!IsFinite(
                gate.Left.X, gate.Left.Y, gate.Left.Z,
                gate.Right.X, gate.Right.Y, gate.Right.Z,
                gate.ForwardX, gate.ForwardZ,
                gate.FitRmsMeters, gate.TraceOffsetMeters, gate.TraceAngleDifferenceDegrees,
                gate.HeightToleranceMeters, gate.EndpointMarginMeters) ||
            !gate.HasDirection || gate.HeightToleranceMeters <= 0 || gate.EndpointMarginMeters < 0)
            throw new InvalidDataException($"地产环道的{name}定义无效。");
        var dx = gate.Right.X - gate.Left.X;
        var dz = gate.Right.Z - gate.Left.Z;
        if (dx * dx + dz * dz < 1)
            throw new InvalidDataException($"地产环道的{name}宽度无效。");
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long maximumBytes, CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException($"{entry.FullName} 超过大小限制。");
        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"{entry.FullName} 解压后超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static bool IsFinite(params double[] values) => values.All(double.IsFinite);

    private sealed record EstateTrackPackagePayload(
        TrackTemplate Track,
        IReadOnlyList<SectorDefinition> Sectors,
        EstateTrackDefinition Definition);

    private sealed record EstateTrackArchive(
        EstateTrackPackageManifest Manifest,
        EstateTrackPackagePayload Payload);
}
