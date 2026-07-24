using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using LazyForza.Domain;

namespace LazyForza.Storage;

public static class PlaygroundOfficialTrackCatalog
{
    public const string DisplayName = "Playground 官方赛事";
    private const string CatalogVersionSetting = "tracks.playgroundOfficialCatalogVersion";
    private const string ResourceName = "LazyForza.Storage.Assets.PlaygroundOfficialTracks.json.gz";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static PlaygroundCatalogImportResult EnsureImported(LazyForzaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var document = LoadEmbedded();
        var installedVersion = store.GetAppSetting(CatalogVersionSetting);
        if (string.Equals(installedVersion, document.Version, StringComparison.Ordinal) &&
            store.CountTracks(TrackCatalogKind.PlaygroundOfficial) == document.Tracks.Count)
        {
            return new PlaygroundCatalogImportResult(document.Version, document.Tracks.Count, 0);
        }

        foreach (var entry in document.Tracks)
        {
            var officialTrack = entry.Track with
            {
                CatalogKind = TrackCatalogKind.PlaygroundOfficial,
                Category = entry.Track.Category?.Trim()
            };
            store.SaveTrack(officialTrack, entry.Sectors);
        }

        store.SetAppSetting(CatalogVersionSetting, document.Version);
        return new PlaygroundCatalogImportResult(document.Version, document.Tracks.Count, document.Tracks.Count);
    }

    public static PlaygroundTrackCatalogDocument LoadEmbedded()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded track catalog resource: {ResourceName}");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<PlaygroundTrackCatalogDocument>(gzip, JsonOptions)
            ?? throw new InvalidOperationException("The embedded Playground official track catalog is empty or invalid.");
    }
}

public sealed record PlaygroundCatalogImportResult(string Version, int TotalTracks, int ImportedTracks);

public sealed record PlaygroundTrackCatalogDocument(
    string Version,
    DateTimeOffset GeneratedAt,
    string Source,
    IReadOnlyList<PlaygroundTrackCatalogEntry> Tracks);

public sealed record PlaygroundTrackCatalogEntry(
    TrackTemplate Track,
    IReadOnlyList<SectorDefinition> Sectors);
