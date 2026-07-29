using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.Update;

public sealed class GitHubReleaseClient : UpdateReleaseClientBase
{
    public const string RepositoryOwner = "Laz22y";
    public const string RepositoryName = "LazyForza";
    public static readonly Uri LatestReleaseApi =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    public GitHubReleaseClient()
        : this(CreateHttpClient(), true)
    {
    }

    public GitHubReleaseClient(HttpClient httpClient, bool disposeClient = false)
        : base(httpClient, disposeClient)
    {
    }

    public override UpdateSourceKind Source => UpdateSourceKind.GitHub;

    public override async Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var request = CreateRequest(HttpMethod.Get, LatestReleaseApi);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(
                    $"GitHub 返回了 HTTP {(int)response.StatusCode}，暂时无法检查更新。");

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(
                content,
                cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new UpdateException("GitHub 返回的发行版信息为空。");

            if (release.Draft || release.Prerelease)
                throw new UpdateException("GitHub latest 指向了草稿或预发行版本，已拒绝更新。");
            if (!TryParseStableVersion(release.TagName, out var version))
                throw new UpdateException(
                    $"无法识别发行版标签“{release.TagName}”。仅支持 v主版本.次版本.修订号。");
            if (version.CompareTo(NormalizeVersion(currentVersion)) <= 0) return null;

            var expectedName = $"LazyForza-{version.ToString(3)}-win-x64.zip";
            var packageCandidates = release.Assets.Where(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packageCandidates.Length != 1)
                throw new UpdateException($"发行版必须且只能包含一个预期文件 {expectedName}。");
            var packageDto = packageCandidates[0];
            if (packageDto.Size is <= 0 or > MaxArchiveBytes)
                throw new UpdateException(
                    $"发行版文件大小异常：{packageDto.Size:N0} 字节。");

            var package = ToAsset(
                packageDto.Name,
                packageDto.BrowserDownloadUrl,
                packageDto.Size,
                packageDto.Digest);
            ValidateReleaseDownloadUri(package.DownloadUri);

            var checksumCandidates = release.Assets.Where(asset =>
                string.Equals(asset.Name, $"{expectedName}.sha256", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (checksumCandidates.Length > 1)
                throw new UpdateException("发行版包含重复的校验和文件。");
            var checksumDto = checksumCandidates.SingleOrDefault();
            var checksum = checksumDto is null
                ? null
                : ToAsset(
                    checksumDto.Name,
                    checksumDto.BrowserDownloadUrl,
                    checksumDto.Size,
                    checksumDto.Digest);
            if (checksum is not null) ValidateReleaseDownloadUri(checksum.DownloadUri);
            if (!TryParseSha256Digest(package.Digest, out _) && checksum is null)
                throw new UpdateException(
                    "发行版既没有 GitHub SHA-256 摘要，也没有校验和文件。");

            if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var pageUri) ||
                pageUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(pageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("发行版页面地址不可信。");

            var metadata = UpdateReleaseMetadata.Parse(
                release.Body,
                currentVersion,
                version);
            return new UpdateReleaseInfo(
                version,
                release.TagName,
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                metadata.Notes,
                pageUri,
                package,
                checksum,
                Source,
                metadata.Type);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("连接 GitHub 检查更新超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException("连接 GitHub 检查更新失败。", exception);
        }
        catch (JsonException exception)
        {
            throw new UpdateException("GitHub 返回的发行版信息格式无效。", exception);
        }
    }

    public static new bool TryParseStableVersion(string? tag, out Version version) =>
        UpdateReleaseClientBase.TryParseStableVersion(tag, out version);

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
        var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/releases/download/";
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("GitHub 发行版文件下载地址不可信。");
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
