using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LazyForza.EstatePeer;

public sealed record PeerComponentFile(string Name, long Size, string Sha256);
public sealed record PeerComponentCatalog(int FormatVersion, int ControlVersion, string Version, string Runtime,
    long DownloadBytes, string ArchiveSha256, string? DownloadUrl, IReadOnlyList<PeerComponentFile> Files);

/// <summary>Only installs the component hash shipped with this client; imported archives cannot choose code to trust.</summary>
public sealed class PeerComponentStore(string root, PeerComponentCatalog catalog)
{
    public const string ExecutableName = "LazyForza.EstatePeer.Host.exe";
    public string InstallDirectory => Path.Combine(root, catalog.ArchiveSha256);
    public long InstalledBytes => catalog.Files.Sum(file => file.Size);
    public PeerComponentCatalog Catalog => catalog;

    public async Task<string?> FindVerifiedExecutableAsync(CancellationToken token)
    {
        ValidateCatalog();
        if (!Directory.Exists(InstallDirectory)) return null;
        EnsureOrdinaryDirectory();
        foreach (var file in catalog.Files)
        {
            var path = Path.Combine(InstallDirectory, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            await using var input = File.OpenRead(path);
            if (!Matches(await SHA256.HashDataAsync(input, token), file.Sha256)) return null;
        }
        if ((File.GetAttributes(InstallDirectory) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(InstallDirectory).Count() != catalog.Files.Count) return null;
        return Path.Combine(InstallDirectory, ExecutableName);
    }

    public async Task InstallAsync(string archivePath, CancellationToken token)
    {
        ValidateCatalog();
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != catalog.DownloadBytes || !Matches(await SHA256.HashDataAsync(input, token), catalog.ArchiveSha256))
            throw new InvalidDataException("组件包与当前客户端要求的版本或 SHA-256 不一致。");
        input.Position = 0;
        Directory.CreateDirectory(root);
        EnsureOrdinaryDirectory();
        var staging = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count != catalog.Files.Count) throw new InvalidDataException("组件文件清单不一致。");
            var entries = zip.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
            foreach (var file in catalog.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(file.Name, out var entry) || entry.Length != file.Size)
                    throw new InvalidDataException("组件文件缺失或大小不一致。");
                await using var source = entry.Open();
                await using var destination = new FileStream(Path.Combine(staging, file.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > file.Size) throw new InvalidDataException("组件解压大小超过清单。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (total != file.Size) throw new InvalidDataException("组件文件不完整。");
            }
            foreach (var file in catalog.Files)
            {
                await using var source = File.OpenRead(Path.Combine(staging, file.Name));
                if (!Matches(await SHA256.HashDataAsync(source, token), file.Sha256)) throw new InvalidDataException("组件文件校验失败。");
            }
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(InstallDirectory))
                throw new IOException("该组件版本已存在；请先卸载损坏的组件再安装。");
            Directory.Move(staging, InstallDirectory);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    public async Task DownloadAndInstallAsync(IProgress<double>? progress, CancellationToken token)
    {
        ValidateCatalog();
        if (catalog.DownloadUrl is null) throw new InvalidOperationException("房主组件尚未发布下载，请使用与此客户端匹配的离线包。");
        var uri = new Uri(catalog.DownloadUrl, UriKind.Absolute);
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/Laz22y/LazyForza/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("组件下载来源无效。");
        Directory.CreateDirectory(root);
        var download = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } size && size != catalog.DownloadBytes)
                throw new InvalidDataException("组件下载大小不一致。");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > catalog.DownloadBytes) throw new InvalidDataException("组件下载超过预期大小。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report((double)total / catalog.DownloadBytes);
                }
            }
            await InstallAsync(download, token);
        }
        finally { if (File.Exists(download)) File.Delete(download); }
    }

    public void Uninstall()
    {
        ValidateCatalog();
        if (!Directory.Exists(InstallDirectory)) return;
        EnsureOrdinaryDirectory();
        if (Directory.EnumerateFileSystemEntries(InstallDirectory)
            .Any(path => (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new IOException("组件目录不能包含链接。");
        // Delete only this immutable content-addressed component, never the separate room data directory.
        Directory.Delete(InstallDirectory, recursive: true);
    }

    private void ValidateCatalog()
    {
        if (catalog.FormatVersion != 1 || catalog.ControlVersion != 1 || catalog.Runtime != "win-x64" ||
            !ValidHash(catalog.ArchiveSha256) || catalog.DownloadBytes is < 1 or > 268_435_456 ||
            catalog.Files.Count is < 1 or > 512 || catalog.Files.Sum(file => file.Size) > 536_870_912 ||
            catalog.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Files.Count ||
            !catalog.Files.Any(file => file.Name == ExecutableName) ||
            catalog.Files.Any(file => file.Size is < 0 or > 134_217_728 || !ValidHash(file.Sha256) ||
                string.IsNullOrWhiteSpace(file.Name) || file.Name != Path.GetFileName(file.Name) || file.Name.Contains(':') ||
                file.Name is "." or ".." || file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                file.Name.EndsWith(' ') || file.Name.EndsWith('.') ||
                System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("房主组件清单不受支持。");
    }

    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private void EnsureOrdinaryDirectory()
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(InstallDirectory)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("组件目录不能位于链接路径中。");
    }
    private static bool Matches(byte[] hash, string expected) => CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    public static PeerComponentCatalog? ReadCatalog(Stream stream) => JsonSerializer.Deserialize<PeerComponentCatalog>(stream);
}
