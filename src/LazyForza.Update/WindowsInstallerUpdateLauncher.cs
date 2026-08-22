using System.ComponentModel;
using System.Diagnostics;

namespace LazyForza.Update;

public static class WindowsInstallerUpdateLauncher
{
    public const string InstalledMarkerFileName = "LazyForza.Installation";

    public static Process Launch(PreparedUpdate update, string installDirectory)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LazyForza 安装版自动升级仅支持 Windows。");
        if (update.Kind != UpdatePackageKind.Installer ||
            !File.Exists(update.ArchivePath))
            throw new UpdateException("准备好的安装版升级程序已丢失。");

        var installRoot = Path.GetFullPath(installDirectory);
        if (!File.Exists(Path.Combine(installRoot, InstalledMarkerFileName)))
            throw new UpdateException("当前程序不是安装版，不能运行安装版升级程序。");

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
        startInfo.ArgumentList.Add("/AUTOUPDATE");
        try
        {
            return Process.Start(startInfo) ??
                   throw new UpdateException("无法启动安装版升级程序。");
        }
        catch (Win32Exception exception)
        {
            throw new UpdateException("安装版升级未启动，请确认管理员权限后重试。", exception);
        }
    }
}
