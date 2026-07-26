using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LazyForza.Update;

public static partial class UpdatePackageVerifier
{
    private const int MaxEntries = 5_000;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;

    public static async Task<string> ExtractAndVerifyAsync(
        string archivePath,
        string extractionRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionRoot);

        var root = Path.GetFullPath(extractionRoot);
        if (Directory.Exists(root))
            throw new UpdateException("更新包解压目录已存在。");
        Directory.CreateDirectory(root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count is 0 or > MaxEntries)
                throw new UpdateException("更新包文件数量异常。");

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(entry.FullName);
                if (relative.Length == 0)
                    throw new UpdateException("更新包包含无效路径。");

                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("更新包包含越界路径。");

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaxExpandedBytes)
                    throw new UpdateException("更新包解压后超过安全大小限制。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var target = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var packageRoot = FindPackageRoot(root);
            await VerifyManifestAsync(packageRoot, cancellationToken).ConfigureAwait(false);
            return packageRoot;
        }
        catch
        {
            TryDeleteDirectory(root);
            throw;
        }
    }

    public static async Task VerifyManifestAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(packageRoot);
        var manifestPath = Path.Combine(root, "MANIFEST.sha256");
        if (!File.Exists(manifestPath) ||
            !File.Exists(Path.Combine(root, "LazyForza.App.exe")) ||
            !File.Exists(Path.Combine(root, "BUILDINFO.txt")))
            throw new UpdateException("更新包缺少应用程序、构建信息或文件清单。");

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var line in await File.ReadAllLinesAsync(manifestPath, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = ManifestLineRegex().Match(line);
            if (!match.Success) throw new UpdateException("更新包文件清单格式无效。");
            var relative = NormalizeRelativePath(match.Groups["path"].Value);
            if (relative.Length == 0 ||
                string.Equals(relative, "MANIFEST.sha256", StringComparison.OrdinalIgnoreCase) ||
                !expected.TryAdd(relative, match.Groups["hash"].Value))
                throw new UpdateException("更新包文件清单包含无效或重复路径。");
        }

        if (expected.Count is 0 or > MaxEntries)
            throw new UpdateException("更新包文件清单为空或过大。");

        foreach (var (relative, expectedHash) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                throw new UpdateException($"更新包缺少清单文件：{relative}");
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(expectedHash, actual, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException($"更新包内部文件校验失败：{relative}");
        }

        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !string.Equals(path, "MANIFEST.sha256", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(expected.Keys))
            throw new UpdateException("更新包包含未列入清单的文件。");
    }

    private static string FindPackageRoot(string extractionRoot)
    {
        if (File.Exists(Path.Combine(extractionRoot, "LazyForza.App.exe"))) return extractionRoot;
        var directories = Directory.GetDirectories(extractionRoot);
        var files = Directory.GetFiles(extractionRoot);
        if (files.Length == 0 && directories.Length == 1 &&
            File.Exists(Path.Combine(directories[0], "LazyForza.App.exe")))
            return directories[0];
        throw new UpdateException("无法在更新包中找到唯一的 LazyForza 应用目录。");
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || Path.IsPathRooted(path))
            return string.Empty;
        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            return string.Empty;
        // Manifest keys must not depend on the host operating system's directory separator.
        return string.Join('/', segments);
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

    [GeneratedRegex(@"^(?<hash>[0-9A-Fa-f]{64})[ \t]+\*?(?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestLineRegex();
}
