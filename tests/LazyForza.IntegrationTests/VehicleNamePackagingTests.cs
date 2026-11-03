using System.Diagnostics;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class VehicleNamePackagingTests
{
    [TestMethod]
    [DataRow("powershell.exe")]
    [DataRow("pwsh.exe")]
    public async Task PackagingPinsUpstreamAndRetainsValidatedSnapshotsWhenOffline(string shell)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "Test-VehicleNamePackaging.ps1"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("PSModulePath"); // Let each PowerShell edition load its own built-in modules.
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory.FullName, "scripts", "Test-VehicleNamePackaging.ps1") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, await output + await error);
        StringAssert.Contains(await output, "VEHICLE_NAME_PACKAGING_TESTS_PASSED");
    }
}
