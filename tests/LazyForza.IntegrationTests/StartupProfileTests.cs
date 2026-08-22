using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class StartupProfileTests
{
    [TestMethod]
    public void MissingProfileRequiresInitializationAndUsesLegacyDataDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new StartupProfileStore(Path.Combine(root, "startup-profile.json"));

            var profile = store.Load();

            Assert.IsFalse(profile.InitializationCompleted);
            Assert.AreEqual("zh-Hans", profile.Language);
            Assert.AreEqual(
                Path.GetFullPath(StartupProfileStore.DefaultDataDirectory),
                profile.DataDirectory);
            Assert.AreEqual(MainWindowCloseBehavior.MinimizeToTray, profile.CloseBehavior);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CompletedProfileSurvivesReloadAndExplicitDataDirectoryWins()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profilePath = Path.Combine(root, "startup-profile.json");
            var dataPath = Path.Combine(root, "selected-data");
            var explicitPath = Path.Combine(root, "command-line-data");
            var store = new StartupProfileStore(profilePath);
            store.Save(new StartupProfile(
                StartupProfile.CurrentSchemaVersion,
                InitializationCompleted: true,
                "en",
                dataPath,
                MainWindowCloseBehavior.ExitApplication,
                DateTimeOffset.Parse("2026-08-22T12:00:00+08:00")));

            var reloaded = store.Load();

            Assert.IsTrue(reloaded.InitializationCompleted);
            Assert.AreEqual("en", reloaded.Language);
            Assert.AreEqual(Path.GetFullPath(dataPath), reloaded.DataDirectory);
            Assert.AreEqual(MainWindowCloseBehavior.ExitApplication, reloaded.CloseBehavior);
            Assert.AreEqual(
                Path.GetFullPath(explicitPath),
                store.ResolveDataDirectory(explicitPath, reloaded));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LocalizationFallsBackToChineseForUnknownKeys()
    {
        AppLocalization.UseLanguage("en");
        Assert.AreEqual("Settings", AppLocalization.Text("nav.settings", "设置"));
        Assert.AreEqual("未翻译", AppLocalization.Text("missing", "未翻译"));
        AppLocalization.UseLanguage("zh-Hans");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LazyForza-startup-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
