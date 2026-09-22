#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Full', 'Extended')]
    [string]$Suite = 'Quick',
    [ValidateSet('All', 'Analysis', 'Telemetry', 'Storage', 'Integration', 'EstatePeer')]
    [string]$Project = 'All',
    [string]$Filter,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = switch ($Project) {
    'All' { 'LazyForza.sln' }
    'Integration' { 'tests/LazyForza.IntegrationTests/LazyForza.IntegrationTests.csproj' }
    default { "tests/LazyForza.$Project.Tests/LazyForza.$Project.Tests.csproj" }
}
$categoryFilter = switch ($Suite) {
    'Quick' { 'TestCategory!=Extended' }
    'Extended' { 'TestCategory=Extended' }
    default { '' }
}
$effectiveFilter = if ($Filter -and $categoryFilter) { "($categoryFilter)&($Filter)" }
    elseif ($Filter) { $Filter } else { $categoryFilter }
$runId = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Suite.ToLowerInvariant(), [guid]::NewGuid().ToString('N').Substring(0, 8)
$results = Join-Path $root "artifacts/tests/$runId"
New-Item -ItemType Directory -Path $results -Force | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
Push-Location $root
try {
    Write-Output "Suite: $Suite | Project: $Project | Configuration: $Configuration"
    if ($effectiveFilter) { Write-Output "Filter: $effectiveFilter" }
    $buildSeconds = 0
    if (-not $NoBuild) {
        & dotnet build $target --no-restore -c $Configuration --verbosity minimal *> (Join-Path $results 'build.log')
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath (Join-Path $results 'build.log') -Tail 60
            throw 'Build failed. Restore with NuGet.Config first if dependencies have changed.'
        }
        $buildSeconds = $watch.Elapsed.TotalSeconds
        Write-Output ('Build: {0:N1}s' -f $buildSeconds)
    }
    $testArguments = @('test', $target, '--no-build', '--no-restore', '-c', $Configuration,
        '--verbosity', 'minimal', '--results-directory', $results, '--logger', 'trx')
    if ($effectiveFilter) { $testArguments += @('--filter', $effectiveFilter) }
    & dotnet @testArguments *> (Join-Path $results 'test.log')
    $testExit = $LASTEXITCODE
    $watch.Stop()
    $files = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -Recurse)
    $total = 0; $passed = 0; $failed = 0; $skipped = 0
    $timings = foreach ($file in $files) {
        [xml]$result = Get-Content -LiteralPath $file.FullName -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        $total += [int]$counters.total
        $passed += [int]$counters.passed
        $failed += [int]$counters.failed + [int]$counters.error
        $skipped += [int]$counters.notExecuted + [int]$counters.inconclusive
        foreach ($test in $result.TestRun.Results.UnitTestResult) {
            if ($test.duration) {
                [pscustomobject]@{ Test = $test.testName; Seconds = [Math]::Round([TimeSpan]::Parse($test.duration).TotalSeconds, 3) }
            }
        }
    }
    $summary = [ordered]@{
        Suite = $Suite; Project = $Project; Filter = $effectiveFilter; Configuration = $Configuration
        Total = $total; Passed = $passed; Failed = $failed; Skipped = $skipped
        BuildSeconds = [Math]::Round($buildSeconds, 2)
        TestSeconds = [Math]::Round($watch.Elapsed.TotalSeconds - $buildSeconds, 2)
        Slowest = @($timings | Sort-Object Seconds -Descending | Select-Object -First 5)
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding utf8
    Write-Output "Tests: $passed passed, $failed failed, $skipped skipped / $total"
    Write-Output "Test time: $($summary.TestSeconds)s | Results: $results"
    if ($testExit -ne 0 -or $failed -ne 0 -or $total -eq 0) {
        Get-Content -LiteralPath (Join-Path $results 'test.log') -Tail 70
        throw 'Test run failed or the filter selected no tests.'
    }
}
finally { Pop-Location }
