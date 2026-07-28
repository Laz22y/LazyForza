using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyForza.Update;

public sealed class GitCodeReleaseClient : UpdateReleaseClientBase
{
    public const string RepositoryOwner = "Laz22y";
    public const string RepositoryName = "LazyForza";
    public static readonly Uri LatestReleaseApi =
        new($"https://api.gitcode.com/api/v5/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
    public static readonly Uri RepositoryPage =
        new($"https://gitcode.com/{RepositoryOwner}/{RepositoryName}");

    private static readonly HashSet<string> TrustedDownloadHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "api.gitcode.com",
            "gitcode.com",
            "raw.gitcode.com",
            "file-cdn.gitcode.com"
        };

    public GitCodeReleaseClient()
        : this(CreateHttpClient(), true)
    {
    }

    public GitCodeReleaseClient(HttpClient httpClient, bool disposeClient = false)
        : base(httpClient, disposeClient)
    {
    }

    public override UpdateSourceKind Source => UpdateSourceKind.GitCode;

    public override async Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var request = CreateRequest(HttpMethod.Get, LatestReleaseApi);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(
                    $"GitCode 返回了 HTTP {(int)response.StatusCode}，暂时无法检查更新。");

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(
                content,
                cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new UpdateException("GitCode 返回的发行版信息为空。");

            if (release.Prerelease)
                throw new UpdateException("GitCode latest 指向了预发行版本，已拒绝更新。");
            if (!TryParseStableVersion(release.TagName, out var version))
                throw new UpdateException(
                    $"无法识别发行版标签“{release.TagName}”。仅支持 v主版本.次版本.修订号。");
            if (version.CompareTo(NormalizeVersion(currentVersion)) <= 0) return null;

            var expectedName = $"LazyForza-{version.ToString(3)}-win-x64.zip";
            var packageCandidates = release.Assets.Where(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packageCandidates.Length != 1)
                throw new UpdateException(
                    $"GitCode 发行版必须且只能包含一个预期文件 {expectedName}。");
            var packageDto = packageCandidates[0];
            if (packageDto.Size is > MaxArchiveBytes)
                throw new UpdateException(
                    $"GitCode 发行版文件大小异常：{packageDto.Size:N0} 字节。");
            var packageMetadata = ToAsset(
                packageDto.Name,
                packageDto.BrowserDownloadUrl,
                packageDto.Size,
                packageDto.Digest ?? packageDto.Sha256);
            ValidateReleaseDownloadUri(packageMetadata.DownloadUri);
            var package = packageMetadata with
            {
                DownloadUri = CreateAttachmentDownloadUri(
                    release.TagName,
                    packageDto.Name)
            };
            ValidateReleaseDownloadUri(package.DownloadUri);

            var checksumCandidates = release.Assets.Where(asset =>
                string.Equals(
                    asset.Name,
                    $"{expectedName}.sha256",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (checksumCandidates.Length > 1)
                throw new UpdateException("GitCode 发行版包含重复的校验和文件。");
            var checksumDto = checksumCandidates.SingleOrDefault();
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
                    $"GitCode 发行版缺少 {expectedName}.sha256，无法安全验证下载文件。");

            return new UpdateReleaseInfo(
                version,
                release.TagName,
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                release.Body ?? string.Empty,
                RepositoryPage,
                package,
                checksum,
                Source);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("连接 GitCode 检查更新超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException("连接 GitCode 检查更新失败。", exception);
        }
        catch (JsonException exception)
        {
            throw new UpdateException("GitCode 返回的发行版信息格式无效。", exception);
        }
    }

    protected override void ValidateReleaseDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !TrustedDownloadHosts.Contains(uri.Host))
            throw new UpdateException("GitCode 发行版文件下载地址不可信。");

        if (string.Equals(uri.Host, "raw.gitcode.com", StringComparison.OrdinalIgnoreCase))
        {
            var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/";
            if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("GitCode 原始文件下载地址不属于 LazyForza 仓库。");
        }

        if (string.Equals(uri.Host, "api.gitcode.com", StringComparison.OrdinalIgnoreCase))
        {
            var browserPrefix =
                $"/{RepositoryOwner}/{RepositoryName}/releases/download/";
            var attachmentPrefix =
                $"/api/v5/repos/{RepositoryOwner}/{RepositoryName}/releases/";
            var isBrowserDownload =
                uri.AbsolutePath.StartsWith(browserPrefix, StringComparison.OrdinalIgnoreCase);
            var isAttachmentApi =
                uri.AbsolutePath.StartsWith(attachmentPrefix, StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Contains("/attach_files/", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.EndsWith("/download", StringComparison.OrdinalIgnoreCase);
            if (!isBrowserDownload && !isAttachmentApi)
                throw new UpdateException("GitCode 发行附件地址不属于 LazyForza 仓库。");
        }

        if (string.Equals(uri.Host, "gitcode.com", StringComparison.OrdinalIgnoreCase))
        {
            var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/";
            if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("GitCode 文件下载地址不属于 LazyForza 仓库。");
        }
    }

    private static Uri CreateAttachmentDownloadUri(string tag, string fileName) =>
        new(
            $"https://api.gitcode.com/api/v5/repos/{RepositoryOwner}/{RepositoryName}" +
            $"/releases/{Uri.EscapeDataString(tag)}/attach_files/" +
            $"{Uri.EscapeDataString(fileName)}/download");

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
