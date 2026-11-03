using System.Net;
using System.Text;
using System.Text.Json;

namespace LazyForza.Update;

public sealed record VehicleNameRevision(string Revision, DateTimeOffset UpdatedAt, int Additions, int Deletions);

/// <summary>Checks commit metadata first. Catalog content is fetched only after the caller obtains consent.</summary>
public sealed class VehicleNameCatalogClient : IDisposable
{
    public const string GistId = "0659d1717bc61504bf83750628963f4f";
    public const string FileName = "Forza Horizon 6 Car Ordinals.json";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient http;

    public VehicleNameCatalogClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LazyForza-vehicle-names/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<VehicleNameRevision> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var document = JsonDocument.Parse(await GetAsync(
            $"https://api.github.com/gists/{GistId}/commits?per_page=1", timeout.Token).ConfigureAwait(false));
        var commit = document.RootElement.EnumerateArray().First();
        var revision = commit.GetProperty("version").GetString();
        if (!ValidRevision(revision) || !commit.GetProperty("committed_at").TryGetDateTimeOffset(out var updatedAt) ||
            updatedAt <= DateTimeOffset.UnixEpoch)
            throw new InvalidDataException("Invalid GitHub catalog revision.");
        var changes = commit.GetProperty("change_status");
        return new(revision!, updatedAt, Math.Max(0, changes.GetProperty("additions").GetInt32()),
            Math.Max(0, changes.GetProperty("deletions").GetInt32()));
    }

    public async Task<string> DownloadAsync(VehicleNameRevision revision, CancellationToken cancellationToken)
    {
        if (!ValidRevision(revision.Revision)) throw new InvalidDataException("Invalid GitHub catalog revision.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var document = JsonDocument.Parse(await GetAsync(
            $"https://api.github.com/gists/{GistId}/{revision.Revision}", timeout.Token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != GistId ||
            root.GetProperty("history")[0].GetProperty("version").GetString() != revision.Revision ||
            !root.GetProperty("files").TryGetProperty(FileName, out var file) ||
            file.GetProperty("truncated").GetBoolean() ||
            file.GetProperty("content").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub returned an incomplete or different vehicle name revision.");
        return file.GetProperty("content").GetString()!;
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub temporarily refused the request. Try again later.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("GitHub response exceeds its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("GitHub response exceeds its size limit.");
            buffer.Write(chunk, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool ValidRevision(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);
    public void Dispose() => http.Dispose();
}
