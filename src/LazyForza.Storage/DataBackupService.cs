using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LazyForza.Storage;

public sealed record BackupSelection(
    bool Settings = true,
    bool Vehicles = true,
    bool Laps = true,
    bool CustomTracks = true)
{
    public bool HasAny => Settings || Vehicles || Laps || CustomTracks;
}

public enum BackupImportMode
{
    Merge,
    Overwrite
}

public sealed record BackupManifest(
    int FormatVersion,
    string Kind,
    DateTimeOffset CreatedAt,
    string ApplicationVersion,
    int SchemaVersion,
    BackupSelection Selection,
    IReadOnlyDictionary<string, string> Files);

public sealed record BackupConflict(
    string Category,
    string Key,
    string SourceSummary,
    string DestinationSummary);

public sealed record BackupPreview(
    BackupManifest Manifest,
    int Settings,
    int Vehicles,
    int Laps,
    int CustomTracks,
    IReadOnlyList<BackupConflict> Conflicts,
    IReadOnlyList<string> Warnings);

public sealed record BackupImportResult(
    int ImportedSettings,
    int ImportedVehicles,
    int ImportedLaps,
    int ImportedCustomTracks,
    int PreservedConflicts);

public sealed class DataBackupService
{
    public const int CurrentFormatVersion = 1;
    public const int AutomaticBackupRetention = 8;
    private const long MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPortableDataBytes = 1024L * 1024 * 1024;
    private const string PortableKind = "portable";
    private const string SnapshotKind = "database-snapshot";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly TableSpec[] TableSpecs =
    [
        new("AppSettings", ["Key", "Value", "UpdatedAt"], ["Key"]),
        new("ModuleSettings", ["ModuleId", "Key", "Value", "UpdatedAt"], ["ModuleId", "Key"]),
        new("VehicleProfiles",
            ["Id", "CarOrdinal", "CarClass", "PI", "Drivetrain", "Cylinders", "MaxRpm",
                "CurveSignature", "GearSignature", "State", "Confidence", "UpdatedAt",
                "DisplayName", "RecommendationsEnabled"],
            ["Id"]),
        new("EngineCurveBins",
            ["VehicleProfileId", "RpmCenter", "SampleCount", "MedianPower", "MedianTorque",
                "MedianBoost", "Deviation", "Confidence"],
            ["VehicleProfileId", "RpmCenter"]),
        new("GearModels",
            ["VehicleProfileId", "Gear", "Slope", "SampleCount", "Confidence"],
            ["VehicleProfileId", "Gear"]),
        new("ShiftTargets",
            ["VehicleProfileId", "FromGear", "ToGear", "TargetRpm", "CueRpm", "AfterRpm",
                "Confidence", "AlgorithmVersion"],
            ["VehicleProfileId", "FromGear", "ToGear"]),
        new("TrackTemplates",
            ["Id", "Name", "Direction", "Source", "GameBuild", "LengthMeters", "ToleranceMeters",
                "Confidence", "CaptureLapCount", "CreatedAt", "UpdatedAt", "LayoutKind",
                "CatalogKind", "Category"],
            ["Id"]),
        new("TrackPoints",
            ["TrackId", "PointIndex", "X", "Y", "Z", "S", "TangentX", "TangentZ"],
            ["TrackId", "PointIndex"]),
        new("SectorDefinitions",
            ["TrackId", "SectorSchemaVersion", "SectorIndex", "StartS", "EndS", "FeatureType",
                "AlgorithmVersion"],
            ["TrackId", "SectorSchemaVersion", "SectorIndex"]),
        new("Sessions", ["Id", "Source", "StartedAt", "RawRecordingPath"], ["Id"]),
        new("Laps",
            ["Id", "TrackId", "Direction", "SectorSchemaVersion", "SessionId",
                "VehicleFingerprint", "StartedAt", "TotalSeconds", "IsValid", "InvalidReason",
                "CarClass", "PerformanceIndex"],
            ["Id"]),
        new("LapSegments",
            ["LapId", "SectorIndex", "TimeSeconds", "IsValid"],
            ["LapId", "SectorIndex"]),
        new("LapSamples",
            ["LapId", "S", "ElapsedSeconds", "SpeedMps", "Rpm", "Gear", "Accel", "Brake",
                "DeltaSeconds", "X", "Y", "Z"],
            [])
    ];

