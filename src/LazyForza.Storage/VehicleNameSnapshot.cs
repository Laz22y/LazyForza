using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace LazyForza.Storage;

/// <summary>Names are resolved at display time; they never replace user-owned profile names.</summary>
public sealed class VehicleNameSnapshot
{
    public const string SourceUrl = "https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f";
    public const int MaximumBytes = 4 * 1024 * 1024;
    public VehicleNameCatalogInfo? Info { get; }
    public FrozenDictionary<int, string> Names { get; }

    public VehicleNameSnapshot(VehicleNameCatalogInfo? info, IReadOnlyDictionary<int, string> names)
    {
        Info = info;
        Names = names.ToFrozenDictionary();
    }

    public static VehicleNameSnapshot FromUpstream(string json, string revision, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(json);
        var names = new Dictionary<int, string>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Vehicle name data must be a JSON object.");
        foreach (var pair in document.RootElement.EnumerateObject())
        {
            var value = pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() : pair.Value.GetRawText();
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0 ||
                !names.TryAdd(ordinal, pair.Name.Trim()))
                throw new InvalidDataException("Invalid or duplicate vehicle ordinal.");
        }
        var snapshot = new VehicleNameSnapshot(new(SourceUrl, "HDR", revision, updatedAt, names.Count), names);
        snapshot.Validate();
        return snapshot;
    }

    public void Validate()
    {
        if (Info is null || Info.Source != SourceUrl || Info.Author != "HDR" ||
            Info.Revision is not { Length: 40 } || !Info.Revision.All(char.IsAsciiHexDigit) ||
            Info.UpdatedAt <= DateTimeOffset.UnixEpoch || Info.VehicleCount != Names.Count ||
            Names.Count is < 100 or > 100_000 || Names.Any(pair => pair.Key <= 0 || !ValidName(pair.Value)))
            throw new InvalidDataException("Invalid vehicle name catalog.");
    }

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 300 &&
        value.All(c => !char.IsControl(c) && c is not ('\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or
            '\u2066' or '\u2067' or '\u2068' or '\u2069'));
}

/// <summary>Validated downloads, bundled data and explicit imports retain the newest usable catalog.</summary>
public sealed class VehicleNameCatalogCache(string path)
{
    // Downloads, imports and directory changes can finish in a different order than they started.
    private static readonly object WriteGate = new();
    private sealed record CacheDocument(int SchemaVersion, VehicleNameCatalogInfo Info, Dictionary<int, string> Names);

    public static VehicleNameCatalogCache ForDirectory(string root) => new(Path.Combine(root, "Cache", "vehicle-names.json"));

    public static byte[] Serialize(VehicleNameSnapshot snapshot)
    {
        snapshot.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CacheDocument(1, snapshot.Info!, snapshot.Names.ToDictionary()));
        if (bytes.Length > VehicleNameSnapshot.MaximumBytes) throw new InvalidDataException("Vehicle name cache exceeds its size limit.");
        return bytes;
    }

    public static VehicleNameSnapshot Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > VehicleNameSnapshot.MaximumBytes) throw new InvalidDataException("Vehicle name cache exceeds its size limit.");
        var document = JsonSerializer.Deserialize<CacheDocument>(bytes);
        if (document is not { SchemaVersion: 1, Info: not null, Names: not null })
            throw new InvalidDataException("Invalid vehicle name cache.");
        var snapshot = new VehicleNameSnapshot(document.Info, document.Names);
        snapshot.Validate();
        return snapshot;
    }

    public VehicleNameSnapshot Load(VehicleNameSnapshot fallback)
    {
        var cached = ReadCache();
        return cached is null || fallback.Info is not null && cached.Info!.UpdatedAt < fallback.Info.UpdatedAt ? fallback : cached;
    }

    private VehicleNameSnapshot? ReadCache()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > VehicleNameSnapshot.MaximumBytes) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Deserialize(buffer.ToArray());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    public async Task<VehicleNameSnapshot> SaveAsync(VehicleNameSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Merge(snapshot, static () => { }, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Persist the newer catalog, then commit related settings/data. Restore the old file if that commit fails.</summary>
    public VehicleNameSnapshot Merge(VehicleNameSnapshot snapshot, Action commit, CancellationToken cancellationToken = default)
    {
        var bytes = Serialize(snapshot);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (WriteGate)
        {
            var cached = ReadCache();
            if (cached is not null && (cached.Info!.Revision == snapshot.Info!.Revision || cached.Info.UpdatedAt >= snapshot.Info.UpdatedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                commit();
                return cached;
            }
            var previous = File.Exists(path) && new FileInfo(path).Length <= VehicleNameSnapshot.MaximumBytes ? File.ReadAllBytes(path) : null;
            WriteAtomic(bytes, cancellationToken);
            try { commit(); }
            catch
            {
                if (previous is null) File.Delete(path);
                else WriteAtomic(previous, CancellationToken.None);
                throw;
            }
            return snapshot;
        }
    }

    private void WriteAtomic(byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
