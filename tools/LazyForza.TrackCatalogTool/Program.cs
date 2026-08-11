using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using LazyForza.Domain;
using LazyForza.Storage;

if (args.Length < 2)
{
    Usage();
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "inventory" => Inventory(args[1]),
    "install" => Install(args[1]),
    "setting" when args.Length >= 3 => Setting(args[1], args[2]),
    "rank-prefix" when args.Length >= 3 && Guid.TryParse(args[2], out var targetId) =>
        RankPrefix(args[1], targetId),
    "export" when args.Length >= 4 => Export(args[1], args[2], args[3]),
    "verify" => Verify(args[1]),
    _ => InvalidUsage()
};

static int Inventory(string databasePath)
{
    using var store = new LazyForzaStore(databasePath);
    var tracks = store.ListTracks();
    Console.WriteLine($"Schema={store.SchemaVersion} Tracks={tracks.Count} Laps={store.CountLaps()}");
    foreach (var summary in tracks)
    {
        var loaded = store.LoadTrack(summary.Id);
        Console.WriteLine(
            $"{summary.Id}\t{loaded?.Track.Source}\t{summary.LayoutKind}\t{summary.CatalogKind}\t" +
            $"{summary.Length:0.0}m\t{loaded?.Track.Points.Count ?? 0} points\t{loaded?.Sectors.Count ?? 0} sectors\t" +
            $"{summary.Laps} laps\t{summary.Category ?? "-"}\t{summary.Name}");
    }

    return 0;
}

static int Install(string databasePath)
{
    using var store = new LazyForzaStore(databasePath);
    var beforeTracks = store.CountTracks();
    var beforeLaps = store.CountLaps();
    var result = PlaygroundOfficialTrackCatalog.EnsureImported(store);
    Console.WriteLine(
        $"Installed catalog {result.Version}: refreshed={result.ImportedTracks}, official={store.CountTracks(TrackCatalogKind.PlaygroundOfficial)}, " +
        $"tracks={beforeTracks}->{store.CountTracks()}, laps={beforeLaps}->{store.CountLaps()}");
    return 0;
}

static int Setting(string databasePath, string key)
{
    using var store = new LazyForzaStore(databasePath);
    Console.WriteLine($"{key}={store.GetAppSetting(key) ?? "<null>"}");
    return 0;
}

static int RankPrefix(string databasePath, Guid targetId)
{
    using var store = new LazyForzaStore(databasePath);
    var target = store.LoadTrack(targetId)?.Track
        ?? throw new InvalidOperationException($"Target track {targetId} was not found.");
    var targetPrefix = Prefix(target.Points, 350);
    var ranked = store.ListTracks("fh6_udp_live")
        .Select(summary => (Summary: summary, Track: store.LoadTrack(summary.Id)?.Track))
        .Where(item => item.Track is not null)
        .Select(item => (
            item.Summary,
            StartMeters: Distance(target.Points[0], item.Track!.Points[0]),
            PrefixMeanMeters: MeanNearestDistance(targetPrefix, Prefix(item.Track.Points, 420))))
        .OrderBy(item => item.PrefixMeanMeters)
        .ThenBy(item => item.StartMeters)
        .Take(12)
        .ToArray();
    Console.WriteLine($"Prefix ranking for {target.Name} ({target.Id})");
    foreach (var candidate in ranked)
        Console.WriteLine(
            $"{candidate.PrefixMeanMeters,8:0.0}m mean\t{candidate.StartMeters,8:0.0}m start\t" +
            $"{candidate.Summary.Category}\t{candidate.Summary.Name}\t{candidate.Summary.Id}");
    return 0;
}

static IReadOnlyList<TrackPoint> Prefix(IReadOnlyList<TrackPoint> points, double maximumS)
{
    var prefix = points.Where(point => point.S <= maximumS).ToArray();
    return prefix.Length > 0 ? prefix : points.Take(Math.Min(points.Count, 80)).ToArray();
}

static double MeanNearestDistance(IReadOnlyList<TrackPoint> source, IReadOnlyList<TrackPoint> candidate)
{
    if (source.Count == 0 || candidate.Count == 0) return double.PositiveInfinity;
    var stride = Math.Max(1, source.Count / 50);
    var distances = new List<double>();
    for (var index = 0; index < source.Count; index += stride)
        distances.Add(candidate.Min(point => Distance(source[index], point)));
    return distances.Average();
}

static double Distance(TrackPoint left, TrackPoint right)
{
    var dx = left.X - right.X;
    var dy = left.Y - right.Y;
    var dz = left.Z - right.Z;
    return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
}

