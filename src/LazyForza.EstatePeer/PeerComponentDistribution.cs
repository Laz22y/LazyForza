using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.Update;

namespace LazyForza.EstatePeer;

public sealed record PeerComponentRelease(int SchemaVersion, long Sequence, string Channel,
    string MinimumClientVersion, string MaximumClientVersionExclusive, int RaceProtocolVersion,
    int ProjectFormatVersion, DateTimeOffset ExpiresAt, PeerComponentCatalog Catalog);

public sealed record PeerSignedComponent(string Payload, string Signature);
public sealed record PeerComponentCandidate(PeerSignedComponent Signed, PeerComponentRelease Release);

/// <summary>Authentication and compatibility are independent of the download mirror.</summary>
public sealed class PeerComponentDistribution(string publicKeyPem, string clientVersion, bool allowPreview)
{
    public const string Repository = "Laz22y/LazyForza.Components";
    public const string ManifestName = "estate-peer-component.signed.json";
    public const string TagPrefix = "estate-peer-host-v";
    public const int MaximumManifestBytes = 384 * 1024;

    public PeerComponentCandidate Verify(PeerSignedComponent signed, bool requireFresh = true)
    {
        try
        {
            if (signed.Payload.Length > MaximumManifestBytes || signed.Signature.Length > 256)
                throw new InvalidDataException("组件更新清单大小无效。");
            var payload = Convert.FromBase64String(signed.Payload);
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (!key.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256))
                throw new InvalidDataException("组件更新清单签名无效。");
            var release = JsonSerializer.Deserialize<PeerComponentRelease>(payload)
                ?? throw new InvalidDataException("组件更新清单为空。");
            if (release.SchemaVersion != 1 || release.Sequence <= 0 || release.Catalog is null ||
                release.Channel is not ("stable" or "preview") || !UpdateSemanticVersion.TryParse(release.Catalog.Version, out var version) ||
                !UpdateSemanticVersion.TryParse(release.MinimumClientVersion, out var minimum) ||
                !UpdateSemanticVersion.TryParse(release.MaximumClientVersionExclusive, out var maximum) ||
                minimum.CompareTo(maximum) >= 0)
                throw new InvalidDataException("组件更新清单不受支持。");
            if (release.RaceProtocolVersion != 2 || release.ProjectFormatVersion != 1 || release.Catalog.ControlVersion != 1 ||
                release.Catalog.Runtime != "win-x64") throw new PeerComponentIncompatibleException();
            new PeerComponentStore(Path.GetTempPath(), release.Catalog).ValidateCatalog();
            var urls = release.Catalog.DownloadUrls;
            if (urls is not { Count: > 0 and <= 2 }) throw new InvalidDataException("组件下载地址缺失。");
            foreach (var url in urls) ValidateAssetUri(url);
            if ((!allowPreview && (release.Channel != "stable" || version.IsPrerelease)) ||
                UpdateSemanticVersion.Parse(clientVersion).CompareTo(minimum) < 0 ||
                UpdateSemanticVersion.Parse(clientVersion).CompareTo(maximum) >= 0)
                throw new PeerComponentIncompatibleException();
            if (requireFresh && release.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("组件更新清单已过期，请重新检查更新。");
            return new(signed, release);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("组件更新清单无效或签名校验失败。", error);
        }
    }

