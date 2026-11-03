using System.Text.Json;
using LazyForza.Domain;
using LazyForza.Storage;

namespace LazyForza.Storage.Tests;

[TestClass]
public sealed class OverlayLayoutProfileTests
{
    [TestMethod]
    public void ExistingLayoutIsPreservedAndProfilesKeepIndependentSettingsAcrossRestart() => WithStore((store, root) =>
    {
        var original = OverlayLayoutGeometry.Normalize(new OverlayLayout(Left: -1200, Top: 100,
            MonitorId: "second", Opacity: .65, ReduceMotion: true, LapHudAttachedToDashboard: false,
            LapHudLeft: -600, LapHudTop: 50, LapHudScale: .8,
            DashboardWidgets: new(Tires: new(false, 20, 10)),
            EstateRaceWidgets: new(Banner: new(true, .1, .2, .9, .7, "future-theme"))));
        store.SetAppSetting("overlay.layout", JsonSerializer.Serialize(original));
        store.SetAppSetting("telemetry.port", "5300");
        var profiles = new OverlayLayoutProfileStore(store, original, "Original");
        Assert.AreEqual(original, profiles.Active.Layout);
        var first = profiles.Active.Id;
        profiles.Create("Driving");
        var second = profiles.Active.Id;
        profiles.SaveCurrent(original with { Opacity = .9, ShowShiftIndicators = false });
        profiles.Select(first);
        Assert.AreEqual(original, profiles.Active.Layout);
        profiles.Rename("Screen 2");
        profiles.Select(second);
        using var reopened = new LazyForzaStore(Path.Combine(root, "test.db"));
        var restored = new OverlayLayoutProfileStore(reopened,
            JsonSerializer.Deserialize<OverlayLayout>(reopened.GetAppSetting("overlay.layout")!)!, "unused");
        Assert.AreEqual(second, restored.Active.Id);
        Assert.AreEqual(.9, restored.Active.Layout.Opacity);
        Assert.IsFalse(restored.Active.Layout.ShowShiftIndicators);
        Assert.AreEqual("Screen 2", restored.State.Profiles[0].Name);
        Assert.AreEqual("5300", store.GetAppSetting("telemetry.port"));
        Assert.AreEqual("future-theme", restored.Active.Layout.EstateRaceWidgets!.Banner!.ThemeId);
        restored.DeleteActive();
        Assert.AreEqual(first, restored.Active.Id);
        Assert.AreEqual(original, restored.Active.Layout);
        Assert.ThrowsExactly<InvalidOperationException>(restored.DeleteActive);
    });

    [TestMethod]
    public void LayoutAndSelectionRollbackTogetherOnWriteFailure() => WithStore((store, root) =>
    {
        var profiles = new OverlayLayoutProfileStore(store, new(), "First");
        profiles.SaveCurrent(new());
        using var raw = new WinSqliteDatabase(Path.Combine(root, "test.db"));
        raw.Execute("CREATE TRIGGER RejectLayout BEFORE UPDATE ON AppSettings WHEN NEW.Key='overlay.layout' BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = store.GetAppSetting(OverlayLayoutProfileStore.SettingKey);
        Assert.Throws<Exception>(() => profiles.Create("Second"));
        Assert.HasCount(1, profiles.State.Profiles);
        Assert.AreEqual(before, store.GetAppSetting(OverlayLayoutProfileStore.SettingKey));
    });

    [TestMethod]
    public void InvalidNamesAndSelectionsCannotChangeSavedProfiles() => WithStore((store, root) =>
    {
        var profiles = new OverlayLayoutProfileStore(store, new(), "First");
        profiles.Create(" Second ");
        Assert.AreEqual("Second", profiles.Active.Name);
        foreach (var name in new[] { "", "  ", "first", "First ", "bad\nname", new string('a', 41) })
            Assert.ThrowsExactly<ArgumentException>(() => profiles.Rename(name));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => profiles.Select(Guid.NewGuid()));
        Assert.AreEqual("Second", profiles.Active.Name);
        for (var i = 2; i < OverlayLayoutProfileStore.MaximumProfiles; i++) profiles.Create($"Layout {i}");
        Assert.ThrowsExactly<InvalidOperationException>(() => profiles.Create("Overflow"));
    });

    [TestMethod]
    [DataRow("{")]
    [DataRow("{\"Version\":1,\"Profiles\":null}")]
    [DataRow("{\"Version\":1,\"Profiles\":[null]}")]
    public void DamagedProfilesRetainCurrentLayoutAndOriginalJson(string json) => WithStore((store, root) =>
    {
        store.SetAppSetting(OverlayLayoutProfileStore.SettingKey, json);
        var profiles = new OverlayLayoutProfileStore(store, new(Opacity: .7), "Recovered");
        Assert.AreEqual(.7, profiles.Active.Layout.Opacity);
        profiles.SaveCurrent(profiles.Active.Layout);
        Assert.AreEqual(json, store.GetAppSetting("overlay.profiles.recovery"));
    });

    [TestMethod]
    public void SettingsBackupRestoresProfilesAndLegacyLayoutTogether() => WithStore((store, root) =>
    {
        var profiles = new OverlayLayoutProfileStore(store, new(), "First");
        profiles.Create("Second"); profiles.SaveCurrent(new(Opacity: .5));
        var backup = Path.Combine(root, "profiles.lfzbackup");
        new DataBackupService(store, "test").Create(backup, new(Settings: true, Vehicles: false, Laps: false, CustomTracks: false));
        using var target = new LazyForzaStore(Path.Combine(root, "restored.db"));
        new DataBackupService(target, "test").Import(backup, BackupImportMode.Merge);
        var restored = new OverlayLayoutProfileStore(target,
            JsonSerializer.Deserialize<OverlayLayout>(target.GetAppSetting("overlay.layout")!)!, "unused");
        Assert.AreEqual(profiles.Active, restored.Active);
        CollectionAssert.AreEqual(profiles.State.Profiles.ToArray(), restored.State.Profiles.ToArray());
    });

    private static void WithStore(Action<LazyForzaStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "lfz-hud-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { using var store = new LazyForzaStore(Path.Combine(root, "test.db")); action(store, root); }
        finally { Directory.Delete(root, true); }
    }
}
