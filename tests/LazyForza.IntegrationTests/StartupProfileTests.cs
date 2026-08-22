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
        try
        {
            AppLocalization.UseLanguage("en");
            Assert.AreEqual("Settings", AppLocalization.Text("nav.settings", "设置"));
            Assert.AreEqual("Learning new track", AppLocalization.Literal("正在学习新赛道"));
            Assert.AreEqual(
                "Confirming start point · 42 trace points",
                AppLocalization.Literal("正在确认起点 · 42 个轨迹点"));
            Assert.AreEqual(
                "Candidate: Demo loop · verified 119 m",
                AppLocalization.Literal("候选：Demo loop · 已验证 119 m"));
            Assert.AreEqual("未翻译", AppLocalization.Text("missing", "未翻译"));
        }
        finally
        {
            AppLocalization.UseLanguage("zh-Hans");
        }
    }

    [TestMethod]
    public void LanguageCanSwitchBothDirectionsWithoutLosingTranslations()
    {
        try
        {
            AppLocalization.UseLanguage("en");
            Assert.AreEqual("en", AppLocalization.CurrentLanguage);
            Assert.AreEqual("en-US", System.Globalization.CultureInfo.CurrentCulture.Name);
            Assert.AreEqual("Hokubu Circuit", AppLocalization.Literal("北部环道赛"));

            AppLocalization.UseLanguage("zh-Hans");
            Assert.AreEqual("zh-Hans", AppLocalization.CurrentLanguage);
            Assert.AreEqual("zh-CN", System.Globalization.CultureInfo.CurrentCulture.Name);
            Assert.AreEqual("北部环道赛", AppLocalization.Literal("北部环道赛"));

            AppLocalization.UseLanguage("en");
            Assert.AreEqual("Settings", AppLocalization.Text("nav.settings", "设置"));
            Assert.AreEqual("Shimanoyama Charge", AppLocalization.Literal("霜山冲锋赛"));
        }
        finally
        {
            AppLocalization.UseLanguage("zh-Hans");
        }
    }

    [TestMethod]
    public void CompletedProfileCanSwitchLanguageBothDirections()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new StartupProfileStore(Path.Combine(root, "startup-profile.json"));
            var original = StartupProfile.CreateDefault() with
            {
                InitializationCompleted = true,
                DataDirectory = Path.Combine(root, "data"),
                CloseBehavior = MainWindowCloseBehavior.ExitApplication,
                InitializationCompletedAt = DateTimeOffset.Parse("2026-08-22T12:00:00+08:00")
            };

            store.Save(original with { Language = "en" });
            var english = store.Load();
            store.Save(english with { Language = "zh-Hans" });
            var chinese = store.Load();

            Assert.AreEqual("zh-Hans", chinese.Language);
            Assert.IsTrue(chinese.InitializationCompleted);
            Assert.AreEqual(Path.GetFullPath(original.DataDirectory), chinese.DataDirectory);
            Assert.AreEqual(original.CloseBehavior, chinese.CloseBehavior);
            Assert.AreEqual(original.InitializationCompletedAt, chinese.InitializationCompletedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow("--open", "C:\\data\\session.lfztelemetry")]
    [DataRow("--open=C:\\data\\shared.lfzlap", null)]
    [DataRow("C:\\data\\track.lfzestate", null)]
    public void AssociatedFileArgumentAcceptsSupportedFileTypes(
        string first,
        string? second)
    {
        var arguments = second is null ? new[] { first } : new[] { first, second };

        var path = LazyForza.App.App.AssociatedFilePath(arguments);

        Assert.IsNotNull(path);
        Assert.IsTrue(
            path.EndsWith(".lfztelemetry", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".lfzlap", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".lfzestate", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AssociatedFileArgumentRejectsUnsupportedFileTypes()
    {
        Assert.IsNull(LazyForza.App.App.AssociatedFilePath(
            ["--open", "C:\\data\\backup.lfzbackup"]));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LazyForza-startup-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