    public async Task<PeerComponentCandidate?> CheckAsync(UpdateSourceKind preferredSource, long highestSequence,
        string installedVersion, HttpClient client, CancellationToken token, int installedRevision = 1)
    {
        token.ThrowIfCancellationRequested();
        Exception? failure = null;
        var checkedSource = false;
        var other = preferredSource == UpdateSourceKind.GitHub ? UpdateSourceKind.GitCode : UpdateSourceKind.GitHub;
        foreach (var source in new[] { preferredSource, other })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var tags = await ReadCandidateTagsAsync(source, installedVersion, installedRevision, client, timeout.Token);
                foreach (var (tag, _, _) in tags.OrderByDescending(item => item.Version).ThenByDescending(item => item.Revision).Take(8))
                {
                    try
                    {
                        var url = source == UpdateSourceKind.GitHub
                            ? $"https://github.com/{Repository}/releases/download/{tag}/{ManifestName}"
                            : $"https://api.gitcode.com/api/v5/repos/{Repository}/releases/{tag}/attach_files/{ManifestName}/download";
                        var signed = JsonSerializer.Deserialize<PeerSignedComponent>(await ReadAsync(client, new Uri(url), MaximumManifestBytes, timeout.Token))
                            ?? throw new InvalidDataException("组件更新清单为空。");
                        var candidate = Verify(signed);
                        if (tag != TagPrefix + candidate.Release.Catalog.ReleaseId || candidate.Release.Sequence < highestSequence)
                            throw new InvalidDataException("组件版本或更新序列校验失败。");
                        return candidate;
                    }
                    catch (PeerComponentIncompatibleException) { }
                }
                // Mirrors may publish at different times. An empty or incompatible list is not authoritative for the other source.
                checkedSource = true;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested(); failure = error;
            }
        }
        if (checkedSource) return null;
        throw new IOException("暂时无法检查房主组件更新，请稍后重试。", failure);
    }

    private async Task<List<(string Tag, UpdateSemanticVersion Version, int Revision)>> ReadCandidateTagsAsync(
        UpdateSourceKind source, string installedVersion, int installedRevision, HttpClient client, CancellationToken token)
    {
        const int pageSize = 100;
        const int maximumPages = 20;
        var installed = UpdateSemanticVersion.Parse(installedVersion);
        var tags = new List<(string Tag, UpdateSemanticVersion Version, int Revision)>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        var api = source == UpdateSourceKind.GitHub
            ? $"https://api.github.com/repos/{Repository}/releases"
            : $"https://api.gitcode.com/api/v5/repos/{Repository}/releases";
        for (var page = 1; page <= maximumPages; page++)
        {
            token.ThrowIfCancellationRequested();
            var uri = new Uri($"{api}?page={page}&per_page={pageSize}");
            using var document = JsonDocument.Parse(await ReadAsync(client, uri, 2 * 1024 * 1024, token));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("组件版本列表无效。");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                    !item.TryGetProperty("tag_name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                var tag = name.GetString()!;
                var revisionOffset = tag.LastIndexOf("-r", StringComparison.Ordinal);
                if (tag.StartsWith(TagPrefix, StringComparison.Ordinal) && tag.Length < 160 &&
                    revisionOffset > TagPrefix.Length && int.TryParse(tag[(revisionOffset + 2)..], out var revision) && revision > 0 &&
                    UpdateSemanticVersion.TryParse(tag[TagPrefix.Length..revisionOffset], out var version) &&
                    (allowPreview || !version.IsPrerelease) &&
                    (version.CompareTo(installed) > 0 || version.CompareTo(installed) == 0 && revision > installedRevision) &&
                    seenTags.Add(tag))
                    tags.Add((tag, version, revision));
            }
            if (document.RootElement.GetArrayLength() < pageSize) return tags;
        }
        throw new InvalidDataException("组件版本列表无效。");
    }

    internal static async Task<string> ReadAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LazyForza-Components/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(maxBytes, token).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    public static Uri ValidateAssetUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            !((uri.Host == "github.com" && (uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal) ||
                uri.AbsolutePath.StartsWith("/Laz22y/LazyForza/releases/download/", StringComparison.Ordinal))) ||
              (uri.Host == "api.gitcode.com" && uri.AbsolutePath.StartsWith($"/api/v5/repos/{Repository}/releases/", StringComparison.Ordinal) &&
                uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))))
            throw new InvalidDataException("组件下载来源无效。");
        return uri;
    }
}

public sealed class PeerComponentIncompatibleException() : IOException("此房主组件需要其他客户端版本或更新通道。");
