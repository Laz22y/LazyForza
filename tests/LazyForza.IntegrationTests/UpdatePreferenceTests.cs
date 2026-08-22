using LazyForza.App;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class UpdatePreferenceTests
{
    [TestMethod]
    public void PreferredUpdateSourceDefaultsToGitCodeAndPersistsGitHubChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-update-pref-{Guid.NewGuid():N}");
        try
        {
            var directories = new DataDirectoryService(root);
            directories.EnsureCreated();
            using var store = new LazyForzaStore(directories.DatabasePath);
            var distribution = Distribution(root, ApplicationDistributionKind.Portable);
            using (var manager = new ApplicationUpdateManager(
                       store,
                       directories,
                       distribution,
                       _ => { }))
            {
                Assert.AreEqual(UpdateSourceKind.GitCode, manager.PreferredSource);
                manager.PreferredSource = UpdateSourceKind.GitHub;
                Assert.AreEqual("GitHub", manager.PreferredSourceName);
                Assert.AreEqual("GitCode", manager.FallbackSourceName);
            }

            using var reopened = new ApplicationUpdateManager(
                store,
                directories,
                distribution,
                _ => { });
            Assert.AreEqual(UpdateSourceKind.GitHub, reopened.PreferredSource);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void UpdateCheckDefaultsOnForInstalledAndOffForPortable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-update-mode-{Guid.NewGuid():N}");
        try
        {
            var directories = new DataDirectoryService(root);
            directories.EnsureCreated();
            using var store = new LazyForzaStore(directories.DatabasePath);
            using var installed = new ApplicationUpdateManager(
                store,
                directories,
                Distribution(root, ApplicationDistributionKind.Installed),
                _ => { });
            using var portable = new ApplicationUpdateManager(
                store,
                directories,
                Distribution(root, ApplicationDistributionKind.Portable),
                _ => { });

            Assert.IsTrue(installed.CheckOnStartup);
            Assert.IsFalse(portable.CheckOnStartup);

            portable.CheckOnStartup = true;
            installed.CheckOnStartup = false;

            Assert.IsTrue(portable.CheckOnStartup);
            Assert.IsFalse(installed.CheckOnStartup);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ApplicationDistribution Distribution(
        string root,
        ApplicationDistributionKind kind) =>
        new(
            kind,
            Path.Combine(root, $"{kind}-profile.json"),
            Path.Combine(root, $"{kind}-initialization.json"));
}
