using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace LazyForza.Update;

public static class WindowsInstallerUpdateLauncher
{
    public const string InstalledMarkerFileName = "LazyForza.Installation";
    internal const string PendingStateFileName = "installer-update.pending.json";
    private static readonly TimeSpan StaleCacheAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Process Launch(
        PreparedUpdate update,
        string installDirectory,
        string logsDirectory)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LazyForza 安装版自动升级仅支持 Windows。");
        if (update.Kind != UpdatePackageKind.Installer ||
            !File.Exists(update.ArchivePath))
            throw new UpdateException("准备好的安装版升级程序已丢失。");

        var installRoot = Path.GetFullPath(installDirectory);
        if (!File.Exists(Path.Combine(installRoot, InstalledMarkerFileName)))
            throw new UpdateException("当前程序不是安装版，不能运行安装版升级程序。");

        var normalizedLogsDirectory = Path.GetFullPath(logsDirectory);
        Directory.CreateDirectory(normalizedLogsDirectory);
        var logPath = Path.Combine(
            normalizedLogsDirectory,
            $"update-install-{update.Version.ToString(3)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        var pendingStatePath = WritePendingState(update, logPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(update.ArchivePath),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = update.WorkDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add("/LOGCLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add($"/LOG={logPath}");
        startInfo.ArgumentList.Add("/AUTOUPDATE");
        try
        {
            return Process.Start(startInfo) ??
                   throw new UpdateException("无法启动安装版升级程序。");
        }
        catch (Win32Exception exception)
        {
            TryDeleteFile(pendingStatePath);
            throw new UpdateException("安装版升级未启动，请确认管理员权限后重试。", exception);
        }
    }

    public static void CleanupUpdateCache(
        string updatesRoot,
        Version installedVersion,
        Action<string>? log = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        ArgumentNullException.ThrowIfNull(installedVersion);
        if (!Directory.Exists(updatesRoot)) return;

        var normalizedRoot = Path.GetFullPath(updatesRoot).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - StaleCacheAge;
        foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var fullPath = info.FullName.TrimEnd(Path.DirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;

                var statePath = Path.Combine(info.FullName, PendingStateFileName);
                var state = ReadPendingState(statePath);
                var completed = state is not null &&
                                Version.TryParse(state.TargetVersion, out var targetVersion) &&
                                installedVersion >= targetVersion;
                var staleInstallerCache = info.LastWriteTimeUtc <= cutoff &&
                                          Directory.EnumerateFiles(
                                                  info.FullName,
                                                  "LazyForza-*-win-x64-setup.exe",
                                                  SearchOption.TopDirectoryOnly)
                                              .Any();
                if (!completed && !staleInstallerCache) continue;

                info.Delete(true);
                log?.Invoke(completed
                    ? $"Removed completed installer update cache for {state!.TargetVersion}."
                    : $"Removed stale installer update cache {info.Name}.");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string WritePendingState(PreparedUpdate update, string logPath)
    {
        Directory.CreateDirectory(update.WorkDirectory);
        var statePath = Path.Combine(update.WorkDirectory, PendingStateFileName);
        var state = new InstallerUpdateState(
            update.Version.ToString(3),
            Path.GetFullPath(logPath),
            DateTimeOffset.UtcNow);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions));
        return statePath;
    }

    private static InstallerUpdateState? ReadPendingState(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallerUpdateState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record InstallerUpdateState(
        string TargetVersion,
        string LogPath,
        DateTimeOffset StartedAt);
}
