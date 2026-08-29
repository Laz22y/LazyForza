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
    public void SemanticVersionOrdersPreviewIdentifiersAndStableRelease()
    {
        var alpha1 = UpdateSemanticVersion.Parse("1.5.1-alpha-1");
        var alpha2 = UpdateSemanticVersion.Parse("v1.5.1-alpha-2");
        var alpha10 = UpdateSemanticVersion.Parse("1.5.1-alpha-10+build.5");
        var beta1 = UpdateSemanticVersion.Parse("1.5.1-beta-1");
        var stable = UpdateSemanticVersion.Parse("1.5.1");

        Assert.IsTrue(alpha1.CompareTo(alpha2) < 0);
        Assert.IsTrue(alpha2.CompareTo(alpha10) < 0);
        Assert.IsTrue(alpha10.CompareTo(beta1) < 0);
        Assert.IsTrue(beta1.CompareTo(stable) < 0);
        Assert.AreEqual("1.5.1-alpha-10", alpha10.ToString());
        Assert.IsFalse(UpdateSemanticVersion.TryParse("1.5.1-alpha.01", out _));
    }

    [TestMethod]
    public async Task PreviewChannelSelectsHighestPublishedSemanticPreview()
    {
        var json = PreviewReleaseListJson(
            ("v1.5.1-alpha-2", true, false),
            ("v1.5.1-alpha-10", true, false),
            ("v1.5.1-alpha-99", true, true),
            ("v1.5.0", false, false));
        using var http = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitHubPreviewReleaseClient.ReleasesApi
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new GitHubPreviewReleaseClient(http);

        var update = await client.CheckForUpdateAsync(
            UpdateSemanticVersion.Parse("1.5.1-alpha-1"),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual("1.5.1-alpha-10", update.VersionLabel);
        Assert.AreEqual("1.5.1-alpha-10", update.ArtifactVersion);
        Assert.AreEqual(
            "LazyForza-1.5.1-alpha-10-win-x64.zip",
            update.Package.Name);
        Assert.IsNull(update.Installer);
    }

    [TestMethod]
    public async Task PreviewChannelMovesToMatchingStableReleaseAndStableChannelStaysSeparate()
    {
        var previewJson = PreviewReleaseListJson(
            ("v1.5.1-alpha-8", true, false),
            ("v1.5.1", false, false));
        var stableJson = ReleaseJson(
            "v1.5.1",
            packageSize: 7,
            digest: $"sha256:{new string('a', 64)}");
        var previewRequests = 0;
        var stableRequests = 0;
        using var http = new HttpClient(new FakeHttpHandler(request =>
        {
            if (request.RequestUri == GitHubPreviewReleaseClient.ReleasesApi)
            {
                previewRequests++;
                return JsonResponse(previewJson);
            }
            if (request.RequestUri == GitHubReleaseClient.LatestReleaseApi)
            {
                stableRequests++;
                return JsonResponse(stableJson);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var preview = new GitHubPreviewReleaseClient(http);
        using var stable = new GitHubReleaseClient(http);

        var previewUpdate = await preview.CheckForUpdateAsync(
            UpdateSemanticVersion.Parse("1.5.1-alpha-7"),
            CancellationToken.None);
        var stableUpdate = await stable.CheckForUpdateAsync(
            new Version(1, 5, 0),
            CancellationToken.None);

        Assert.IsNotNull(previewUpdate);
        Assert.AreEqual("1.5.1", previewUpdate.ArtifactVersion);
        Assert.IsNotNull(stableUpdate);
        Assert.AreEqual("1.5.1", stableUpdate.ArtifactVersion);
        Assert.AreEqual(1, previewRequests);
        Assert.AreEqual(1, stableRequests);
    }

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
    public async Task GitHubReleaseParsesExplicitUpdateTypeAndHidesMetadataMarker()
    {
        var packageBytes = Encoding.UTF8.GetBytes("package");
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var json = ReleaseJson(
            "v1.4.0",
            packageBytes.Length,
            $"sha256:{hash}",
            "<!-- lazyforza-update-type: major-feature -->\n\n## 更新内容\n\n- 新增功能");
        using var http = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitHubReleaseClient.LatestReleaseApi
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new GitHubReleaseClient(http);

        var update = await client.CheckForUpdateAsync(
            new Version(1, 3, 1),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(UpdateReleaseType.MajorFeature, update.Type);
        Assert.AreEqual("重大功能更新", update.Type.DisplayName());
        Assert.IsFalse(update.Notes.Contains("lazyforza-update-type", StringComparison.Ordinal));
        StringAssert.Contains(update.Notes, "新增功能");
    }

    [TestMethod]
    public async Task InstalledUpdateDownloadsVerifiedSetupWithoutExtractingPortablePackage()
    {
        var root = CreateTempDirectory("lazyforza-installed-update");
        try
        {
            var packageBytes = Encoding.UTF8.GetBytes("portable-package");
            var installerBytes = Encoding.UTF8.GetBytes("signed-installer-placeholder");
            var json = ReleaseJsonWithInstaller("v1.2.3", packageBytes, installerBytes);
            var installerName = "LazyForza-1.2.3-win-x64-setup.exe";
            var installerUri = new Uri(
                $"https://github.com/Laz22y/LazyForza/releases/download/v1.2.3/{installerName}");
            using var http = new HttpClient(new FakeHttpHandler(request =>
            {
                if (request.RequestUri == GitHubReleaseClient.LatestReleaseApi)
                    return JsonResponse(json);
                if (request.RequestUri == installerUri)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(installerBytes)
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var client = new GitHubReleaseClient(http);
            var release = await client.CheckForUpdateAsync(
                new Version(1, 2, 2),
                CancellationToken.None);

            Assert.IsNotNull(release);
            Assert.IsNotNull(release.Installer);
            Assert.AreEqual(installerName, release.Installer.Name);
            var prepared = await client.DownloadAndPrepareAsync(
                release,
                root,
                new Progress<UpdateProgress>(),
                UpdatePackageKind.Installer,
                CancellationToken.None);

            Assert.AreEqual(UpdatePackageKind.Installer, prepared.Kind);
            Assert.AreEqual(string.Empty, prepared.PackageRoot);
            CollectionAssert.AreEqual(
                installerBytes,
                await File.ReadAllBytesAsync(prepared.ArchivePath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void InstallerCacheCleanupRemovesCompletedUpdateAndKeepsDiagnosticLog()
    {
        var root = CreateTempDirectory("lazyforza-installer-cleanup");
        var logs = Path.Combine(root, "Logs");
        var updates = Path.Combine(root, "Updates");
        var work = Path.Combine(updates, "1.5.0-test");
        try
        {
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(logs);
            File.WriteAllText(
                Path.Combine(work, "LazyForza-1.5.0-win-x64-setup.exe"),
                "setup");
            var logPath = Path.Combine(logs, "update-install-1.5.0.log");
            File.WriteAllText(logPath, "diagnostic log");
            File.WriteAllText(
                Path.Combine(work, "installer-update.pending.json"),
                JsonSerializer.Serialize(new
                {
                    targetVersion = "1.5.0",
                    logPath,
                    startedAt = "2026-08-24T00:00:00Z"
                }));

            WindowsInstallerUpdateLauncher.CleanupUpdateCache(
                updates,
                new Version(1, 5, 0));

            Assert.IsFalse(Directory.Exists(work));
            Assert.IsTrue(File.Exists(logPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void InstallerCacheCleanupPreservesRecentFailedUpdateAndExpiresOldSetup()
    {
        var root = CreateTempDirectory("lazyforza-installer-stale-cleanup");
        var updates = Path.Combine(root, "Updates");
        var recent = Path.Combine(updates, "recent");
        var stale = Path.Combine(updates, "stale");
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        try
        {
            Directory.CreateDirectory(recent);
            Directory.CreateDirectory(stale);
            File.WriteAllText(
                Path.Combine(recent, "LazyForza-1.5.0-win-x64-setup.exe"),
                "setup");
            File.WriteAllText(
                Path.Combine(stale, "LazyForza-1.5.0-win-x64-setup.exe"),
                "setup");
            Directory.SetLastWriteTimeUtc(recent, now.UtcDateTime.AddDays(-1));
            Directory.SetLastWriteTimeUtc(stale, now.UtcDateTime.AddDays(-31));

            WindowsInstallerUpdateLauncher.CleanupUpdateCache(
                updates,
                new Version(1, 4, 10),
                now: now);

            Assert.IsTrue(Directory.Exists(recent));
            Assert.IsFalse(Directory.Exists(stale));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task GitCodeReleaseParsesExplicitFeatureUpdateType()
    {
        var json = GitCodeReleaseJson(
            "v1.3.2",
            body: "<!-- lazyforza-update-type: feature -->\n\n- 增加设置");
        using var http = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitCodeReleaseClient.LatestReleaseApi
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new GitCodeReleaseClient(http);

        var update = await client.CheckForUpdateAsync(
            new Version(1, 3, 1),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(UpdateReleaseType.Feature, update.Type);
        Assert.AreEqual("- 增加设置", update.Notes);
    }

    [TestMethod]
    public void ReleaseMetadataFallsBackByVersionAndFormatsMarkdownForDisplay()
    {
        var major = UpdateReleaseMetadata.Parse(
            "重大升级",
            new Version(1, 9, 0),
            new Version(2, 0, 0));
        var feature = UpdateReleaseMetadata.Parse(
            "新增功能",
            new Version(1, 2, 4),
            new Version(1, 3, 0));
        var fix = UpdateReleaseMetadata.Parse(
            "修复问题",
            new Version(1, 3, 0),
            new Version(1, 3, 1));
        var display = UpdateReleaseMetadata.ToDisplayText(
            "## 更新内容\n\n- **新增** 回放功能\n> 可以稍后安装");

        Assert.AreEqual(UpdateReleaseType.MajorFeature, major.Type);
        Assert.AreEqual(UpdateReleaseType.Feature, feature.Type);
        Assert.AreEqual(UpdateReleaseType.Fix, fix.Type);
        Assert.AreEqual(
            $"更新内容{Environment.NewLine}{Environment.NewLine}" +
            $"• 新增 回放功能{Environment.NewLine}可以稍后安装",
            display);
        Assert.AreEqual(
            "本次发行暂未提供更新说明。",
            UpdateReleaseMetadata.ToDisplayText(" "));
    }

    [TestMethod]
    public void ReleaseMetadataSelectsCurrentLanguageFromBilingualNotes()
    {
        const string notes = """
            ## 简体中文

            ### 更新内容

            - 增加中英文更新日志

            ## English

            ### What's new

            - Added bilingual release notes
            """;

        var chinese = UpdateReleaseMetadata.ToDisplayText(notes, "zh-Hans");
        var english = UpdateReleaseMetadata.ToDisplayText(notes, "en");
        var legacy = UpdateReleaseMetadata.ToDisplayText(notes);

        StringAssert.Contains(chinese, "增加中英文更新日志");
        Assert.IsFalse(chinese.Contains("Added bilingual", StringComparison.Ordinal));
        StringAssert.Contains(english, "Added bilingual release notes");
        Assert.IsFalse(english.Contains("增加中英文", StringComparison.Ordinal));
        StringAssert.Contains(legacy, "增加中英文更新日志");
        StringAssert.Contains(legacy, "Added bilingual release notes");
    }

    [TestMethod]
    public async Task GitCodeLatestReleaseAcceptsUploadedAssetsWithoutSizeAndRequiresChecksum()
    {
        var json = GitCodeReleaseJson("v1.2.3");
        using var http = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitCodeReleaseClient.LatestReleaseApi
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new GitCodeReleaseClient(http);

        var update = await client.CheckForUpdateAsync(
            new Version(1, 2, 2),
            CancellationToken.None);
        var current = await client.CheckForUpdateAsync(
            new Version(1, 2, 3),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(UpdateSourceKind.GitCode, update.Source);
        Assert.AreEqual("GitCode", update.SourceName);
        Assert.AreEqual("LazyForza-1.2.3-win-x64.zip", update.Package.Name);
        Assert.IsNull(update.Package.Size);
        Assert.IsNotNull(update.Checksum);
        Assert.AreEqual("api.gitcode.com", update.Package.DownloadUri.Host);
        Assert.AreEqual(
            "/api/v5/repos/Laz22y/LazyForza/releases/v1.2.3/attach_files/LazyForza-1.2.3-win-x64.zip/download",
            update.Package.DownloadUri.AbsolutePath);
        Assert.IsNull(current);
    }

    [TestMethod]
    public async Task GitCodeDownloadUsesDocumentedAttachmentEndpoint()
    {
        var root = CreateTempDirectory("lazyforza-gitcode-download");
        try
        {
            var archive = BuildPackageArchive(new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("gitcode-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("LazyForza 1.2.3")
            });
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            var packageName = "LazyForza-1.2.3-win-x64.zip";
            var packageUri = new Uri(
                $"https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/v1.2.3/attach_files/{packageName}/download");
            var checksumUri = new Uri(
                $"https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/v1.2.3/attach_files/{packageName}.sha256/download");

            using var http = new HttpClient(new FakeHttpHandler(request =>
            {
                if (request.RequestUri == GitCodeReleaseClient.LatestReleaseApi)
                    return JsonResponse(GitCodeReleaseJson("v1.2.3"));
                if (request.RequestUri == packageUri)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archive)
                    };
                if (request.RequestUri == checksumUri)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"{hash}  {packageName}",
                            Encoding.ASCII)
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var client = new GitCodeReleaseClient(http);
            var release = await client.CheckForUpdateAsync(
                new Version(1, 2, 2),
                CancellationToken.None);

            Assert.IsNotNull(release);
            var prepared = await client.DownloadAndPrepareAsync(
                release,
                root,
                new Progress<UpdateProgress>(),
                CancellationToken.None);

            Assert.AreEqual(
                "gitcode-app",
                await File.ReadAllTextAsync(
                    Path.Combine(prepared.PackageRoot, "LazyForza.App.exe")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task MultiSourceCheckPrefersGitCodeWithoutContactingGitHub()
    {
        var githubRequests = 0;
        using var gitCodeHttp = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitCodeReleaseClient.LatestReleaseApi
                ? JsonResponse(GitCodeReleaseJson("v1.2.3"))
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var githubHttp = new HttpClient(new FakeHttpHandler(_ =>
        {
            githubRequests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        using var client = new MultiSourceUpdateClient(
            new GitCodeReleaseClient(gitCodeHttp),
            new GitHubReleaseClient(githubHttp));

        var update = await client.CheckForUpdateAsync(
            new Version(1, 2, 2),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(UpdateSourceKind.GitCode, update.Source);
        Assert.AreEqual(0, githubRequests);
    }

    [TestMethod]
    public async Task MultiSourceCheckFallsBackToGitHubWhenGitCodeFails()
    {
        var packageBytes = Encoding.UTF8.GetBytes("package");
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        using var gitCodeHttp = new HttpClient(new FakeHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var githubHttp = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri == GitHubReleaseClient.LatestReleaseApi
                ? JsonResponse(ReleaseJson("v1.2.3", packageBytes.Length, $"sha256:{hash}"))
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new MultiSourceUpdateClient(
            new GitCodeReleaseClient(gitCodeHttp),
            new GitHubReleaseClient(githubHttp));

        var update = await client.CheckForUpdateAsync(
            new Version(1, 2, 2),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(UpdateSourceKind.GitHub, update.Source);
    }

    [TestMethod]
    public async Task MultiSourceCheckCanPreferGitHubAndFallBackToGitCode()
    {
        var gitCodeRequests = 0;
        using var githubHttp = new HttpClient(new FakeHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var gitCodeHttp = new HttpClient(new FakeHttpHandler(request =>
        {
            gitCodeRequests++;
            return request.RequestUri == GitCodeReleaseClient.LatestReleaseApi
                ? JsonResponse(GitCodeReleaseJson("v1.2.3"))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var client = new MultiSourceUpdateClient(
            new GitCodeReleaseClient(gitCodeHttp),
            new GitHubReleaseClient(githubHttp),
            UpdateSourceKind.GitHub);

        var update = await client.CheckForUpdateAsync(
            new Version(1, 2, 2),
            CancellationToken.None);

        Assert.IsNotNull(update);
        Assert.AreEqual(UpdateSourceKind.GitCode, update.Source);
        Assert.AreEqual(1, gitCodeRequests);
    }

    [TestMethod]
    public async Task MultiSourceDownloadFallsBackToSameGitHubReleaseWhenGitCodeFails()
    {
        var root = CreateTempDirectory("lazyforza-update-source-fallback");
        try
        {
            var archive = BuildPackageArchive(new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("new-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("LazyForza 1.2.3")
            });
            var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var packageName = "LazyForza-1.2.3-win-x64.zip";
            var gitCodePackageUri = new Uri(
                $"https://file-cdn.gitcode.com/123/releases/v1.2.3/{packageName}?auth_key=test");
            var gitCodeChecksumUri = new Uri(
                $"https://file-cdn.gitcode.com/123/releases/v1.2.3/{packageName}.sha256?auth_key=test");
            var githubPackageUri = new Uri(
                $"https://github.com/Laz22y/LazyForza/releases/download/v1.2.3/{packageName}");

            using var gitCodeHttp = new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.BadGateway)));
            using var githubHttp = new HttpClient(new FakeHttpHandler(request =>
            {
                if (request.RequestUri == GitHubReleaseClient.LatestReleaseApi)
                    return JsonResponse(ReleaseJson(
                        "v1.2.3",
                        archive.Length,
                        $"sha256:{hash}"));
                if (request.RequestUri == githubPackageUri)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archive)
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var client = new MultiSourceUpdateClient(
                new GitCodeReleaseClient(gitCodeHttp),
                new GitHubReleaseClient(githubHttp));
            var gitCodeRelease = new UpdateReleaseInfo(
                new Version(1, 2, 3),
                "v1.2.3",
                "LazyForza 1.2.3",
                string.Empty,
                GitCodeReleaseClient.RepositoryPage,
                new UpdateReleaseAsset(packageName, gitCodePackageUri, null, null),
                new UpdateReleaseAsset(
                    $"{packageName}.sha256",
                    gitCodeChecksumUri,
                    null,
                    null),
                UpdateSourceKind.GitCode);

            var prepared = await client.DownloadAndPrepareAsync(
                gitCodeRelease,
                root,
                new Progress<UpdateProgress>(),
                CancellationToken.None);

            Assert.AreEqual(new Version(1, 2, 3), prepared.Version);
            Assert.AreEqual(
                "new-app",
                await File.ReadAllTextAsync(
                    Path.Combine(prepared.PackageRoot, "LazyForza.App.exe")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task MultiSourceCheckReportsFailureWhenBothSourcesFail()
    {
        using var gitCodeHttp = new HttpClient(new FakeHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var githubHttp = new HttpClient(new FakeHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var client = new MultiSourceUpdateClient(
            new GitCodeReleaseClient(gitCodeHttp),
            new GitHubReleaseClient(githubHttp));

        var exception = await Assert.ThrowsExactlyAsync<UpdateException>(() =>
            client.CheckForUpdateAsync(new Version(1, 2, 2), CancellationToken.None));

        StringAssert.Contains(exception.Message, "GitCode");
        StringAssert.Contains(exception.Message, "GitHub");
    }

    [TestMethod]
    public async Task GitCodeRejectsReleaseAssetOnUntrustedHost()
    {
        var json = GitCodeReleaseJson(
            "v1.2.3",
            packageUrl: "https://example.invalid/LazyForza-1.2.3-win-x64.zip");
        using var http = new HttpClient(new FakeHttpHandler(_ => JsonResponse(json)));
        using var client = new GitCodeReleaseClient(http);

        await Assert.ThrowsExactlyAsync<UpdateException>(() =>
            client.CheckForUpdateAsync(new Version(1, 2, 2), CancellationToken.None));
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
            var release = new UpdateReleaseInfo(
                new Version(1, 2, 3),
                "v1.2.3",
                "LazyForza 1.2.3",
                string.Empty,
                new Uri("https://github.com/Laz22y/LazyForza/releases/tag/v1.2.3"),
                new UpdateReleaseAsset(
                    "LazyForza-1.2.3-win-x64.zip",
                    packageUri,
                    archive.Length,
                    $"sha256:{hash}"),
                null,
                UpdateSourceKind.GitHub);

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
    public async Task PreviewDownloadUsesSemanticArtifactNameAndPreservesVersionLabel()
    {
        var root = CreateTempDirectory("lazyforza-preview-download");
        try
        {
            var archive = BuildPackageArchive(new Dictionary<string, byte[]>
            {
                ["LazyForza.App.exe"] = Encoding.UTF8.GetBytes("preview-app"),
                ["BUILDINFO.txt"] = Encoding.UTF8.GetBytes("LazyForza 1.5.1-alpha-2"),
                ["LazyForza.Preview"] = Encoding.UTF8.GetBytes("preview-package")
            });
            var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            const string version = "1.5.1-alpha-2";
            var packageName = $"LazyForza-{version}-win-x64.zip";
            var packageUri = new Uri(
                $"https://github.com/Laz22y/LazyForza/releases/download/v{version}/{packageName}");
            using var http = new HttpClient(new FakeHttpHandler(request =>
                request.RequestUri == packageUri
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archive)
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)));
            using var client = new GitHubPreviewReleaseClient(http);
            var release = new UpdateReleaseInfo(
                new Version(1, 5, 1),
                $"v{version}",
                $"LazyForza {version}",
                string.Empty,
                new Uri($"https://github.com/Laz22y/LazyForza/releases/tag/v{version}"),
                new UpdateReleaseAsset(
                    packageName,
                    packageUri,
                    archive.Length,
                    $"sha256:{hash}"),
                null,
                UpdateSourceKind.GitHub,
                VersionLabel: version);

            var prepared = await client.DownloadAndPrepareAsync(
                release,
                root,
                new Progress<UpdateProgress>(),
                CancellationToken.None);

            Assert.AreEqual(version, prepared.ArtifactVersion);
            Assert.IsTrue(File.Exists(Path.Combine(prepared.PackageRoot, "LazyForza.Preview")));
            await UpdatePackageVerifier.VerifyManifestAsync(
                prepared.PackageRoot,
                CancellationToken.None);
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
            var release = new UpdateReleaseInfo(
                new Version(1, 2, 3),
                "v1.2.3",
                "LazyForza 1.2.3",
                string.Empty,
                new Uri("https://github.com/Laz22y/LazyForza/releases/tag/v1.2.3"),
                new UpdateReleaseAsset(packageName, packageUri, archive.Length, null),
                new UpdateReleaseAsset(
                    $"{packageName}.sha256",
                    checksumUri,
                    checksumBytes.Length,
                    null),
                UpdateSourceKind.GitHub);

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
                ["stale-runtime.dll"] = Encoding.UTF8.GetBytes("stale"),
                ["LazyForza.Preview"] = Encoding.UTF8.GetBytes("preview-package")
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
            Assert.IsFalse(File.Exists(Path.Combine(target, "LazyForza.Preview")));
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

    private static string ReleaseJson(
        string tag,
        int packageSize,
        string digest,
        string body = "Stable")
    {
        var version = tag.TrimStart('v');
        var name = $"LazyForza-{version}-win-x64.zip";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = $"LazyForza {version}",
            body,
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

    private static string ReleaseJsonWithInstaller(
        string tag,
        byte[] package,
        byte[] installer)
    {
        var version = tag.TrimStart('v');
        var packageName = $"LazyForza-{version}-win-x64.zip";
        var installerName = $"LazyForza-{version}-win-x64-setup.exe";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = $"LazyForza {version}",
            body = "Stable",
            html_url = $"https://github.com/Laz22y/LazyForza/releases/tag/{tag}",
            draft = false,
            prerelease = false,
            assets = new object[]
            {
                new
                {
                    name = packageName,
                    state = "uploaded",
                    size = package.Length,
                    digest = $"sha256:{Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant()}",
                    browser_download_url = $"https://github.com/Laz22y/LazyForza/releases/download/{tag}/{packageName}"
                },
                new
                {
                    name = installerName,
                    state = "uploaded",
                    size = installer.Length,
                    digest = $"sha256:{Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant()}",
                    browser_download_url = $"https://github.com/Laz22y/LazyForza/releases/download/{tag}/{installerName}"
                }
            }
        });
    }

    private static string GitCodeReleaseJson(
        string tag,
        string? packageUrl = null,
        string? checksumUrl = null,
        string body = "Stable")
    {
        var version = tag.TrimStart('v');
        var packageName = $"LazyForza-{version}-win-x64.zip";
        packageUrl ??=
            $"https://api.gitcode.com/Laz22y/LazyForza/releases/download/{tag}/{packageName}";
        checksumUrl ??=
            $"https://api.gitcode.com/Laz22y/LazyForza/releases/download/{tag}/{packageName}.sha256";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            prerelease = false,
            name = $"LazyForza {version}",
            body,
            assets = new[]
            {
                new
                {
                    name = packageName,
                    browser_download_url = packageUrl
                },
                new
                {
                    name = $"{packageName}.sha256",
                    browser_download_url = checksumUrl
                }
            }
        });
    }

    private static string PreviewReleaseListJson(
        params (string Tag, bool Prerelease, bool Draft)[] releases) =>
        JsonSerializer.Serialize(releases.Select(release =>
        {
            var version = release.Tag.TrimStart('v');
            var packageName = $"LazyForza-{version}-win-x64.zip";
            return new
            {
                tag_name = release.Tag,
                name = $"LazyForza {version}",
                body = "Preview",
                html_url = $"https://github.com/Laz22y/LazyForza/releases/tag/{release.Tag}",
                draft = release.Draft,
                prerelease = release.Prerelease,
                assets = new[]
                {
                    new
                    {
                        name = packageName,
                        state = "uploaded",
                        size = 1024,
                        digest = $"sha256:{new string('a', 64)}",
                        browser_download_url =
                            $"https://github.com/Laz22y/LazyForza/releases/download/{release.Tag}/{packageName}"
                    }
                }
            };
        }));

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
