using System.IO;
using System.Text.Json;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed record AutomaticRecordingOptions(
    bool Enabled,
    long MaximumBytes,
    bool RotateOldest,
    long MinimumFreeBytes,
    int PreRollSeconds,
    int PostRollSeconds)
{
    public const long DefaultMaximumBytes = 5L * 1024 * 1024 * 1024;
    public const long DefaultMinimumFreeBytes = 5L * 1024 * 1024 * 1024;

    public static AutomaticRecordingOptions Load(LazyForzaStore store) => new(
        bool.TryParse(store.GetAppSetting("recording.auto.enabled"), out var enabled) && enabled,
        ParseLong(store.GetAppSetting("recording.auto.maximumBytes"), DefaultMaximumBytes, 1024L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
        bool.TryParse(store.GetAppSetting("recording.auto.rotateOldest"), out var rotate) && rotate,
        ParseLong(store.GetAppSetting("recording.auto.minimumFreeBytes"), DefaultMinimumFreeBytes, 1024L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
        15,
        10);

    public void Save(LazyForzaStore store)
    {
        store.SetAppSetting("recording.auto.enabled", Enabled.ToString());
        store.SetAppSetting("recording.auto.maximumBytes", MaximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        store.SetAppSetting("recording.auto.rotateOldest", RotateOldest.ToString());
        store.SetAppSetting("recording.auto.minimumFreeBytes", MinimumFreeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static long ParseLong(string? text, long fallback, long minimum, long maximum) =>
        long.TryParse(text, out var value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

internal sealed record RecordingCatalogEntry(
    string RecordingPath,
    DateTimeOffset CreatedAt,
    Guid? SessionId,
    string? TrackName,
    long Frames,
    double DurationSeconds,
    bool IsAutomatic,
    bool IsPinned,
    string? ProtectionReason,
    DateTimeOffset? ProtectedUntil)
{
    public bool IsProtected(DateTimeOffset now) =>
        IsPinned || ProtectedUntil is DateTimeOffset until && until > now;
}

internal sealed class RecordingCatalog
{
    private readonly string recordingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RecordingCatalog(string recordingsPath) => this.recordingsPath = recordingsPath;

    public IReadOnlyList<RecordingCatalogEntry> List()
    {
        if (!Directory.Exists(recordingsPath)) return [];
        return Directory.EnumerateFiles(recordingsPath, "*.lfztelemetry", SearchOption.TopDirectoryOnly)
            .Select(LoadOrInfer)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToArray();
    }

    public void Save(RecordingCatalogEntry entry)
    {
        var metadataPath = MetadataPath(entry.RecordingPath);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(entry, JsonOptions));
    }

    public void SetPinned(string recordingPath, bool pinned)
    {
        var current = LoadOrInfer(recordingPath);
        Save(current with { IsPinned = pinned });
    }

    public void Delete(RecordingCatalogEntry entry)
    {
        if (File.Exists(entry.RecordingPath)) File.Delete(entry.RecordingPath);
        var metadataPath = MetadataPath(entry.RecordingPath);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
    }

    private RecordingCatalogEntry LoadOrInfer(string recordingPath)
    {
        var metadataPath = MetadataPath(recordingPath);
        if (File.Exists(metadataPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<RecordingCatalogEntry>(File.ReadAllText(metadataPath));
                if (saved is not null) return saved with { RecordingPath = recordingPath };
            }
            catch (JsonException)
            {
            }
        }

        var file = new FileInfo(recordingPath);
        return new RecordingCatalogEntry(
            recordingPath,
            file.CreationTimeUtc,
            null,
            null,
            0,
            0,
            Path.GetFileName(recordingPath).StartsWith("auto-", StringComparison.OrdinalIgnoreCase),
            false,
            null,
            null);
    }

    private static string MetadataPath(string recordingPath) => recordingPath + ".json";
}

internal sealed record RecordingCapacityResult(
    bool CanStart,
    long UsedBytes,
    long AvailableBytes,
    string Message);

internal sealed class RecordingCapacityManager(
    string recordingsPath,
    RecordingCatalog catalog)
{
    public RecordingCapacityResult Prepare(AutomaticRecordingOptions options)
    {
        Directory.CreateDirectory(recordingsPath);
        var entries = catalog.List();
        var used = TotalBytes();
        var available = AvailableBytes();
        if (available < options.MinimumFreeBytes)
            return new RecordingCapacityResult(false, used, available, "磁盘剩余空间低于录制保留值。");
        if (used < options.MaximumBytes)
            return new RecordingCapacityResult(true, used, available, "容量充足。");
        if (!options.RotateOldest)
            return new RecordingCapacityResult(false, used, available, "录制容量已达到上限；保守模式不会自动删除文件。");

        var keepRecent = entries
            .Where(entry => entry.IsAutomatic)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(5)
            .Select(entry => entry.RecordingPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries
                     .Where(entry => entry.IsAutomatic)
                     .OrderBy(entry => entry.CreatedAt))
        {
            if (used < options.MaximumBytes * 0.9) break;
            if (keepRecent.Contains(entry.RecordingPath) || entry.IsProtected(DateTimeOffset.UtcNow)) continue;
            var size = File.Exists(entry.RecordingPath) ? new FileInfo(entry.RecordingPath).Length : 0;
            catalog.Delete(entry);
            used = Math.Max(0, used - size);
        }

        return used < options.MaximumBytes && AvailableBytes() >= options.MinimumFreeBytes
            ? new RecordingCapacityResult(true, used, AvailableBytes(), "已轮换最旧的未保护自动录制。")
            : new RecordingCapacityResult(false, used, AvailableBytes(), "受保护录制已占满容量，无法开始新的自动录制。");
    }

    public long TotalBytes() =>
        Directory.Exists(recordingsPath)
            ? Directory.EnumerateFiles(recordingsPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".lfztelemetry", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".lfztelemetry.partial", StringComparison.OrdinalIgnoreCase))
                .Sum(path => new FileInfo(path).Length)
            : 0;

    public long AvailableBytes()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(recordingsPath));
        return string.IsNullOrWhiteSpace(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
    }
}
