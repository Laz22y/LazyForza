using System.IO.Compression;
using System.Text.Json;

namespace LazyForza.Storage;

public sealed record VehicleNameCatalogInfo(
    string Source,
    string Author,
    string Revision,
    DateTimeOffset UpdatedAt,
    int VehicleCount);

public static class VehicleNameCatalog
{
    private static readonly Lazy<VehicleNameSnapshot> Embedded = new(Load);
    private static VehicleNameSnapshot? current;

    public static VehicleNameSnapshot EmbeddedSnapshot => Embedded.Value;
    public static VehicleNameSnapshot Current => Volatile.Read(ref current) ?? Embedded.Value;
    public static VehicleNameCatalogInfo? Info => Current.Info;

    public static void Activate(VehicleNameSnapshot snapshot) =>
        Interlocked.Exchange(ref current, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

    public static string? TryGetName(int carOrdinal) =>
        Current.Names.GetValueOrDefault(carOrdinal);

    public static string DisplayName(int carOrdinal) =>
        TryGetName(carOrdinal) ?? $"车辆 {carOrdinal}";

    private static VehicleNameSnapshot Load()
    {
        try
        {
            var assembly = typeof(VehicleNameCatalog).Assembly;
            var resourceName = assembly
                .GetManifestResourceNames()
                .Single(name => name.EndsWith(
                    ".Assets.Fh6VehicleNames.json.gz",
                    StringComparison.Ordinal));
            using var compressed = assembly.GetManifestResourceStream(resourceName) ??
                                   throw new InvalidDataException(
                                       "Embedded FH6 vehicle name catalog is missing.");
            using var gzip = new GZipStream(
                compressed,
                CompressionMode.Decompress,
                leaveOpen: false);
            var document = JsonSerializer.Deserialize<CatalogDocument>(
                gzip,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                           throw new InvalidDataException(
                               "Embedded FH6 vehicle name catalog is empty.");
            var names = new Dictionary<int, string>();
            foreach (var pair in document.Cars)
            {
                if (int.TryParse(pair.Key, out var ordinal) &&
                    ordinal > 0 &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                    names[ordinal] = pair.Value.Trim();
            }
            var updatedAt = DateTimeOffset.TryParse(
                document.UpdatedAtUtc,
                out var parsedUpdatedAt)
                ? parsedUpdatedAt
                : DateTimeOffset.MinValue;
            return new VehicleNameSnapshot(
                new VehicleNameCatalogInfo(
                    document.Source,
                    document.Author,
                    document.Revision,
                    updatedAt,
                    names.Count), names);
        }
        catch
        {
            // Vehicle naming is auxiliary. Missing/corrupt snapshots must not block Live UDP.
            return new VehicleNameSnapshot(null, new Dictionary<int, string>());
        }
    }

    private sealed record CatalogDocument(
        string Source,
        string Author,
        string Revision,
        string UpdatedAtUtc,
        Dictionary<string, string> Cars);

}
