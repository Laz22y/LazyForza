using System.Net.Http.Headers;
using System.Text.Json;

namespace LazyForza.Update;

public sealed record ReleaseAnnouncement(
    string Tag, string Title, string Notes, DateTimeOffset? PublishedAt, bool IsPreview, UpdateSourceKind Source)
{
    // Construct links from the known repository; release bodies never supply executable links or markup.
    public Uri PageUri => new($"https://{(Source == UpdateSourceKind.GitHub ? "github.com" : "gitcode.com")}/" +
        $"{GitHubReleaseClient.RepositoryOwner}/{GitHubReleaseClient.RepositoryName}/releases/tag/{Uri.EscapeDataString(Tag)}");
}

public sealed record ReleaseHistorySnapshot(DateTimeOffset FetchedAt, ReleaseAnnouncement[] Releases);

/// <summary>Read-only release announcements, independent of update availability and package installation.</summary>
public sealed class ReleaseHistoryClient : IDisposable
{
    public const int MaximumEntries = 5;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public ReleaseHistoryClient() : this(new HttpClient(), true) { }
    public ReleaseHistoryClient(HttpClient httpClient, bool ownsClient = false)
    {
        this.httpClient = httpClient;
        this.ownsClient = ownsClient;
    }

    public async Task<ReleaseHistorySnapshot> GetRecentAsync(
        UpdateSourceKind preferredSource, bool includePreview, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var other = preferredSource == UpdateSourceKind.GitHub ? UpdateSourceKind.GitCode : UpdateSourceKind.GitHub;
        foreach (var source in new[] { preferredSource, other })
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var uri = source == UpdateSourceKind.GitHub
                    ? GitHubPreviewReleaseClient.ReleasesApi
                    : GitCodePreviewReleaseClient.ReleasesApi;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("LazyForza-Announcements/1.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var releases = Parse(json, source, includePreview);
                if (releases.Length == 0) throw new JsonException("No published releases in this channel.");
                return new(DateTimeOffset.UtcNow, releases);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failure = error;
            }
        }
        throw new UpdateException("暂时无法加载更新公告。", failure!);
    }

    public static ReleaseAnnouncement[] Parse(string json, UpdateSourceKind source, bool includePreview)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a release list.");
        var entries = new List<(UpdateSemanticVersion Version, ReleaseAnnouncement Announcement)>();
        foreach (var release in document.RootElement.EnumerateArray().Take(100))
        {
            if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft")) continue;
            var tag = String(release, "tag_name");
            if (tag.Length > 160 || !UpdateSemanticVersion.TryParse(tag, out var version)) continue;
            var preview = Flag(release, "prerelease") || version.IsPrerelease;
            if (!includePreview && preview) continue;
            var title = String(release, "name");
            if (string.IsNullOrWhiteSpace(title)) title = tag;
            var notes = String(release, "body");
            notes = UpdateReleaseMetadata.Parse(notes[..Math.Min(notes.Length, 32000)], version.NumericVersion, version.NumericVersion).Notes;
            var date = DateTimeOffset.TryParse(String(release, "published_at"), out var published)
                ? published : (DateTimeOffset?)null;
            entries.Add((version, new(tag, title[..Math.Min(title.Length, 240)], notes, date, preview, source)));
        }
        return entries.OrderByDescending(entry => entry.Version)
            .DistinctBy(entry => entry.Version.Value).Take(MaximumEntries).Select(entry => entry.Announcement).ToArray();
    }

    private static string String(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && (value.ValueKind == JsonValueKind.True ||
            value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result);

    public void Dispose() { if (ownsClient) httpClient.Dispose(); }
}
