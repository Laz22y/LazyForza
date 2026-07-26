using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class UpdatePipelineTests
{
    [TestMethod]
    public async Task LatestStableReleaseSelectsExactWinX64AssetAndIgnoresCurrentVersion()
    {
        var packageBytes = Encoding.UTF8.GetBytes("package");
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var json = ReleaseJson("v1.2.3", packageBytes.Length, $"sha256:{hash}");
        using var http = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitHubReleaseClient.LatestReleaseApi
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new GitHubReleaseClient(http);

        var update = await client.CheckForUpdateAsync(new Version(1, 0, 0), CancellationToken.None);
        var current = await client.CheckForUpdateAsync(new Version(1, 2, 3), CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(new Version(1, 2, 3), update.Version);
        Assert.AreEqual("LazyForza-1.2.3-win-x64.zip", update.Package.Name);
        Assert.IsNull(current);
    }

    [TestMethod]
    public async Task DownloadRequiresOuterDigestAndVerifiedInternalManifest()
    {
        var root = CreateTempDirectory("lazyforza-update-download");
        try
        {
            var archive = BuildPackageArchive(new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("LazyForza 1.2.3"),
                ["README.txt"] = Encoding.UTF8.GetBytes("readme")
            });
            var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var packageUri = new Uri("https://github.com/Laz22y/LazyForza/releases/download/v1.2.3/LazyForza-1.2.3-win-x64.zip");
            using var http = new HttpClient(new FakeHttpHandler(request =>
            {
                if (request.RequestUri == packageUri)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var client = new GitHubReleaseClient(http);
            var release = new GitHubReleaseInfo(
                new Version(1, 2, 3),
                "v1.2.3",
                "LazyForza 1.2.3",
                string.Empty,
                new Uri("https://github.com/Laz22y/LazyForza/releases/tag/v1.2.3"),
                new GitHubReleaseAsset(
                    "LazyForza-1.2.3-win-x64.zip",
                    packageUri,
                    archive.Length,
                    $"sha256:{hash}"),
                null);

            var prepared = await client.DownloadAndPrepareAsync(
                release,
                root,
                new Progress<UpdateProgress>(),
                CancellationToken.None);

            Assert.IsTrue(File.Exists(Path.Combine(prepared.PackageRoot, "LazyForza.App.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(prepared.PackageRoot, "MANIFEST.sha256")));
            await UpdatePackageVerifier.VerifyManifestAsync(prepared.PackageRoot, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task LegacyReleaseCanUseSeparateSha256Asset()
    {
        var root = CreateTempDirectory("lazyforza-update-checksum");
        try
        {
            var archive = BuildPackageArchive(new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("LazyForza 1.2.3")
            });
            var packageName = "LazyForza-1.2.3-win-x64.zip";
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            var checksumBytes = Encoding.ASCII.GetBytes($"{hash}  {packageName}\n");
            var packageUri = new Uri($"https://github.com/Laz22y/LazyForza/releases/download/v1.2.3/{packageName}");
            var checksumUri = new Uri($"{packageUri}.sha256");
            using var http = new HttpClient(new FakeHttpHandler(request =>
            {
                if (request.RequestUri == packageUri)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
                if (request.RequestUri == checksumUri)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(checksumBytes) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var client = new GitHubReleaseClient(http);
            var release = new GitHubReleaseInfo(
                new Version(1, 2, 3),
                "v1.2.3",
                "LazyForza 1.2.3",
                string.Empty,
                new Uri("https://github.com/Laz22y/LazyForza/releases/tag/v1.2.3"),
                new GitHubReleaseAsset(packageName, packageUri, archive.Length, null),
                new GitHubReleaseAsset($"{packageName}.sha256", checksumUri, checksumBytes.Length, null));

            var prepared = await client.DownloadAndPrepareAsync(
                release,
                root,
                new Progress<UpdateProgress>(),
                CancellationToken.None);

            Assert.IsTrue(File.Exists(Path.Combine(prepared.PackageRoot, "LazyForza.App.exe")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task PackageExtractionAcceptsNestedManifestPaths()
    {
        var root = CreateTempDirectory("lazyforza-update-nested");
        try
        {
            var archivePath = Path.Combine(root, "nested.zip");
            await File.WriteAllBytesAsync(
                archivePath,
                BuildPackageArchive(new Dictionary<string, byte[]>
                {
                    ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
                    ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("LazyForza 1.2.3"),
                    ["zh-Hans\\PresentationCore.resources.dll"] = Encoding.UTF8.GetBytes("satellite")
                }));

            var packageRoot = await UpdatePackageVerifier.ExtractAndVerifyAsync(
                archivePath,
                Path.Combine(root, "extract"),
                CancellationToken.None);

            Assert.AreEqual(
                "satellite",
                await File.ReadAllTextAsync(
                    Path.Combine(packageRoot, "zh-Hans", "PresentationCore.resources.dll")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task PackageExtractionRejectsPathTraversal()
    {
        var root = CreateTempDirectory("lazyforza-update-traversal");
        try
        {
            var archivePath = Path.Combine(root, "malicious.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                await using var stream = entry.Open();
                await stream.WriteAsync(Encoding.UTF8.GetBytes("no"));
            }

            await Assert.ThrowsExactlyAsync<UpdateException>(() =>
                UpdatePackageVerifier.ExtractAndVerifyAsync(
                    archivePath,
                    Path.Combine(root, "extract"),
                    CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task InstallerOverwritesPackageFilesAndPreservesUnrelatedUserFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateTempDirectory("lazyforza-update-install");
        try
        {
            var target = Path.Combine(root, "target");
            var source = Path.Combine(root, "source");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(source);
            WritePackage(target, new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("old-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("old-build"),
                ["stale-runtime.dll"] = Encoding.UTF8.GetBytes("stale")
            });
            File.WriteAllText(Path.Combine(target, "user-note.txt"), "preserve");
            WritePackage(source, new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("new-build"),
                ["new-runtime.dll"] = Encoding.UTF8.GetBytes("new-runtime"),
                ["zh-Hans/PresentationCore.resources.dll"] = Encoding.UTF8.GetBytes("satellite")
            });

            var exitCode = await RunInstallerScriptAsync(source, target, work);

            Assert.AreEqual(0, exitCode, File.Exists(Path.Combine(work, "install.log"))
                ? File.ReadAllText(Path.Combine(work, "install.log"))
                : string.Empty);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(target, "LazyForza.App.exe")));
            Assert.AreEqual("new-runtime", File.ReadAllText(Path.Combine(target, "new-runtime.dll")));
            Assert.AreEqual(
                "satellite",
                File.ReadAllText(Path.Combine(target, "zh-Hans", "PresentationCore.resources.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(target, "stale-runtime.dll")));
            Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(target, "user-note.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(work, "install.complete")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task InstallerRollsBackWhenInstalledHashDoesNotMatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateTempDirectory("lazyforza-update-rollback");
        try
        {
            var target = Path.Combine(root, "target");
            var source = Path.Combine(root, "source");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(source);
            WritePackage(target, new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("old-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("old-build")
            });
            WritePackage(source, new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("new-build"),
                ["created.dll"] = Encoding.UTF8.GetBytes("created")
            });
            var manifestPath = Path.Combine(source, "MANIFEST.sha256");
            var lines = await File.ReadAllLinesAsync(manifestPath);
            lines[0] = new string('0', 64) + lines[0][64..];
            await File.WriteAllLinesAsync(manifestPath, lines);

            var exitCode = await RunInstallerScriptAsync(source, target, work);

            Assert.AreEqual(1, exitCode);
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(target, "LazyForza.App.exe")));
            Assert.AreEqual("old-build", File.ReadAllText(Path.Combine(target, "BUILDINFO.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(target, "created.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(work, "install.complete")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<int> RunInstallerScriptAsync(string source, string target, string work)
    {
        Directory.CreateDirectory(work);
        var prepared = new PreparedUpdate(
            new Version(1, 2, 3),
            work,
            source,
            Path.Combine(work, "package.zip"));
        using var process = WindowsUpdateLauncher.Launch(
            prepared,
            target,
            Path.Combine(work, "data"),
            int.MaxValue,
            noRestart: true);
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static byte[] BuildPackageArchive(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var manifest = Manifest(files);
            foreach (var (name, content) in files.Append(
                         new KeyValuePair<string, byte[]>("MANIFEST.sha256", Encoding.ASCII.GetBytes(manifest))))
            {
                var entry = archive.CreateEntry($"LazyForza-1.2.3-win-x64/{name}");
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    private static void WritePackage(string directory, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        File.WriteAllText(Path.Combine(directory, "MANIFEST.sha256"), Manifest(files), Encoding.ASCII);
    }

    private static string Manifest(IReadOnlyDictionary<string, byte[]> files) =>
        string.Join(
            Environment.NewLine,
            files.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Convert.ToHexString(SHA256.HashData(pair.Value))}  {pair.Key.Replace('\\', '/')}"));

    private static string ReleaseJson(string tag, int packageSize, string digest)
    {
        var version = tag.TrimStart('v');
        var name = $"LazyForza-{version}-win-x64.zip";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = $"LazyForza {version}",
            body = "Stable",
            html_url = $"https://github.com/Laz22y/LazyForza/releases/tag/{tag}",
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new
                {
                    name,
                    state = "uploaded",
                    size = packageSize,
                    digest,
                    browser_download_url = $"https://github.com/Laz22y/LazyForza/releases/download/{tag}/{name}"
                }
            }
        });
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
