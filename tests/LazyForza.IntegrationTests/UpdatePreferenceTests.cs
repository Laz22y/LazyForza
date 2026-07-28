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
            using (var manager = new ApplicationUpdateManager(store, directories, _ => { }))
            {
                Assert.AreEqual(UpdateSourceKind.GitCode, manager.PreferredSource);
                manager.PreferredSource = UpdateSourceKind.GitHub;
                Assert.AreEqual("GitHub", manager.PreferredSourceName);
                Assert.AreEqual("GitCode", manager.FallbackSourceName);
            }

            using var reopened = new ApplicationUpdateManager(store, directories, _ => { });
            Assert.AreEqual(UpdateSourceKind.GitHub, reopened.PreferredSource);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
