using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class StartupProfileTests
{
    [TestMethod]
    public void MissingProfileUsesDefaultsAndInitializationStateIsSeparate()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new StartupProfileStore(Path.Combine(root, "startup-profile.json"));

            var profile = store.Load();

            Assert.AreEqual("zh-Hans", profile.Language);
            Assert.AreEqual(
                Path.GetFullPath(StartupProfileStore.DefaultDataDirectory),
                profile.DataDirectory);
            Assert.AreEqual(MainWindowCloseBehavior.MinimizeToTray, profile.CloseBehavior);
            var state = new InitializationStateStore(
                Path.Combine(root, "initialization-state.json")).Load();
            Assert.IsFalse(state.Exists);
            Assert.IsFalse(state.Completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProfileAndCompletedInitializationStateSurviveReload()
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
                "en",
                dataPath,
                MainWindowCloseBehavior.ExitApplication));
            var stateStore = new InitializationStateStore(
                Path.Combine(root, "initialization-state.json"));
            var completedAt = DateTimeOffset.Parse("2026-08-22T12:00:00+08:00");
            stateStore.MarkCompleted(completedAt);

            var reloaded = store.Load();
            var state = stateStore.Load();

            Assert.AreEqual("en", reloaded.Language);
            Assert.AreEqual(Path.GetFullPath(dataPath), reloaded.DataDirectory);
            Assert.AreEqual(MainWindowCloseBehavior.ExitApplication, reloaded.CloseBehavior);
            Assert.AreEqual(
                Path.GetFullPath(explicitPath),
                store.ResolveDataDirectory(explicitPath, reloaded));
            Assert.IsTrue(state.Exists);
            Assert.IsTrue(state.Completed);
            Assert.AreEqual(completedAt, state.CompletedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DistributionKeepsPortableStateLocalAndInstalledStatePerUser()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var applicationRoot = Path.Combine(root, "app");
            var localRoot = Path.Combine(root, "local");
            Directory.CreateDirectory(applicationRoot);
            Directory.CreateDirectory(localRoot);

            var portable = ApplicationDistribution.Detect(applicationRoot, localRoot);
            Assert.AreEqual(ApplicationDistributionKind.Portable, portable.Kind);
            Assert.AreEqual(
                Path.Combine(applicationRoot, "LazyForza_Data", "initialization-state.json"),
                portable.InitializationStatePath);
            Assert.IsFalse(portable.DefaultUpdateCheckEnabled);

            File.WriteAllText(
                Path.Combine(applicationRoot, ApplicationDistribution.DevelopmentMarkerFileName),
                "development-preview");
            var development = ApplicationDistribution.Detect(applicationRoot, localRoot);
            Assert.AreEqual(ApplicationDistributionKind.Development, development.Kind);
            Assert.IsFalse(development.DefaultUpdateCheckEnabled);

            File.WriteAllText(
                Path.Combine(applicationRoot, ApplicationDistribution.InstalledMarkerFileName),
                "installed");
            var installed = ApplicationDistribution.Detect(applicationRoot, localRoot);
            Assert.AreEqual(ApplicationDistributionKind.Installed, installed.Kind);
            Assert.AreEqual(
                Path.Combine(localRoot, "LazyForza", "initialization-state.json"),
                installed.InitializationStatePath);
            Assert.IsTrue(installed.DefaultUpdateCheckEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LegacyCompletedProfileCanMigrateToIndependentState()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profilePath = Path.Combine(root, "startup-profile.json");
            File.WriteAllText(
                profilePath,
                """
                {
                  "schemaVersion": 1,
                  "initializationCompleted": true,
                  "language": "en",
                  "dataDirectory": "C:\\LazyForzaData",
                  "closeBehavior": 1,
                  "initializationCompletedAt": "2026-08-22T12:00:00+08:00"
                }
                """);

            var loaded = new StartupProfileStore(profilePath).LoadWithMigration();

            Assert.IsTrue(loaded.LegacyInitializationCompleted);
            Assert.AreEqual(
                DateTimeOffset.Parse("2026-08-22T12:00:00+08:00"),
                loaded.LegacyInitializationCompletedAt);
            Assert.AreEqual(StartupProfile.CurrentSchemaVersion, loaded.Profile.SchemaVersion);
            Assert.AreEqual("en", loaded.Profile.Language);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ResetInitializationStatePreservesProfile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profilePath = Path.Combine(root, "startup-profile.json");
            var stateStore = new InitializationStateStore(
                Path.Combine(root, "initialization-state.json"));
            var profileStore = new StartupProfileStore(profilePath);
            profileStore.Save(StartupProfile.CreateDefault() with
            {
                Language = "en",
                DataDirectory = Path.Combine(root, "data")
            });
            stateStore.MarkCompleted();

            stateStore.Reset();

            Assert.IsFalse(stateStore.Load().Completed);
            Assert.AreEqual("en", profileStore.Load().Language);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(root, "data")),
                profileStore.Load().DataDirectory);
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
    public void EnglishLocalizationCoversRuntimeStatusAndErrorTemplates()
    {
        try
        {
            AppLocalization.UseLanguage("en");
            Assert.AreEqual(
                "Starting sequence in 4 s",
                AppLocalization.Literal("4 秒后启动发车程序"));
            Assert.AreEqual(
                "Estate circuit saved: reference 72.315 s, validation 72.601 s.",
                AppLocalization.Literal("地产环道已保存：参考圈 72.315 s，验证圈 72.601 s。"));
            Assert.AreEqual(
                "At least 2 valid pit stops are required; 1 still required. Staying out leaves too few laps to complete them.",
                AppLocalization.Literal("本场至少要求 2 次有效维修停留，目前还差 1 次；若继续留在赛道，将没有足够圈数完成规定进站。"));
            Assert.AreEqual(
                "GitHub returned HTTP 503; updates cannot be checked right now.",
                AppLocalization.Literal("GitHub 返回了 HTTP 503，暂时无法检查更新。"));
            Assert.AreEqual("Player One", AppLocalization.Literal("Player One"));
        }
        finally
        {
            AppLocalization.UseLanguage("zh-Hans");
        }
    }

    [TestMethod]
    public void RaceServerSystemTextUsesTheClientLanguage()
    {
        try
        {
            AppLocalization.UseLanguage("en");
            Assert.AreEqual("Race start", AppLocalization.Literal("比赛开始"));
            Assert.AreEqual(
                "Driver One entered the room.",
                AppLocalization.Literal("Driver One 进入房间。"));
            Assert.AreEqual(
                "Driver One completed lap 6: 1:08.424.",
                AppLocalization.Literal("Driver One 完成第 6 圈：1:08.424。"));
            Assert.AreEqual(
                "Driver One lap invalid: Marked invalid by client.",
                AppLocalization.Literal("Driver One 的本圈无效：客户端判定无效。"));
            Assert.AreEqual(
                "Driver One may cross the finish line 2 more times before serving the drive-through.",
                AppLocalization.Literal("Driver One 的通过维修区处罚还可跨越终点线 2 次。"));
            Assert.AreEqual(
                "Possible contact between Driver One and Driver Two sent to Race Control for investigation (lap 4).",
                AppLocalization.Literal("Driver One 与 Driver Two 发生疑似车辆接触，已交由总控调查（第 4 圈）。"));
            Assert.AreEqual(
                "+5 seconds pending · False start",
                AppLocalization.Literal("待执行 +5 秒 · 抢跑"));
            Assert.AreEqual(
                "The room has reached its 12-driver limit.",
                AppLocalization.Literal("房间人数已达到 12 人上限。"));
            Assert.AreEqual("Warnings only", AppLocalization.Literal("WarningsOnly"));
            Assert.AreEqual(
                "比赛开始 entered the room.",
                AppLocalization.Literal("比赛开始 进入房间。"));
            Assert.AreEqual("Driver One", AppLocalization.Literal("Driver One"));

            AppLocalization.UseLanguage("zh-Hans");
            Assert.AreEqual(
                "Driver One 进入房间。",
                AppLocalization.Literal("Driver One 进入房间。"));
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
    public void ProfileCanSwitchLanguageBothDirections()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new StartupProfileStore(Path.Combine(root, "startup-profile.json"));
            var original = StartupProfile.CreateDefault() with
            {
                DataDirectory = Path.Combine(root, "data"),
                CloseBehavior = MainWindowCloseBehavior.ExitApplication
            };

            store.Save(original with { Language = "en" });
            var english = store.Load();
            store.Save(english with { Language = "zh-Hans" });
            var chinese = store.Load();

            Assert.AreEqual("zh-Hans", chinese.Language);
            Assert.AreEqual(Path.GetFullPath(original.DataDirectory), chinese.DataDirectory);
            Assert.AreEqual(original.CloseBehavior, chinese.CloseBehavior);
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
