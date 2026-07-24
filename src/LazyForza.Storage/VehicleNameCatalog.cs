using System.IO.Compression;
using System.Reflection;
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
    private static readonly Lazy<CatalogState> State = new(Load);

    public static VehicleNameCatalogInfo? Info => State.Value.Info;

    public static string? TryGetName(int carOrdinal) =>
        State.Value.Names.GetValueOrDefault(carOrdinal);

    public static string DisplayName(int carOrdinal) =>
        TryGetName(carOrdinal) ?? $"车辆 {carOrdinal}";

    private static CatalogState Load()
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
            return new CatalogState(
                names,
                new VehicleNameCatalogInfo(
                    document.Source,
                    document.Author,
                    document.Revision,
                    updatedAt,
                    names.Count));
        }
        catch
        {
            // Vehicle naming is auxiliary. Missing/corrupt snapshots must not block Live UDP.
            return new CatalogState(new Dictionary<int, string>(), null);
        }
    }

    private sealed record CatalogDocument(
        string Source,
        string Author,
        string Revision,
        string UpdatedAtUtc,
        Dictionary<string, string> Cars);

    private sealed record CatalogState(
        IReadOnlyDictionary<int, string> Names,
        VehicleNameCatalogInfo? Info);
}
