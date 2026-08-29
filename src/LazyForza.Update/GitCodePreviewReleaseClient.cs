using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.Update;

public sealed class GitCodePreviewReleaseClient : UpdateReleaseClientBase
{
    public static readonly Uri ReleasesApi = new(
        $"https://api.gitcode.com/api/v5/repos/{GitCodeReleaseClient.RepositoryOwner}/{GitCodeReleaseClient.RepositoryName}/releases");

    private static readonly HashSet<string> TrustedDownloadHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "api.gitcode.com",
            "gitcode.com",
            "raw.gitcode.com",
            "file-cdn.gitcode.com"
        };

    public GitCodePreviewReleaseClient()
        : this(CreateHttpClient(), true)
    {
    }

    public GitCodePreviewReleaseClient(HttpClient httpClient, bool disposeClient = false)
        : base(httpClient, disposeClient)
    {
    }

    public override UpdateSourceKind Source => UpdateSourceKind.GitCode;

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
                    $"GitCode 返回了 HTTP {(int)response.StatusCode}，暂时无法检查预览版更新。");

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<ReleaseResponse[]>(
                    content,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false) ?? throw new UpdateException("GitCode 返回的预览版列表为空。");

            var selected = releases
                .Select(release => new
                {
                    Release = release,
                    Parsed = UpdateSemanticVersion.TryParse(release.TagName, out var version)
                        ? version
                        : null
                })
                .Where(candidate =>
                    candidate.Parsed is not null &&
                    candidate.Parsed.IsPrerelease == candidate.Release.Prerelease &&
                    candidate.Parsed.CompareTo(currentVersion) > 0)
                .OrderByDescending(candidate => candidate.Parsed)
                .FirstOrDefault();
            if (selected?.Parsed is null) return null;

            var release = selected.Release;
            var version = selected.Parsed;
            var expectedName = $"LazyForza-{version}-win-x64.zip";
            var packages = release.Assets.Where(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packages.Length != 1)
                throw new UpdateException(
                    $"GitCode 预览版必须且只能包含一个预期文件 {expectedName}。");
            var packageDto = packages[0];
            if (packageDto.Size is <= 0 or > MaxArchiveBytes)
                throw new UpdateException(
                    $"GitCode 预览版文件大小异常：{packageDto.Size:N0} 字节。");
            var packageMetadata = ToAsset(
                packageDto.Name,
                packageDto.BrowserDownloadUrl,
                packageDto.Size,
                packageDto.Digest ?? packageDto.Sha256);
            ValidateReleaseDownloadUri(packageMetadata.DownloadUri);
            var package = packageMetadata with
            {
                DownloadUri = CreateAttachmentDownloadUri(release.TagName, packageDto.Name)
            };
            ValidateReleaseDownloadUri(package.DownloadUri);

            var checksumDtos = release.Assets.Where(asset =>
                string.Equals(
                    asset.Name,
                    $"{expectedName}.sha256",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (checksumDtos.Length > 1)
                throw new UpdateException("GitCode 预览版包含重复的校验和文件。");
            var checksumDto = checksumDtos.SingleOrDefault();
            var checksumMetadata = checksumDto is null
                ? null
                : ToAsset(
                    checksumDto.Name,
                    checksumDto.BrowserDownloadUrl,
                    checksumDto.Size,
                    checksumDto.Digest ?? checksumDto.Sha256);
            if (checksumMetadata is not null)
                ValidateReleaseDownloadUri(checksumMetadata.DownloadUri);
            var checksum = checksumMetadata is null
                ? null
                : checksumMetadata with
                {
                    DownloadUri = CreateAttachmentDownloadUri(
                        release.TagName,
                        checksumMetadata.Name)
                };
            if (checksum is not null) ValidateReleaseDownloadUri(checksum.DownloadUri);
            if (!TryParseSha256Digest(package.Digest, out _) && checksum is null)
                throw new UpdateException(
                    $"GitCode 预览版缺少 {expectedName}.sha256，无法安全验证下载文件。");

            var metadata = UpdateReleaseMetadata.Parse(
                release.Body,
                currentVersion.NumericVersion,
                version.NumericVersion);
            return new UpdateReleaseInfo(
                version.NumericVersion,
                release.TagName,
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                metadata.Notes,
                GitCodeReleaseClient.RepositoryPage,
                package,
                checksum,
                Source,
                metadata.Type,
                VersionLabel: version.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("连接 GitCode 检查预览版更新超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException("连接 GitCode 检查预览版更新失败。", exception);
        }
        catch (JsonException exception)
        {
            throw new UpdateException("GitCode 返回的预览版信息格式无效。", exception);
        }
    }

    protected override void ValidateReleaseDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !TrustedDownloadHosts.Contains(uri.Host))
            throw new UpdateException("GitCode 预览版文件下载地址不可信。");

        if (string.Equals(uri.Host, "api.gitcode.com", StringComparison.OrdinalIgnoreCase))
        {
            var browserPrefix =
                $"/{GitCodeReleaseClient.RepositoryOwner}/{GitCodeReleaseClient.RepositoryName}/releases/download/";
            var attachmentPrefix =
                $"/api/v5/repos/{GitCodeReleaseClient.RepositoryOwner}/{GitCodeReleaseClient.RepositoryName}/releases/";
            var isBrowserDownload =
                uri.AbsolutePath.StartsWith(browserPrefix, StringComparison.OrdinalIgnoreCase);
            var isAttachmentApi =
                uri.AbsolutePath.StartsWith(attachmentPrefix, StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Contains("/attach_files/", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.EndsWith("/download", StringComparison.OrdinalIgnoreCase);
            if (!isBrowserDownload && !isAttachmentApi)
                throw new UpdateException("GitCode 预览版附件地址不属于 LazyForza 仓库。");
        }
        else if (!string.Equals(uri.Host, "file-cdn.gitcode.com", StringComparison.OrdinalIgnoreCase))
        {
            var expectedPrefix =
                $"/{GitCodeReleaseClient.RepositoryOwner}/{GitCodeReleaseClient.RepositoryName}/";
            if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("GitCode 预览版文件地址不属于 LazyForza 仓库。");
        }
    }

    private static Uri CreateAttachmentDownloadUri(string tag, string fileName) =>
        new(
            $"https://api.gitcode.com/api/v5/repos/{GitCodeReleaseClient.RepositoryOwner}/" +
            $"{GitCodeReleaseClient.RepositoryName}/releases/{Uri.EscapeDataString(tag)}/" +
            $"attach_files/{Uri.EscapeDataString(fileName)}/download");

    private sealed class ReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public AssetResponse[] Assets { get; init; } = [];
    }

    private sealed class AssetResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long? Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
    }
}