    private readonly LazyForzaStore store;
    private readonly string applicationVersion;

    public DataBackupService(LazyForzaStore store, string applicationVersion)
    {
        this.store = store;
        this.applicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? "unknown"
            : applicationVersion;
    }

    public BackupManifest Create(
        string targetPath,
        BackupSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!selection.HasAny)
            throw new InvalidOperationException("至少选择一类需要备份的数据。");

        cancellationToken.ThrowIfCancellationRequested();
        var fullTargetPath = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(fullTargetPath)
                              ?? throw new InvalidOperationException("备份路径无效。");
        Directory.CreateDirectory(targetDirectory);

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LazyForza-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryArchive = Path.Combine(temporaryDirectory, "backup.tmp");
        var dataPath = Path.Combine(temporaryDirectory, "data.json");
        try
        {
            var payload = ExportPayload(selection, cancellationToken);
            using (var stream = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, payload, JsonOptions);
            }

            var dataHash = ComputeSha256(dataPath);
            var manifest = new BackupManifest(
                CurrentFormatVersion,
                PortableKind,
                DateTimeOffset.UtcNow,
                applicationVersion,
                store.SchemaVersion,
                selection,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["data.json"] = dataHash
                });

            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(dataPath, "data.json", CompressionLevel.Optimal);
                WriteJsonEntry(archive, "manifest.json", manifest);
            }

            File.Move(temporaryArchive, fullTargetPath, true);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, true);
        }
    }

    public BackupPreview Preview(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var archive = ReadPortableArchive(backupPath, cancellationToken);
        var conflicts = BuildConflicts(archive.Payload);
        var warnings = new List<string>();
        if (archive.Manifest.SchemaVersion > LazyForzaStore.CurrentSchemaVersion)
            warnings.Add("备份来自更高版本的数据库，当前版本不能导入。");
        if (archive.Payload.Table("TrackTemplates").Rows.Any(IsOfficialTrackRow))
            warnings.Add("圈速所依赖的官方赛道定义已随包保存；目标电脑已有的官方赛道不会被覆盖。");

        return new BackupPreview(
            archive.Manifest,
            archive.Payload.Table("AppSettings").Rows.Count +
            archive.Payload.Table("ModuleSettings").Rows.Count,
            archive.Payload.Table("VehicleProfiles").Rows.Count,
            archive.Payload.Table("Laps").Rows.Count,
            archive.Payload.Table("TrackTemplates").Rows.Count(row => !IsOfficialTrackRow(row)),
            conflicts,
            warnings);
    }

    public BackupImportResult Import(
        string backupPath,
        BackupImportMode mode,
        CancellationToken cancellationToken = default)
    {
        var archive = ReadPortableArchive(backupPath, cancellationToken);
        if (archive.Manifest.SchemaVersion > LazyForzaStore.CurrentSchemaVersion)
            throw new InvalidOperationException("该备份由更高版本的 LazyForza 创建，请先升级应用后再导入。");

        var preview = PreviewFromArchive(archive);
        var destination = DestinationKeys();
        var commands = BuildImportCommands(archive.Payload, mode, destination, cancellationToken);
        store.ExecuteBackupTransaction(commands);

        var preserved = mode == BackupImportMode.Merge ? preview.Conflicts.Count : 0;
        return new BackupImportResult(
            preview.Settings - CountConflicts(preview, "配置", mode),
            preview.Vehicles - CountConflicts(preview, "车辆", mode),
            preview.Laps - CountConflicts(preview, "圈速", mode),
            preview.CustomTracks - CountConflicts(preview, "自定义赛道", mode),
            preserved);
    }

    public string CreateAutomaticUpdateBackup(
        string backupDirectory,
        CancellationToken cancellationToken = default) =>
        CreateAutomaticBackup(backupDirectory, "update", cancellationToken);

    public string CreateAutomaticBackup(
        string backupDirectory,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(backupDirectory);
        var safeReason = new string(reason
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .ToArray());
        if (safeReason.Length == 0) safeReason = "operation";
        var path = Path.Combine(
            backupDirectory,
            $"auto-{safeReason}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.lfzbackup");
        Create(path, new BackupSelection(), cancellationToken);
        RotateAutomaticBackups(backupDirectory);
        return path;
    }

    public static string? CreatePreMigrationSnapshotIfNeeded(
        string databasePath,
        string backupDirectory,
        string applicationVersion)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0) return null;

        int schemaVersion;
        using (var database = new WinSqliteDatabase(databasePath))
        {
            var hasSchema = database.QueryText(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='SchemaVersion' LIMIT 1;");
            schemaVersion = hasSchema is null
                ? 0
                : int.TryParse(
                    database.QueryText("SELECT Version FROM SchemaVersion LIMIT 1;"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 0;
            if (schemaVersion >= LazyForzaStore.CurrentSchemaVersion) return null;
            database.Execute("PRAGMA wal_checkpoint(FULL);");
        }

        Directory.CreateDirectory(backupDirectory);
        var targetPath = Path.Combine(
            backupDirectory,
            $"auto-migration-schema-{schemaVersion}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.lfzbackup");
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        var databaseHash = ComputeSha256(databasePath);
        var manifest = new BackupManifest(
            CurrentFormatVersion,
            SnapshotKind,
            DateTimeOffset.UtcNow,
            applicationVersion,
            schemaVersion,
            new BackupSelection(false, false, false, false),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lazyforza.db"] = databaseHash
            });

        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(databasePath, "lazyforza.db", CompressionLevel.Optimal);
            WriteJsonEntry(archive, "manifest.json", manifest);
        }

        File.Move(temporaryPath, targetPath, true);
        RotateAutomaticBackups(backupDirectory);
        return targetPath;
    }

    public static void RotateAutomaticBackups(string backupDirectory)
    {
        if (!Directory.Exists(backupDirectory)) return;
        var automatic = Directory.EnumerateFiles(backupDirectory, "auto-*.lfzbackup")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(AutomaticBackupRetention)
            .ToArray();
        foreach (var file in automatic)
        {
            file.Delete();
        }
    }

    private BackupPayload ExportPayload(
        BackupSelection selection,
        CancellationToken cancellationToken)
    {
        var requests = new List<(string Name, string? Where)>();
        if (selection.Settings)
        {
            requests.Add(("AppSettings", null));
            requests.Add(("ModuleSettings", null));
        }

        if (selection.Vehicles)
        {
            requests.Add(("VehicleProfiles", null));
            requests.Add(("EngineCurveBins", null));
            requests.Add(("GearModels", null));
            requests.Add(("ShiftTargets", null));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var trackClauses = new List<string>();
        if (selection.Laps)
            trackClauses.Add("Id IN (SELECT DISTINCT TrackId FROM Laps)");
        if (selection.CustomTracks)
            trackClauses.Add("CatalogKind='UserCustom'");
        if (trackClauses.Count > 0)
        {
            var trackWhere = string.Join(" OR ", trackClauses.Select(clause => $"({clause})"));
            var childWhere =
                $"TrackId IN (SELECT Id FROM TrackTemplates WHERE {trackWhere})";
            requests.Add(("TrackTemplates", trackWhere));
            requests.Add(("TrackPoints", childWhere));
            requests.Add(("SectorDefinitions", childWhere));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Laps)
        {
            requests.Add(("Sessions", "Id IN (SELECT DISTINCT SessionId FROM Laps)"));
            requests.Add(("Laps", null));
            requests.Add(("LapSegments", null));
            requests.Add(("LapSamples", null));
        }

        var queries = requests.Select(request =>
        {
            var spec = Spec(request.Name);
            return $"SELECT {string.Join(',', spec.Columns)} FROM {spec.Name}" +
                   (string.IsNullOrWhiteSpace(request.Where)
                       ? string.Empty
                       : $" WHERE {request.Where}") +
                   ";";
        }).ToArray();
        var snapshots = store.QueryBackupSnapshot(queries);
        var tables = requests.Select((request, index) =>
        {
            var spec = Spec(request.Name);
            return new BackupTableData(
                spec.Name,
                spec.Columns,
                snapshots[index].Select(row => row.ToArray()).ToList());
        }).ToList();
        return new BackupPayload(tables);
    }

    private PortableArchive ReadPortableArchive(
        string backupPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        using var archive = ZipFile.OpenRead(Path.GetFullPath(backupPath));
        var entries = archive.Entries
            .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (entries.Values.Any(group => group.Length != 1))
            throw new InvalidDataException("备份包包含重复文件。");
        if (!entries.TryGetValue("manifest.json", out var manifestEntries))
            throw new InvalidDataException("备份包缺少 manifest.json。");

        var manifest = ReadJsonEntry<BackupManifest>(manifestEntries[0], MaximumManifestBytes);
        if (manifest.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"不支持的备份格式版本：{manifest.FormatVersion}。");
        if (!string.Equals(manifest.Kind, PortableKind, StringComparison.Ordinal))
            throw new InvalidDataException("这是数据库升级安全快照，不能作为可合并的迁移备份导入。");
        if (!entries.TryGetValue("data.json", out var dataEntries))
            throw new InvalidDataException("备份包缺少 data.json。");
        if (dataEntries[0].Length > MaximumPortableDataBytes)
            throw new InvalidDataException("备份数据超过 1 GB 安全限制。");
        var allowedEntries = manifest.Files.Keys
            .Append("manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        var unexpectedEntry = entries.Keys.FirstOrDefault(name => !allowedEntries.Contains(name));
        if (unexpectedEntry is not null)
            throw new InvalidDataException($"备份包包含未列入清单的文件：{unexpectedEntry}。");

        foreach (var (name, expectedHash) in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(name, out var fileEntries))
                throw new InvalidDataException($"备份包缺少清单文件：{name}。");
            using var entryStream = fileEntries[0].Open();
            var actualHash = ComputeSha256(entryStream);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"备份文件校验失败：{name}。");
        }

        var payload = ReadJsonEntry<BackupPayload>(dataEntries[0], MaximumPortableDataBytes);
        ValidatePayload(payload);
        return new PortableArchive(manifest, payload);
    }

    private BackupPreview PreviewFromArchive(PortableArchive archive)
    {
        var conflicts = BuildConflicts(archive.Payload);
        return new BackupPreview(
            archive.Manifest,
            archive.Payload.Table("AppSettings").Rows.Count +
            archive.Payload.Table("ModuleSettings").Rows.Count,
            archive.Payload.Table("VehicleProfiles").Rows.Count,
            archive.Payload.Table("Laps").Rows.Count,
            archive.Payload.Table("TrackTemplates").Rows.Count(row => !IsOfficialTrackRow(row)),
            conflicts,
            []);
    }

    private IReadOnlyList<BackupConflict> BuildConflicts(BackupPayload payload)
    {
        var destination = DestinationKeys();
        var conflicts = new List<BackupConflict>();
        AddConflicts(
            conflicts,
            payload.Table("AppSettings"),
            "配置",
            row => row[0] ?? string.Empty,
            row => Summarize(row.ElementAtOrDefault(1)),
            destination.AppSettings);
        AddConflicts(
            conflicts,
            payload.Table("ModuleSettings"),
            "配置",
            row => $"{row[0]}/{row[1]}",
            row => Summarize(row.ElementAtOrDefault(2)),
            destination.ModuleSettings);
        AddConflicts(
            conflicts,
            payload.Table("VehicleProfiles"),
            "车辆",
            row => row[0] ?? string.Empty,
            row => string.IsNullOrWhiteSpace(row.ElementAtOrDefault(12))
                ? $"CarOrdinal {row.ElementAtOrDefault(1)} / PI {row.ElementAtOrDefault(3)}"
                : row[12]!,
            destination.Vehicles);
        AddConflicts(
            conflicts,
            payload.Table("TrackTemplates"),
            "自定义赛道",
            row => row[0] ?? string.Empty,
            row => row.ElementAtOrDefault(1) ?? "未命名赛道",
            destination.Tracks,
            row => !IsOfficialTrackRow(row));
        AddConflicts(
            conflicts,
            payload.Table("Laps"),
            "圈速",
            row => row[0] ?? string.Empty,
            row => $"{row.ElementAtOrDefault(6)} / {row.ElementAtOrDefault(7)} s",
            destination.Laps);
        return conflicts;
    }

    private DestinationData DestinationKeys()
    {
        return new DestinationData(
            ToSummaryDictionary(
                store.QueryBackupRows("SELECT Key,Value FROM AppSettings;"),
                row => row[0] ?? string.Empty,
                row => Summarize(row[1])),
            ToSummaryDictionary(
                store.QueryBackupRows("SELECT ModuleId,Key,Value FROM ModuleSettings;"),
                row => $"{row[0]}/{row[1]}",
                row => Summarize(row[2])),
            ToSummaryDictionary(
                store.QueryBackupRows("SELECT Id,CarOrdinal,PI,DisplayName FROM VehicleProfiles;"),
                row => row[0] ?? string.Empty,
                row => string.IsNullOrWhiteSpace(row[3])
                    ? $"CarOrdinal {row[1]} / PI {row[2]}"
                    : row[3]!),
            ToSummaryDictionary(
                store.QueryBackupRows("SELECT Id,Name FROM TrackTemplates;"),
                row => row[0] ?? string.Empty,
                row => row[1] ?? "未命名赛道"),
            ToSummaryDictionary(
                store.QueryBackupRows("SELECT Id,StartedAt,TotalSeconds FROM Laps;"),
                row => row[0] ?? string.Empty,
                row => $"{row[1]} / {row[2]} s"));
    }

    private IEnumerable<string> BuildImportCommands(
        BackupPayload payload,
        BackupImportMode mode,
        DestinationData destination,
        CancellationToken cancellationToken)
    {
        var appSettings = payload.Table("AppSettings");
        foreach (var row in appSettings.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = row[0] ?? string.Empty;
            if (mode == BackupImportMode.Merge && destination.AppSettings.ContainsKey(key)) continue;
            yield return UpsertSql(appSettings, row);
        }

        var moduleSettings = payload.Table("ModuleSettings");
        foreach (var row in moduleSettings.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{row[0]}/{row[1]}";
            if (mode == BackupImportMode.Merge && destination.ModuleSettings.ContainsKey(key)) continue;
            yield return UpsertSql(moduleSettings, row);
        }

        var vehicleProfiles = payload.Table("VehicleProfiles");
        var vehicleIds = vehicleProfiles.Rows
            .Select(row => row[0] ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedVehicles = mode == BackupImportMode.Merge
            ? vehicleIds.Where(destination.Vehicles.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var importedVehicles = vehicleIds.Except(skippedVehicles, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mode == BackupImportMode.Overwrite && importedVehicles.Count > 0)
            yield return $"DELETE FROM VehicleProfiles WHERE {InFilter("Id", importedVehicles)};";
        foreach (var name in new[] { "VehicleProfiles", "EngineCurveBins", "GearModels", "ShiftTargets" })
        {
            var table = payload.Table(name);
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ownerId = row[0] ?? string.Empty;
                if (!importedVehicles.Contains(ownerId)) continue;
                yield return UpsertSql(table, row);
            }
        }

        var trackTemplates = payload.Table("TrackTemplates");
        var sourceTrackRows = trackTemplates.Rows
            .Where(row => row[0] is not null)
            .ToDictionary(row => row[0]!, StringComparer.OrdinalIgnoreCase);
        var importedTrackContentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (trackId, row) in sourceTrackRows)
        {
            var destinationExists = destination.Tracks.ContainsKey(trackId);
            var official = IsOfficialTrackRow(row);
            if (destinationExists && (mode == BackupImportMode.Merge || official)) continue;
            importedTrackContentIds.Add(trackId);
        }

        if (mode == BackupImportMode.Overwrite && importedTrackContentIds.Count > 0)
        {
            yield return $"DELETE FROM TrackPoints WHERE {InFilter("TrackId", importedTrackContentIds)};";
            yield return $"DELETE FROM SectorDefinitions WHERE {InFilter("TrackId", importedTrackContentIds)};";
        }

        foreach (var row in trackTemplates.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackId = row[0] ?? string.Empty;
            var destinationExists = destination.Tracks.ContainsKey(trackId);
            if (destinationExists && (mode == BackupImportMode.Merge || IsOfficialTrackRow(row))) continue;
            yield return UpsertSql(trackTemplates, row);
        }

        foreach (var name in new[] { "TrackPoints", "SectorDefinitions" })
        {
            var table = payload.Table(name);
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!importedTrackContentIds.Contains(row[0] ?? string.Empty)) continue;
                yield return UpsertSql(table, row);
            }
        }

        var sessions = payload.Table("Sessions");
        foreach (var row in sessions.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return InsertIgnoreSql(sessions, row);
        }

        var laps = payload.Table("Laps");
        var lapIds = laps.Rows
            .Select(row => row[0] ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedLaps = mode == BackupImportMode.Merge
            ? lapIds.Where(destination.Laps.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var importedLaps = lapIds.Except(skippedLaps, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mode == BackupImportMode.Overwrite && importedLaps.Count > 0)
            yield return $"DELETE FROM Laps WHERE {InFilter("Id", importedLaps)};";
        foreach (var name in new[] { "Laps", "LapSegments", "LapSamples" })
        {
            var table = payload.Table(name);
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ownerId = row[0] ?? string.Empty;
                if (!importedLaps.Contains(ownerId)) continue;
                yield return UpsertSql(table, row);
            }
        }
    }

    private static string UpsertSql(BackupTableData table, IReadOnlyList<string?> row)
    {
        var spec = Spec(table.Name);
        var values = string.Join(',', row.Select(SqlValue));
        if (spec.Keys.Length == 0)
            return $"INSERT INTO {spec.Name}({string.Join(',', spec.Columns)}) VALUES({values});";
        var updates = spec.Columns
            .Where(column => !spec.Keys.Contains(column, StringComparer.Ordinal))
            .Select(column => $"{column}=excluded.{column}")
            .ToArray();
        return $"INSERT INTO {spec.Name}({string.Join(',', spec.Columns)}) VALUES({values}) " +
               $"ON CONFLICT({string.Join(',', spec.Keys)}) DO UPDATE SET {string.Join(',', updates)};";
    }

    private static string InsertIgnoreSql(BackupTableData table, IReadOnlyList<string?> row)
    {
        var spec = Spec(table.Name);
        return $"INSERT OR IGNORE INTO {spec.Name}({string.Join(',', spec.Columns)}) " +
               $"VALUES({string.Join(',', row.Select(SqlValue))});";
    }

    private static void ValidatePayload(BackupPayload payload)
    {
        var duplicateTable = payload.Tables
            .GroupBy(table => table.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTable is not null)
            throw new InvalidDataException($"备份包含重复数据表：{duplicateTable.Key}。");
        foreach (var table in payload.Tables)
        {
            var spec = TableSpecs.SingleOrDefault(
                candidate => string.Equals(candidate.Name, table.Name, StringComparison.Ordinal))
                       ?? throw new InvalidDataException($"备份包含未知数据表：{table.Name}。");
            if (!table.Columns.SequenceEqual(spec.Columns, StringComparer.Ordinal))
                throw new InvalidDataException($"备份表结构不匹配：{table.Name}。");
            if (table.Rows.Any(row => row.Length != spec.Columns.Length))
                throw new InvalidDataException($"备份表行长度不匹配：{table.Name}。");
        }
    }

    private static void AddConflicts(
        ICollection<BackupConflict> conflicts,
        BackupTableData table,
        string category,
        Func<string?[], string> keySelector,
        Func<string?[], string> sourceSummary,
        IReadOnlyDictionary<string, string> destination,
        Func<string?[], bool>? predicate = null)
    {
        foreach (var row in table.Rows)
        {
            if (predicate is not null && !predicate(row)) continue;
            var key = keySelector(row);
            if (!destination.TryGetValue(key, out var destinationSummary)) continue;
            conflicts.Add(new BackupConflict(category, key, sourceSummary(row), destinationSummary));
        }
    }

    private static Dictionary<string, string> ToSummaryDictionary(
        IReadOnlyList<IReadOnlyList<string?>> rows,
        Func<IReadOnlyList<string?>, string> key,
        Func<IReadOnlyList<string?>, string> summary) =>
        rows.ToDictionary(key, summary, StringComparer.OrdinalIgnoreCase);

    private static int CountConflicts(
        BackupPreview preview,
        string category,
        BackupImportMode mode) =>
        mode == BackupImportMode.Merge
            ? preview.Conflicts.Count(conflict => conflict.Category == category)
            : 0;

    private static bool IsOfficialTrackRow(IReadOnlyList<string?> row) =>
        string.Equals(row.ElementAtOrDefault(12), "PlaygroundOfficial", StringComparison.OrdinalIgnoreCase);

    private static string InFilter(string column, IEnumerable<string> values)
    {
        var quoted = values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(SqlValue)
            .ToArray();
        return quoted.Length == 0 ? "1=0" : $"{column} IN ({string.Join(',', quoted)})";
    }

    private static string SqlValue(string? value) =>
        value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Summarize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "空值";
        return value.Length <= 80 ? value : value[..77] + "...";
    }

    private static TableSpec Spec(string name) =>
        TableSpecs.Single(spec => string.Equals(spec.Name, name, StringComparison.Ordinal));

    private static T ReadJsonEntry<T>(ZipArchiveEntry entry, long maximumBytes)
    {
        if (entry.Length > maximumBytes)
            throw new InvalidDataException($"备份文件过大：{entry.FullName}。");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
               ?? throw new InvalidDataException($"无法读取备份文件：{entry.FullName}。");
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return ComputeSha256(stream);
    }

    private static string ComputeSha256(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream));

    private sealed record TableSpec(string Name, string[] Columns, string[] Keys);

    private sealed record BackupTableData(
        string Name,
        string[] Columns,
        List<string?[]> Rows);

    private sealed record BackupPayload(List<BackupTableData> Tables)
    {
        public BackupTableData Table(string name) =>
            Tables.FirstOrDefault(table => string.Equals(table.Name, name, StringComparison.Ordinal))
            ?? new BackupTableData(name, Spec(name).Columns, []);
    }

    private sealed record PortableArchive(BackupManifest Manifest, BackupPayload Payload);

    private sealed record DestinationData(
        IReadOnlyDictionary<string, string> AppSettings,
        IReadOnlyDictionary<string, string> ModuleSettings,
        IReadOnlyDictionary<string, string> Vehicles,
        IReadOnlyDictionary<string, string> Tracks,
        IReadOnlyDictionary<string, string> Laps);
}
