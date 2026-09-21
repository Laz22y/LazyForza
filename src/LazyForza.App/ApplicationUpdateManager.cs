using System.Diagnostics;
using System.Text.Json;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.App;

internal sealed class ApplicationUpdateManager : IDisposable
{
    internal const string CheckOnStartupSetting = "updates.checkOnStartup";
    internal const string PreferredSourceSetting = "updates.preferredSource";
    internal const string PreviewPreferredSourceSetting = "updates.preview.preferredSource";

    private readonly LazyForzaStore store;
    private readonly DataDirectoryService directories;
    private readonly ApplicationDistribution distribution;
    private readonly MultiSourceUpdateClient stableClient;
    private readonly PreviewMultiSourceUpdateClient previewClient;
    private readonly ReleaseHistoryClient announcementClient;
    private readonly Action<string> log;

    public ApplicationUpdateManager(
        LazyForzaStore store,
        DataDirectoryService directories,
        ApplicationDistribution distribution,
        Action<string> log,
        ReleaseHistoryClient? announcementClient = null)
    {
        this.store = store;
        this.directories = directories;
        this.distribution = distribution;
        this.log = log;
        this.announcementClient = announcementClient ?? new ReleaseHistoryClient();
        stableClient = new MultiSourceUpdateClient(
            ReadPreferredSource(PreferredSourceSetting),
            log);
        previewClient = new PreviewMultiSourceUpdateClient(
            ReadPreferredSource(PreviewPreferredSourceSetting),
            log);
        WindowsUpdateLauncher.CleanupCompletedUpdates(directories.UpdatesPath);
        WindowsInstallerUpdateLauncher.CleanupUpdateCache(
            directories.UpdatesPath,
            CurrentVersion,
            log);
    }

    public Version CurrentVersion
    {
        get
        {
            var version = typeof(ApplicationUpdateManager).Assembly.GetName().Version;
            return version is null
                ? new Version(0, 0, 0)
                : new Version(
                    Math.Max(0, version.Major),
                    Math.Max(0, version.Minor),
                    Math.Max(0, version.Build));
        }
    }

    public UpdateSemanticVersion CurrentUpdateVersion => ApplicationVersionInfo.UpdateVersion;

    public bool IsUpdateMandatory => distribution.IsPreview || CurrentUpdateVersion.IsPrerelease;

    public bool CheckOnStartup
    {
        get
        {
            if (IsUpdateMandatory) return true;
            var saved = store.GetAppSetting(ModeCheckOnStartupSetting) ??
                        store.GetAppSetting(CheckOnStartupSetting);
            return bool.TryParse(saved, out var enabled)
                ? enabled
                : distribution.DefaultUpdateCheckEnabled;
        }
        set
        {
            if (IsUpdateMandatory) return;
            store.SetAppSetting(ModeCheckOnStartupSetting, value.ToString());
        }
    }

    public UpdateSourceKind PreferredSource
    {
        get
        {
            return ReadPreferredSource(
                IsUpdateMandatory
                    ? PreviewPreferredSourceSetting
                    : PreferredSourceSetting);
        }
        set
        {
            var normalized = value == UpdateSourceKind.GitHub
                ? UpdateSourceKind.GitHub
                : UpdateSourceKind.GitCode;
            if (IsUpdateMandatory)
            {
                store.SetAppSetting(PreviewPreferredSourceSetting, normalized.ToString());
                previewClient.PreferredSource = normalized;
            }
            else
            {
                store.SetAppSetting(PreferredSourceSetting, normalized.ToString());
                stableClient.PreferredSource = normalized;
            }
        }
    }

    public string PreferredSourceName => PreferredSource == UpdateSourceKind.GitHub ? "GitHub" : "GitCode";

    public string FallbackSourceName =>
        PreferredSource == UpdateSourceKind.GitHub ? "GitCode" : "GitHub";

    public ApplicationDistributionKind DistributionKind => distribution.Kind;

    public bool CanInstallAutomatically =>
        !distribution.IsDevelopment &&
        WindowsUpdateLauncher.IsPackagedInstall(AppContext.BaseDirectory);

