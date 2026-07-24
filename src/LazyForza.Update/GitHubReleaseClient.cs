using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LazyForza.Update;

public sealed partial class GitHubReleaseClient : IDisposable
{
    public const string RepositoryOwner = "Laz22y";
    public const string RepositoryName = "LazyForza";
    public static readonly Uri LatestReleaseApi =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly bool disposeClient;

    public GitHubReleaseClient()
        : this(CreateHttpClient(), true)
    {
    }

    public GitHubReleaseClient(HttpClient httpClient, bool disposeClient = false)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.disposeClient = disposeClient;
    }

    public async Task<GitHubReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = CreateRequest(HttpMethod.Get, LatestReleaseApi);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitHub 返回了 HTTP {(int)response.StatusCode}，暂时无法检查更新。");

        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(
            content,
            cancellationToken: timeout.Token).ConfigureAwait(false)
            ?? throw new UpdateException("GitHub 返回的发行版信息为空。");

        if (release.Draft || release.Prerelease)
            throw new UpdateException("GitHub latest 指向了草稿或预发行版本，已拒绝更新。");
        if (!TryParseStableVersion(release.TagName, out var version))
            throw new UpdateException($"无法识别发行版标签“{release.TagName}”。仅支持 v主版本.次版本.修订号。");
        if (version.CompareTo(NormalizeVersion(currentVersion)) <= 0) return null;

        var expectedName = $"LazyForza-{version.ToString(3)}-win-x64.zip";
        var packageCandidates = release.Assets.Where(asset =>
            string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (packageCandidates.Length != 1)
            throw new UpdateException($"发行版必须且只能包含一个预期文件 {expectedName}。");
        var packageDto = packageCandidates[0];
        if (packageDto.Size is <= 0 or > MaxArchiveBytes)
            throw new UpdateException($"发行版文件大小异常：{packageDto.Size:N0} 字节。");

        var package = ToAsset(packageDto);
        ValidateReleaseDownloadUri(package.DownloadUri);
        var checksumCandidates = release.Assets.Where(asset =>
            string.Equals(asset.Name, $"{expectedName}.sha256", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (checksumCandidates.Length > 1)
            throw new UpdateException("发行版包含重复的校验和文件。");
        var checksumDto = checksumCandidates.SingleOrDefault();
        var checksum = checksumDto is null ? null : ToAsset(checksumDto);
        if (checksum is not null) ValidateReleaseDownloadUri(checksum.DownloadUri);
        if (!TryParseSha256Digest(package.Digest, out _) && checksum is null)
            throw new UpdateException("发行版既没有 GitHub SHA-256 摘要，也没有校验和文件。");

        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var pageUri) ||
            pageUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(pageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("发行版页面地址不可信。");

        return new GitHubReleaseInfo(
            version,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            release.Body ?? string.Empty,
            pageUri,
            package,
            checksum);
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        GitHubReleaseInfo release,
        string updatesRoot,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        var expectedName = $"LazyForza-{release.Version.ToString(3)}-win-x64.zip";
        if (!string.Equals(release.Package.Name, expectedName, StringComparison.OrdinalIgnoreCase) ||
            release.Package.Size is <= 0 or > MaxArchiveBytes)
            throw new UpdateException("待下载的发行版文件与版本信息不匹配。");
        ValidateReleaseDownloadUri(release.Package.DownloadUri);
        if (release.Checksum is not null) ValidateReleaseDownloadUri(release.Checksum.DownloadUri);

        Directory.CreateDirectory(updatesRoot);
        var workDirectory = Path.Combine(
            Path.GetFullPath(updatesRoot),
            $"{release.Version.ToString(3)}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            var archivePath = Path.Combine(workDirectory, release.Package.Name);
            progress?.Report(new UpdateProgress("正在下载更新…", 0, release.Package.Size));
            await DownloadFileAsync(
                release.Package.DownloadUri,
                archivePath,
                release.Package.Size,
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new UpdateProgress("正在校验下载文件…"));
            var expectedHash = await ResolveExpectedHashAsync(release, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new UpdateException("更新包 SHA-256 校验失败，文件可能不完整或已被篡改。");

            var extractionRoot = Path.Combine(workDirectory, "package");
            progress?.Report(new UpdateProgress("正在验证更新包…"));
            var packageRoot = await UpdatePackageVerifier.ExtractAndVerifyAsync(
                archivePath,
                extractionRoot,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateProgress("更新已准备好。", 1, 1));
            return new PreparedUpdate(release.Version, workDirectory, packageRoot, archivePath);
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposeClient) httpClient.Dispose();
    }

    public static bool TryParseStableVersion(string? tag, out Version version)
    {
        var match = StableVersionRegex().Match(tag?.Trim() ?? string.Empty);
        if (match.Success &&
            int.TryParse(match.Groups["major"].Value, out var major) &&
            int.TryParse(match.Groups["minor"].Value, out var minor) &&
            int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            version = new Version(major, minor, patch);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string destination,
        long expectedSize,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"下载更新时 GitHub 返回了 HTTP {(int)response.StatusCode}。");

        var responseSize = response.Content.Headers.ContentLength;
        if (responseSize is > MaxArchiveBytes)
            throw new UpdateException("更新包超过安全大小限制。");
        if (responseSize is > 0 && expectedSize > 0 && responseSize != expectedSize)
            throw new UpdateException("GitHub 返回的更新包大小与发行版信息不一致。");

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 128);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaxArchiveBytes)
                    throw new UpdateException("更新包超过安全大小限制。");
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                progress?.Report(new UpdateProgress("正在下载更新…", total, expectedSize));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (expectedSize > 0 && total != expectedSize)
            throw new UpdateException($"更新包下载不完整：预期 {expectedSize:N0} 字节，实际 {total:N0} 字节。");
    }

    private async Task<byte[]> ResolveExpectedHashAsync(
        GitHubReleaseInfo release,
        CancellationToken cancellationToken)
    {
        if (TryParseSha256Digest(release.Package.Digest, out var digest)) return digest;
        if (release.Checksum is null)
            throw new UpdateException("更新包缺少 SHA-256 校验信息。");
        if (release.Checksum.Size is <= 0 or > 4096)
            throw new UpdateException("更新包校验和文件大小异常。");

        using var request = CreateRequest(HttpMethod.Get, release.Checksum.DownloadUri);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"下载校验和时 GitHub 返回了 HTTP {(int)response.StatusCode}。");
        if (response.Content.Headers.ContentLength is > 4096)
            throw new UpdateException("更新包校验和文件超过安全大小限制。");
        var checksumBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (checksumBytes.Length > 4096)
            throw new UpdateException("更新包校验和文件超过安全大小限制。");
        var text = Encoding.UTF8.GetString(checksumBytes);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !string.Equals(parts[^1].TrimStart('*'), release.Package.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (parts[0].Length == 64)
            {
                try
                {
                    var hash = Convert.FromHexString(parts[0]);
                    if (hash.Length == 32) return hash;
                }
                catch (FormatException)
                {
                }
            }
        }

        throw new UpdateException("更新包校验和文件格式无效。");
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("LazyForza-Updater/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

    private static GitHubReleaseAsset ToAsset(AssetResponse asset)
    {
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri))
            throw new UpdateException($"发行版文件 {asset.Name} 的下载地址无效。");
        return new GitHubReleaseAsset(asset.Name, uri, asset.Size, asset.Digest);
    }

    private static void ValidateReleaseDownloadUri(Uri uri)
    {
        var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/releases/download/";
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("发行版文件下载地址不可信。");
    }

    private static Version NormalizeVersion(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static bool TryParseSha256Digest(string? digest, out byte[] hash)
    {
        const string prefix = "sha256:";
        if (digest?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            var hex = digest[prefix.Length..].Trim();
            if (hex.Length == 64)
            {
                try
                {
                    hash = Convert.FromHexString(hex);
                    return true;
                }
                catch (FormatException)
                {
                }
            }
        }

        hash = [];
        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionRegex();

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
