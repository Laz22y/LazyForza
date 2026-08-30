using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.Update;

public sealed class GitHubPreviewReleaseClient : UpdateReleaseClientBase
{
    public static readonly Uri ReleasesApi = new(
        $"https://api.github.com/repos/{GitHubReleaseClient.RepositoryOwner}/{GitHubReleaseClient.RepositoryName}/releases?per_page=30");

    public GitHubPreviewReleaseClient()
        : this(CreateHttpClient(), true)
    {
    }

    public GitHubPreviewReleaseClient(HttpClient httpClient, bool disposeClient = false)
        : base(httpClient, disposeClient)
    {
    }

    public override UpdateSourceKind Source => UpdateSourceKind.GitHub;

    public override Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var normalized = NormalizeVersion(currentVersion);
        return CheckForUpdateAsync(
            UpdateSemanticVersion.Parse(normalized.ToString(3)),
            cancellationToken);
    }

    public async Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        UpdateSemanticVersion currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var request = CreateRequest(HttpMethod.Get, ReleasesApi);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(
                    $"GitHub 返回了 HTTP {(int)response.StatusCode}，暂时无法检查预览版更新。");

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<ReleaseResponse[]>(
                    content,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false) ?? throw new UpdateException("GitHub 返回的预览版列表为空。");

            var selected = releases
                .Where(release => !release.Draft)
                .Select(release => new
                {
                    Release = release,
                    Parsed = UpdateSemanticVersion.TryParse(release.TagName, out var version)
                        ? version
                        : null
                })
                .Where(candidate =>
                    candidate.Parsed is not null &&
                    candidate.Release.Prerelease &&
                    candidate.Parsed.IsPrerelease &&
                    candidate.Parsed.CompareTo(currentVersion) > 0)
                .OrderByDescending(candidate => candidate.Parsed)
                .FirstOrDefault();
            if (selected?.Parsed is null) return null;

            var release = selected.Release;
            var version = selected.Parsed;
            var expectedName = $"LazyForza-{version}-win-x64.zip";
            var packages = release.Assets.Where(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packages.Length != 1)
                throw new UpdateException(
                    $"GitHub 预览版必须且只能包含一个预期文件 {expectedName}。");
            var packageDto = packages[0];
            if (packageDto.Size is <= 0 or > MaxArchiveBytes)
                throw new UpdateException(
                    $"GitHub 预览版文件大小异常：{packageDto.Size:N0} 字节。");
            var package = ToAsset(
                packageDto.Name,
                packageDto.BrowserDownloadUrl,
                packageDto.Size,
                packageDto.Digest);
            ValidateReleaseDownloadUri(package.DownloadUri);

            var checksumDtos = release.Assets.Where(asset =>
                string.Equals(asset.Name, $"{expectedName}.sha256", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (checksumDtos.Length > 1)
                throw new UpdateException("GitHub 预览版包含重复的校验和文件。");
            var checksum = checksumDtos.Length == 0
                ? null
                : ToAsset(
                    checksumDtos[0].Name,
                    checksumDtos[0].BrowserDownloadUrl,
                    checksumDtos[0].Size,
                    checksumDtos[0].Digest);
            if (checksum is not null) ValidateReleaseDownloadUri(checksum.DownloadUri);

            if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var pageUri) ||
                pageUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(pageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("GitHub 预览版页面地址不可信。");

            var metadata = UpdateReleaseMetadata.Parse(
                release.Body,
                currentVersion.NumericVersion,
                version.NumericVersion);
            return new UpdateReleaseInfo(
                version.NumericVersion,
                release.TagName,
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                metadata.Notes,
                pageUri,
                package,
                checksum,
                Source,
                metadata.Type,
                VersionLabel: version.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("连接 GitHub 检查预览版更新超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException("连接 GitHub 检查预览版更新失败。", exception);
        }
        catch (JsonException exception)
        {
            throw new UpdateException("GitHub 返回的预览版信息格式无效。", exception);
        }
    }

    protected override HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = base.CreateRequest(method, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    protected override void ValidateReleaseDownloadUri(Uri uri)
    {
        var expectedPrefix =
            $"/{GitHubReleaseClient.RepositoryOwner}/{GitHubReleaseClient.RepositoryName}/releases/download/";
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("GitHub 预览版文件下载地址不可信。");
    }

    private sealed class ReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public AssetResponse[] Assets { get; init; } = [];
    }

    private sealed class AssetResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
