using System.Diagnostics;
using LazyForza.Storage;
using LazyForza.Update;

namespace LazyForza.App;

internal sealed class ApplicationUpdateManager : IDisposable
{
    internal const string CheckOnStartupSetting = "updates.checkOnStartup";
    internal const string PreferredSourceSetting = "updates.preferredSource";

    private readonly LazyForzaStore store;
    private readonly DataDirectoryService directories;
    private readonly MultiSourceUpdateClient client;
    private readonly Action<string> log;

    public ApplicationUpdateManager(
        LazyForzaStore store,
        DataDirectoryService directories,
        Action<string> log)
    {
        this.store = store;
        this.directories = directories;
        this.log = log;
        client = new MultiSourceUpdateClient(PreferredSource, log);
        WindowsUpdateLauncher.CleanupCompletedUpdates(directories.UpdatesPath);
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

    public bool CheckOnStartup
    {
        get
        {
            var saved = store.GetAppSetting(CheckOnStartupSetting);
            return saved is null || !bool.TryParse(saved, out var enabled) || enabled;
        }
        set => store.SetAppSetting(CheckOnStartupSetting, value.ToString());
    }

    public UpdateSourceKind PreferredSource
    {
        get
        {
            var saved = store.GetAppSetting(PreferredSourceSetting);
            return Enum.TryParse<UpdateSourceKind>(saved, true, out var source) &&
                   source == UpdateSourceKind.GitHub
                ? UpdateSourceKind.GitHub
                : UpdateSourceKind.GitCode;
        }
        set
        {
            var normalized = value == UpdateSourceKind.GitHub
                ? UpdateSourceKind.GitHub
                : UpdateSourceKind.GitCode;
            store.SetAppSetting(PreferredSourceSetting, normalized.ToString());
            client.PreferredSource = normalized;
        }
    }

    public string PreferredSourceName => PreferredSource == UpdateSourceKind.GitHub ? "GitHub" : "GitCode";

    public string FallbackSourceName => PreferredSource == UpdateSourceKind.GitHub ? "GitCode" : "GitHub";

    public bool CanInstallAutomatically =>
        WindowsUpdateLauncher.IsPackagedInstall(AppContext.BaseDirectory);

    public Task<UpdateReleaseInfo?> CheckAsync(CancellationToken cancellationToken) =>
        client.CheckForUpdateAsync(CurrentVersion, cancellationToken);

    public Task<PreparedUpdate> DownloadAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken) =>
        client.DownloadAndPrepareAsync(release, directories.UpdatesPath, progress, cancellationToken);

    public void InstallAndRestart(PreparedUpdate update)
    {
        if (!CanInstallAutomatically)
            throw new UpdateException("当前运行的是开发构建，已阻止发行包覆盖开发目录。请在完整发行版中安装更新。");

        var backup = new DataBackupService(store, CurrentVersion.ToString(3))
            .CreateAutomaticUpdateBackup(directories.BackupsPath);
        log($"Automatic pre-update data backup created: {backup}");

        var process = WindowsUpdateLauncher.Launch(
            update,
            AppContext.BaseDirectory,
            directories.Root,
            Environment.ProcessId);
        log($"Update installer started as PID {process.Id}. Preparing to exit.");
        App.RequestExit();
    }

    public void ReportFailure(string context, Exception exception) =>
        log($"{context}: {exception.GetType().Name}: {exception.Message}");

    public void Dispose() => client.Dispose();
}