static int Export(string databasePath, string outputPath, string catalogVersion)
{
    if (!Regex.IsMatch(catalogVersion, @"^\d{4}\.\d{2}\.\d{2}\.\d+$"))
        throw new ArgumentException(
            $"Catalog version must use YYYY.MM.DD.revision format: {catalogVersion}",
            nameof(catalogVersion));

    using var store = new LazyForzaStore(databasePath);
    var entries = store.ListTracks("fh6_udp_live")
        .Select(summary => store.LoadTrack(summary.Id)
            ?? throw new InvalidOperationException($"Track {summary.Id} could not be loaded."))
        .Select(loaded =>
        {
            var (category, displayName) = ResolveOfficialIdentity(loaded.Track);
            return new PlaygroundTrackCatalogEntry(
                loaded.Track with
                {
                    Name = displayName,
                    CatalogKind = TrackCatalogKind.PlaygroundOfficial,
                    Category = category
                },
                loaded.Sectors);
        })
        .OrderBy(entry => entry.Track.Category, StringComparer.Ordinal)
        .ThenBy(entry => entry.Track.Name, StringComparer.Ordinal)
        .ToArray();

    if (entries.Length == 0)
        throw new InvalidOperationException("No fh6_udp_live tracks were found; refusing to produce an empty catalog.");
    if (entries.Select(entry => entry.Track.Id).Distinct().Count() != entries.Length)
        throw new InvalidOperationException("The official catalog contains duplicate track IDs.");

    var document = new PlaygroundTrackCatalogDocument(
        catalogVersion,
        DateTimeOffset.UtcNow,
        "Player-recorded FH6 Playground official events (excluding Showcase and Wristband events)",
        entries);
    var fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    using (var file = File.Create(fullOutputPath))
    using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
    {
        JsonSerializer.Serialize(gzip, document);
    }

    Console.WriteLine(
        $"Exported {entries.Length} tracks, {entries.Sum(entry => entry.Track.Points.Count):N0} points and " +
        $"{entries.Sum(entry => entry.Sectors.Count):N0} sectors to {fullOutputPath}");
    return Verify(fullOutputPath);
}

static (string Category, string DisplayName) ResolveOfficialIdentity(TrackTemplate track)
{
    if (track.CatalogKind == TrackCatalogKind.PlaygroundOfficial &&
        !string.IsNullOrWhiteSpace(track.Category) &&
        !track.Name.Contains('|'))
    {
        return (track.Category.Trim(), track.Name.Trim());
    }

    return SplitOfficialName(track.Name);
}

static int Verify(string inputPath)
{
    using var file = File.OpenRead(inputPath);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    var document = JsonSerializer.Deserialize<PlaygroundTrackCatalogDocument>(gzip)
        ?? throw new InvalidOperationException("Catalog deserialization returned null.");
    var duplicateIds = document.Tracks
        .GroupBy(entry => entry.Track.Id)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToArray();
    var invalidTracks = document.Tracks
        .Where(entry =>
            entry.Track.CatalogKind != TrackCatalogKind.PlaygroundOfficial ||
            string.IsNullOrWhiteSpace(entry.Track.Category) ||
            entry.Track.Points.Count < 4 ||
            entry.Sectors.Count == 0)
        .Select(entry => entry.Track.Name)
        .ToArray();
    if (duplicateIds.Length > 0 || invalidTracks.Length > 0)
        throw new InvalidOperationException(
            $"Catalog validation failed. Duplicate IDs={duplicateIds.Length}; invalid tracks={string.Join(", ", invalidTracks)}");

    Console.WriteLine(
        $"Verified version {document.Version}: {document.Tracks.Count} tracks, " +
        $"{document.Tracks.Sum(entry => entry.Track.Points.Count):N0} points, " +
        $"{document.Tracks.Sum(entry => entry.Sectors.Count):N0} sectors.");
    foreach (var group in document.Tracks.GroupBy(entry => entry.Track.Category).OrderBy(group => group.Key))
        Console.WriteLine($"{group.Key}: {group.Count()}");
    return 0;
}

static (string Category, string DisplayName) SplitOfficialName(string name)
{
    var separator = name.IndexOf('|');
    if (separator < 0) return ("其他", name.Trim());
    var category = name[..separator].Trim();
    var displayName = name[(separator + 1)..].Trim();
    return (
        string.IsNullOrWhiteSpace(category) ? "其他" : category,
        string.IsNullOrWhiteSpace(displayName) ? name.Trim() : displayName);
}

static int InvalidUsage()
{
    Usage();
    return 1;
}

static void Usage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  LazyForza.TrackCatalogTool inventory <lazyforza.db>");
    Console.Error.WriteLine("  LazyForza.TrackCatalogTool install <lazyforza.db>");
    Console.Error.WriteLine("  LazyForza.TrackCatalogTool setting <lazyforza.db> <key>");
    Console.Error.WriteLine("  LazyForza.TrackCatalogTool rank-prefix <lazyforza.db> <track-id>");
    Console.Error.WriteLine("  LazyForza.TrackCatalogTool export <lazyforza.db> <catalog.json.gz> <YYYY.MM.DD.revision>");
    Console.Error.WriteLine("  LazyForza.TrackCatalogTool verify <catalog.json.gz>");
}
