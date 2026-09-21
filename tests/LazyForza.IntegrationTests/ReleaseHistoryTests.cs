using System.Net;
using System.Text.Json;
using LazyForza.Update;
using LazyForza.App;
using LazyForza.Storage;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class ReleaseHistoryTests
{
    [TestMethod]
    public void HistoryIncludesOlderVersionsWithoutAssetsButExcludesDraftsAndPreviewsForStable()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { tag_name = "v2.0.0", draft = true },
            new { tag_name = "v1.6.0-alpha-1", prerelease = false },
            new { tag_name = "v1.5.9", prerelease = true },
            new { tag_name = "not-a-version" },
            new { tag_name = "v1.5.1", name = "First", body = "old release" },
            new { tag_name = "v1.5.3", name = "Radio Check", body = "<!-- lazyforza-update-type: feature -->\n## 简体中文\n- 新功能\n## English\n- New feature", html_url = "file:///untrusted" },
            new { tag_name = "1.5.3", name = "Duplicate" },
            new { tag_name = "v1.5.2" }, new { tag_name = "v1.5.0" }, new { tag_name = "v1.4.0" }, new { tag_name = "v1.3.0" }
        });
        var stable = ReleaseHistoryClient.Parse(json, UpdateSourceKind.GitHub, false);
        CollectionAssert.AreEqual(new[] { "v1.5.3", "v1.5.2", "v1.5.1", "v1.5.0", "v1.4.0" }, stable.Select(item => item.Tag).ToArray());
        Assert.AreEqual("• New feature", UpdateReleaseMetadata.ToDisplayText(stable[0].Notes, "en"));
        Assert.AreEqual("• 新功能", UpdateReleaseMetadata.ToDisplayText(stable[0].Notes, "zh-Hans"));
        Assert.AreEqual("https://github.com/Laz22y/LazyForza/releases/tag/v1.5.3", stable[0].PageUri.AbsoluteUri);
        var preview = ReleaseHistoryClient.Parse(json, UpdateSourceKind.GitCode, true);
        Assert.AreEqual("v1.6.0-alpha-1", preview[0].Tag);
        Assert.IsTrue(preview[0].IsPreview);
        Assert.AreEqual("gitcode.com", preview[0].PageUri.Host);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("empty")]
    [DataRow("malformed")]
    [DataRow("oversized")]
    public async Task UnavailablePreferredSourceFallsBackWithoutRequiringANewerVersion(string failure)
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.Host);
            if (requests.Count > 1) return Task.FromResult(Json("[{\"tag_name\":\"v1.0.0\"}]"));
            return Task.FromResult(failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "empty" => Json("[]"),
                "oversized" => Json(new string('x', 2 * 1024 * 1024 + 1)),
                _ => Json("{")
            });
        }));
        using var client = new ReleaseHistoryClient(http);
        var snapshot = await client.GetRecentAsync(UpdateSourceKind.GitCode, false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "api.gitcode.com", "api.github.com" }, requests);
        Assert.AreEqual(UpdateSourceKind.GitHub, snapshot.Releases.Single().Source);
    }

    [TestMethod]
    public async Task LeavingThePageCancelsFetchWithoutStartingFallback()
    {
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, token) =>
        {
            requests++;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        }));
        using var client = new ReleaseHistoryClient(http);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => client.GetRecentAsync(UpdateSourceKind.GitHub, false, cancellation.Token));
        Assert.AreEqual(1, requests);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [TestMethod]
    public async Task CachedAnnouncementsSurviveReopenFailedRefreshAndChannelChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-announcements-{Guid.NewGuid():N}");
        try
        {
            var directories = new DataDirectoryService(root);
            directories.EnsureCreated();
            var distribution = new ApplicationDistribution(ApplicationDistributionKind.Portable, "", "");
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("[{\"tag_name\":\"v1.5.3\",\"body\":\"已保存的公告\"}]"))));
            using (var store = new LazyForzaStore(directories.DatabasePath))
            using (var manager = new ApplicationUpdateManager(store, directories, distribution, _ => { }, new ReleaseHistoryClient(http)))
            {
                Assert.IsNull(manager.ReadAnnouncementCache());
                var fetched = await manager.LoadAnnouncementsAsync(CancellationToken.None);
                Assert.AreEqual(fetched, manager.ReadAnnouncementCache()! with { Releases = fetched.Releases });
            }
            using var reopenedStore = new LazyForzaStore(directories.DatabasePath);
            using var failedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
            using var reopened = new ApplicationUpdateManager(reopenedStore, directories, distribution, _ => { }, new ReleaseHistoryClient(failedHttp));
            Assert.AreEqual("已保存的公告", reopened.ReadAnnouncementCache()!.Releases.Single().Notes);
            await Assert.ThrowsExactlyAsync<UpdateException>(() => reopened.LoadAnnouncementsAsync(CancellationToken.None));
            Assert.AreEqual("已保存的公告", reopened.ReadAnnouncementCache()!.Releases.Single().Notes);
            using var preview = new ApplicationUpdateManager(reopenedStore, directories, distribution with { Kind = ApplicationDistributionKind.Preview }, _ => { });
            Assert.IsNull(preview.ReadAnnouncementCache());
            reopenedStore.SetAppSetting("updates.announcements.stable", "{broken");
            Assert.IsNull(reopened.ReadAnnouncementCache());
            reopenedStore.SetAppSetting("updates.announcements.stable", JsonSerializer.Serialize(new ReleaseHistorySnapshot(DateTimeOffset.UtcNow,
                [new("v1.6.0-alpha-1", "Preview", "", null, false, UpdateSourceKind.GitHub)])));
            Assert.IsNull(reopened.ReadAnnouncementCache(), "A preview tag must not leak into the stable channel through cache.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
