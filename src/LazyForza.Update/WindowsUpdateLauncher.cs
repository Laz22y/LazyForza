using System.Diagnostics;
using System.Text;

namespace LazyForza.Update;

public static class WindowsUpdateLauncher
{
    private const string InstallerFileName = "install-update.ps1";

    public static bool IsPackagedInstall(string installDirectory)
    {
        var root = Path.GetFullPath(installDirectory);
        return File.Exists(Path.Combine(root, "LazyForza.App.exe")) &&
               File.Exists(Path.Combine(root, "BUILDINFO.txt")) &&
               File.Exists(Path.Combine(root, "MANIFEST.sha256"));
    }

    public static Process Launch(
        PreparedUpdate update,
        string installDirectory,
        string dataRoot,
        int processId,
        bool noRestart = false)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LazyForza 自动更新仅支持 Windows。");

        var installRoot = Path.GetFullPath(installDirectory);
        if (!IsPackagedInstall(installRoot))
            throw new UpdateException("当前程序不是完整发行版。为保护开发目录，不能执行自动覆盖。");
        if (!Directory.Exists(update.PackageRoot) ||
            !File.Exists(Path.Combine(update.PackageRoot, "LazyForza.App.exe")))
            throw new UpdateException("准备好的更新包已丢失。");

        var scriptPath = WriteInstallerScript(update.WorkDirectory);
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powerShell)) powerShell = "powershell.exe";

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = update.WorkDirectory
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-SourceDirectory");
        startInfo.ArgumentList.Add(update.PackageRoot);
        startInfo.ArgumentList.Add("-TargetDirectory");
        startInfo.ArgumentList.Add(installRoot);
        startInfo.ArgumentList.Add("-WorkDirectory");
        startInfo.ArgumentList.Add(update.WorkDirectory);
        startInfo.ArgumentList.Add("-ExecutableName");
        startInfo.ArgumentList.Add("LazyForza.App.exe");
        startInfo.ArgumentList.Add("-DataRoot");
        startInfo.ArgumentList.Add(Path.GetFullPath(dataRoot));
        if (noRestart) startInfo.ArgumentList.Add("-NoRestart");

        if (!CanWriteDirectory(installRoot)) startInfo.Verb = "runas";
        return Process.Start(startInfo)
            ?? throw new UpdateException("无法启动更新安装程序。");
    }

    public static string WriteInstallerScript(string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(Path.GetFullPath(workDirectory), InstallerFileName);
        File.WriteAllText(path, InstallerScript, new UTF8Encoding(false));
        return path;
    }

    public static void CleanupCompletedUpdates(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot)) return;
        var normalizedRoot = Path.GetFullPath(updatesRoot).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var fullPath = info.FullName.TrimEnd(Path.DirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(Path.Combine(info.FullName, "install.complete")))
                    continue;
                info.Delete(true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        var marker = Path.Combine(directory, $".lazyforza-update-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
            File.Delete(marker);
            return true;
        }
        catch (IOException)
        {
            TryDeleteFile(marker);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(marker);
            return false;
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

    private const string InstallerScript = """
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)][int]$ProcessId,
            [Parameter(Mandatory = $true)][string]$SourceDirectory,
            [Parameter(Mandatory = $true)][string]$TargetDirectory,
            [Parameter(Mandatory = $true)][string]$WorkDirectory,
            [Parameter(Mandatory = $true)][string]$ExecutableName,
            [Parameter(Mandatory = $true)][string]$DataRoot,
            [switch]$NoRestart
        )

        $ErrorActionPreference = 'Stop'
        $logPath = Join-Path $WorkDirectory 'install.log'
        $backupRoot = Join-Path $WorkDirectory 'rollback'
        $createdList = New-Object System.Collections.Generic.List[string]

        function Write-InstallLog {
            param([string]$Message)
            Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) $Message" -Encoding UTF8
        }

        function Get-SafeChildPath {
            param([string]$Root, [string]$RelativePath)
            if ([string]::IsNullOrWhiteSpace($RelativePath) -or
                [System.IO.Path]::IsPathRooted($RelativePath) -or
                $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
                throw "Unsafe relative path: $RelativePath"
            }
            $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
            $fullPath = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
            if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Path escapes root: $RelativePath"
            }
            return $fullPath
        }

        function Get-Sha256 {
            param([string]$Path)
            $stream = [System.IO.File]::OpenRead($Path)
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
                }
                finally {
                    $sha.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
        }

        function Start-LazyForza {
            if ($NoRestart) { return }
            $env:LAZYFORZA_DATA_DIR = $DataRoot
            $executable = Get-SafeChildPath -Root $TargetDirectory -RelativePath $ExecutableName
            Start-Process -FilePath $executable -WorkingDirectory $TargetDirectory
        }

        try {
            New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
            Write-InstallLog "Waiting for PID $ProcessId."
            $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $running) {
                Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction Stop
            }
            if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
                throw "LazyForza did not exit within 120 seconds."
            }

            foreach ($required in @('LazyForza.App.exe', 'BUILDINFO.txt', 'MANIFEST.sha256')) {
                if (-not (Test-Path -LiteralPath (Get-SafeChildPath -Root $TargetDirectory -RelativePath $required) -PathType Leaf)) {
                    throw "Target is not a packaged LazyForza installation."
                }
                if (-not (Test-Path -LiteralPath (Get-SafeChildPath -Root $SourceDirectory -RelativePath $required) -PathType Leaf)) {
                    throw "Update package is incomplete."
                }
            }

            if (Test-Path -LiteralPath $backupRoot) {
                Remove-Item -LiteralPath $backupRoot -Recurse -Force
            }
            New-Item -ItemType Directory -Path $backupRoot | Out-Null

            $newManaged = @{}
            $sourceManifestPath = Get-SafeChildPath -Root $SourceDirectory -RelativePath 'MANIFEST.sha256'
            foreach ($line in Get-Content -LiteralPath $sourceManifestPath) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -notmatch '^([0-9A-Fa-f]{64})[ \t]+\*?(.+)$') {
                    throw "Invalid update manifest."
                }
                $newManaged[$Matches[2].Replace('/', '\').ToLowerInvariant()] = $true
            }

            $oldManaged = New-Object System.Collections.Generic.List[string]
            $targetManifestPath = Get-SafeChildPath -Root $TargetDirectory -RelativePath 'MANIFEST.sha256'
            foreach ($line in Get-Content -LiteralPath $targetManifestPath) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -match '^([0-9A-Fa-f]{64})[ \t]+\*?(.+)$') {
                    $oldManaged.Add($Matches[2])
                }
            }

            $sourcePrefix = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\') + '\'
            $sourceFiles = Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse
            foreach ($sourceFile in $sourceFiles) {
                $relative = $sourceFile.FullName.Substring($sourcePrefix.Length)
                $target = Get-SafeChildPath -Root $TargetDirectory -RelativePath $relative
                $parent = Split-Path -Parent $target
                if (Test-Path -LiteralPath $target -PathType Leaf) {
                    $backup = Get-SafeChildPath -Root $backupRoot -RelativePath $relative
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
                    Copy-Item -LiteralPath $target -Destination $backup -Force
                }
                else {
                    $createdList.Add($target)
                }
                New-Item -ItemType Directory -Force -Path $parent | Out-Null
                Copy-Item -LiteralPath $sourceFile.FullName -Destination $target -Force
            }

            foreach ($relative in $oldManaged) {
                $key = $relative.Replace('/', '\').ToLowerInvariant()
                if ($newManaged.ContainsKey($key)) { continue }
                $target = Get-SafeChildPath -Root $TargetDirectory -RelativePath $relative
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
                $backup = Get-SafeChildPath -Root $backupRoot -RelativePath $relative
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
                Copy-Item -LiteralPath $target -Destination $backup -Force
                Remove-Item -LiteralPath $target -Force
            }

            foreach ($line in Get-Content -LiteralPath $sourceManifestPath) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -notmatch '^([0-9A-Fa-f]{64})[ \t]+\*?(.+)$') {
                    throw "Invalid update manifest."
                }
                $expected = $Matches[1]
                $relative = $Matches[2]
                $installed = Get-SafeChildPath -Root $TargetDirectory -RelativePath $relative
                if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
                    throw "Installed file is missing: $relative"
                }
                $actual = Get-Sha256 -Path $installed
                if ($actual -ne $expected) {
                    throw "Installed file hash mismatch: $relative"
                }
            }

            Set-Content -LiteralPath (Join-Path $WorkDirectory 'install.complete') -Value 'ok' -Encoding ASCII
            Write-InstallLog 'Update installed successfully.'
            Start-LazyForza
            exit 0
        }
        catch {
            Write-InstallLog "Update failed: $($_.Exception.Message)"
            try {
                if (Test-Path -LiteralPath $backupRoot) {
                    $backupPrefix = [System.IO.Path]::GetFullPath($backupRoot).TrimEnd('\') + '\'
                    foreach ($backupFile in Get-ChildItem -LiteralPath $backupRoot -File -Recurse) {
                        $relative = $backupFile.FullName.Substring($backupPrefix.Length)
                        $target = Get-SafeChildPath -Root $TargetDirectory -RelativePath $relative
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                        Copy-Item -LiteralPath $backupFile.FullName -Destination $target -Force
                    }
                }
                foreach ($created in $createdList) {
                    if (Test-Path -LiteralPath $created -PathType Leaf) {
                        Remove-Item -LiteralPath $created -Force
                    }
                }
                Write-InstallLog 'Rollback completed.'
            }
            catch {
                Write-InstallLog "Rollback failed: $($_.Exception.Message)"
            }
            try { Start-LazyForza } catch { Write-InstallLog "Restart failed: $($_.Exception.Message)" }
            exit 1
        }
        """;
}
