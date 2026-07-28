using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LazyForza.Update;

public abstract partial class UpdateReleaseClientBase : IDisposable
{
    protected const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private readonly bool disposeClient;

    protected UpdateReleaseClientBase(HttpClient httpClient, bool disposeClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.disposeClient = disposeClient;
    }

    public abstract UpdateSourceKind Source { get; }

    public string SourceName => Source switch
    {
        UpdateSourceKind.GitCode => "GitCode",
        UpdateSourceKind.GitHub => "GitHub",
        _ => Source.ToString()
    };

    protected HttpClient HttpClient { get; }

    public abstract Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken);

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateReleaseInfo release,
        string updatesRoot,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        if (release.Source != Source)
            throw new UpdateException(
                $"发行版来源为 {release.SourceName}，不能交给 {SourceName} 下载器处理。");

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
            progress?.Report(new UpdateProgress(
                $"正在从 {SourceName} 下载更新…",
                0,
                release.Package.Size));
            await DownloadFileAsync(
                release.Package.DownloadUri,
                archivePath,
                release.Package.Size,
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new UpdateProgress("正在校验下载文件…"));
            var expectedHash = await ResolveExpectedHashAsync(release, cancellationToken)
                .ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new UpdateException(
                    $"{SourceName} 更新包 SHA-256 校验失败，文件可能不完整或已被篡改。");

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
        if (disposeClient) HttpClient.Dispose();
        GC.SuppressFinalize(this);
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

    protected static Version NormalizeVersion(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));

    protected static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

    protected virtual HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LazyForza-Updater/1.1");
        return request;
    }

    protected abstract void ValidateReleaseDownloadUri(Uri uri);

    protected static UpdateReleaseAsset ToAsset(
        string name,
        string? downloadUrl,
        long? size,
        string? digest)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            throw new UpdateException($"发行版文件 {name} 的下载地址无效。");
        var normalizedSize = size is > 0 ? size : null;
        return new UpdateReleaseAsset(name, uri, normalizedSize, digest);
    }

    protected static bool TryParseSha256Digest(string? digest, out byte[] hash)
    {
        const string prefix = "sha256:";
        var hex = digest?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? digest[prefix.Length..].Trim()
            : digest?.Trim();
        if (hex?.Length == 64)
        {
            try
            {
                hash = Convert.FromHexString(hex);
                return hash.Length == 32;
            }
            catch (FormatException)
            {
            }
        }

        hash = [];
        return false;
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string destination,
        long? expectedSize,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(
                    $"下载更新时 {SourceName} 返回了 HTTP {(int)response.StatusCode}。");

            var responseSize = response.Content.Headers.ContentLength;
            if (responseSize is > MaxArchiveBytes)
                throw new UpdateException("更新包超过安全大小限制。");
            if (responseSize is > 0 && expectedSize is > 0 && responseSize != expectedSize)
                throw new UpdateException(
                    $"{SourceName} 返回的更新包大小与发行版信息不一致。");

            var progressTotal = expectedSize ?? responseSize;
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
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
                    var read = await source.ReadAsync(buffer.AsMemory(), timeout.Token)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxArchiveBytes)
                        throw new UpdateException("更新包超过安全大小限制。");
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token)
                        .ConfigureAwait(false);
                    progress?.Report(new UpdateProgress(
                        $"正在从 {SourceName} 下载更新…",
                        total,
                        progressTotal));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (expectedSize is > 0 && total != expectedSize)
                throw new UpdateException(
                    $"更新包下载不完整：预期 {expectedSize:N0} 字节，实际 {total:N0} 字节。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"连接 {SourceName} 下载更新超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException($"连接 {SourceName} 下载更新失败。", exception);
        }
    }

    private async Task<byte[]> ResolveExpectedHashAsync(
        UpdateReleaseInfo release,
        CancellationToken cancellationToken)
    {
        if (TryParseSha256Digest(release.Package.Digest, out var digest)) return digest;
        if (release.Checksum is null)
            throw new UpdateException($"{SourceName} 发行版缺少 SHA-256 校验信息。");
        if (release.Checksum.Size is > 4096)
            throw new UpdateException("更新包校验和文件大小异常。");

        try
        {
            using var request = CreateRequest(HttpMethod.Get, release.Checksum.DownloadUri);
            using var response = await HttpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(
                    $"下载校验和时 {SourceName} 返回了 HTTP {(int)response.StatusCode}。");
            if (response.Content.Headers.ContentLength is > 4096)
                throw new UpdateException("更新包校验和文件超过安全大小限制。");
            var checksumBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (checksumBytes.Length > 4096)
                throw new UpdateException("更新包校验和文件超过安全大小限制。");
            var text = Encoding.UTF8.GetString(checksumBytes);
            foreach (var line in text.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 ||
                    !string.Equals(
                        parts[^1].TrimStart('*'),
                        release.Package.Name,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryParseSha256Digest(parts[0], out var hash)) return hash;
            }
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException($"连接 {SourceName} 下载校验和失败。", exception);
        }

        throw new UpdateException("更新包校验和文件格式无效。");
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
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

    [GeneratedRegex(
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionRegex();
}