    public Task<UpdateReleaseInfo?> CheckAsync(CancellationToken cancellationToken) =>
        IsUpdateMandatory
            ? previewClient.CheckForUpdateAsync(CurrentUpdateVersion, cancellationToken)
            : stableClient.CheckForUpdateAsync(CurrentVersion, cancellationToken);

    private string AnnouncementCacheKey => $"updates.announcements.{(IsUpdateMandatory ? "preview" : "stable")}";

    internal ReleaseHistorySnapshot? ReadAnnouncementCache()
    {
        try
        {
            var json = store.GetAppSetting(AnnouncementCacheKey);
            if (string.IsNullOrEmpty(json) || json.Length > 200000) return null;
            var cached = JsonSerializer.Deserialize<ReleaseHistorySnapshot>(json);
            if (cached?.Releases is not { Length: > 0 and <= ReleaseHistoryClient.MaximumEntries } releases ||
                releases.Any(release => release is null || string.IsNullOrEmpty(release.Title) || release.Notes is null ||
                    !UpdateSemanticVersion.TryParse(release.Tag, out var version) ||
                    !IsUpdateMandatory && (release.IsPreview || version.IsPrerelease))) return null;
            return cached;
        }
        catch (JsonException) { return null; }
    }

    internal async Task<ReleaseHistorySnapshot> LoadAnnouncementsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await announcementClient.GetRecentAsync(PreferredSource, IsUpdateMandatory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Human-readable JSON can exceed its text length when escaped; Unicode notes stay compact in the cache.
        try
        {
            store.SetAppSetting(AnnouncementCacheKey, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (InvalidOperationException error) { ReportFailure("Save announcement cache", error); }
        return snapshot;
    }

    public Task<PreparedUpdate> DownloadAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken) =>
        IsUpdateMandatory
            ? previewClient.DownloadAndPrepareAsync(
                release,
                directories.UpdatesPath,
                progress,
                cancellationToken)
            : stableClient.DownloadAndPrepareAsync(
                release,
                directories.UpdatesPath,
                progress,
                distribution.IsInstalled
                    ? UpdatePackageKind.Installer
                    : UpdatePackageKind.Portable,
                cancellationToken);

    public void InstallAndRestart(PreparedUpdate update)
    {
        if (!CanInstallAutomatically)
            throw new UpdateException("当前运行的是开发构建，已阻止发行包覆盖开发目录。请在完整发行版中安装更新。");

        var backup = new DataBackupService(store, CurrentVersion.ToString(3))
            .CreateAutomaticUpdateBackup(directories.BackupsPath);
        log($"Automatic pre-update data backup created: {backup}");

        var process = distribution.IsInstalled
            ? WindowsInstallerUpdateLauncher.Launch(
                update,
                AppContext.BaseDirectory,
                directories.LogsPath)
            : WindowsUpdateLauncher.Launch(
                update,
                AppContext.BaseDirectory,
                directories.Root,
                Environment.ProcessId);
        log(distribution.IsInstalled
            ? $"Installed update setup started as PID {process.Id}. Preparing to exit."
            : $"Portable update installer started as PID {process.Id}. Preparing to exit.");
        App.RequestExit();
    }

    public void ReportFailure(string context, Exception exception) =>
        log($"{context}: {exception.GetType().Name}: {exception.Message}");

    public void Dispose()
    {
        announcementClient.Dispose();
        previewClient.Dispose();
        stableClient.Dispose();
    }

    private string ModeCheckOnStartupSetting =>
        $"{CheckOnStartupSetting}.{distribution.Kind.ToString().ToLowerInvariant()}";

    private UpdateSourceKind ReadPreferredSource(string setting)
    {
        var saved = store.GetAppSetting(setting);
        return Enum.TryParse<UpdateSourceKind>(saved, true, out var source) &&
               source == UpdateSourceKind.GitHub
            ? UpdateSourceKind.GitHub
            : UpdateSourceKind.GitCode;
    }
}
