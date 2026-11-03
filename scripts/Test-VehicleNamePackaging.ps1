# Deterministic regression checks: no network requests, user caches or source snapshots are changed.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('lfz-catalog-build-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$updater = Join-Path $PSScriptRoot 'Update-VehicleNameCatalog.ps1'
$source = Join-Path $root 'source.json.gz'
$output = Join-Path $root 'staged.json.gz'
$global:lfzCatalogTestState = [pscustomobject]@{ Revision = ('b' * 40); Requests = [Collections.Generic.List[string]]::new(); Mode = 'normal' }

function Assert-Equal($expected, $actual) {
    if ($expected -ne $actual) { throw "Expected '$expected', got '$actual'." }
}
function Write-Fixture($path) {
    $cars = [ordered]@{}
    for ($i = 1; $i -le 100; $i++) { $cars[[string]$i] = "Old car $i" }
    $document = @{source='https://gist.github.com/HDR/0659d1717bc61504bf83750628963f4f'; author='HDR'; revision=('a' * 40); updatedAtUtc='2020-01-01T00:00:00Z'; cars=$cars}
    $file = [IO.File]::Create($path)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes(($document | ConvertTo-Json -Depth 4)); $gzip.Write($bytes, 0, $bytes.Length) }
    finally { $gzip.Dispose(); $file.Dispose() }
}
function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec)
    $global:lfzCatalogTestState.Requests.Add([string]$Uri)
    if ($global:lfzCatalogTestState.Mode -eq 'offline') { throw 'Simulated offline build' }
    if ($Uri -like '*/commits?per_page=1') {
        return @([pscustomobject]@{version=$global:lfzCatalogTestState.Revision; committed_at=([DateTime]::Parse('2030-01-01T00:00:00Z').ToUniversalTime())})
    }
    Assert-Equal "https://api.github.com/gists/0659d1717bc61504bf83750628963f4f/$($global:lfzCatalogTestState.Revision)" $Uri
    $map = [ordered]@{}
    for ($i = 1; $i -le 100; $i++) { $map["New car $i"] = $i }
    if ($global:lfzCatalogTestState.Mode -eq 'duplicate') { $map['Duplicate car'] = 1 }
    if ($global:lfzCatalogTestState.Mode -eq 'unsafe') { $map["Bad$([char]0x202e)car"] = 101 }
    $returnedRevision = if ($global:lfzCatalogTestState.Mode -eq 'revision') { 'd' * 40 } else { $global:lfzCatalogTestState.Revision }
    return [pscustomobject]@{id='0659d1717bc61504bf83750628963f4f'; history=@(@{version=$returnedRevision}); files=@{
        'Forza Horizon 6 Car Ordinals.json'=@{truncated=($global:lfzCatalogTestState.Mode -eq 'truncated'); content=($map | ConvertTo-Json)}
    }}
}

try {
    Write-Fixture $source
    $sourceHash = (Get-FileHash $source).Hash
    $result = & $updater -OutputPath $output -FallbackPath $source
    Assert-Equal 'Refreshed' $result.Status
    Assert-Equal $global:lfzCatalogTestState.Revision $result.Revision
    Assert-Equal '2030-01-01T00:00:00.0000000+00:00' $result.UpdatedAtUtc
    Assert-Equal 100 $result.VehicleCount
    Assert-Equal 2 $global:lfzCatalogTestState.Requests.Count
    Assert-Equal $sourceHash (Get-FileHash $source).Hash
    $global:lfzCatalogTestState.Requests.Clear()
    $result = & $updater -OutputPath $output -FallbackPath $source
    Assert-Equal 'Unchanged' $result.Status
    Assert-Equal 1 $global:lfzCatalogTestState.Requests.Count
    $global:lfzCatalogTestState.Revision = 'c' * 40
    foreach ($failure in @('offline','truncated','duplicate','unsafe','revision')) {
        $global:lfzCatalogTestState.Mode = $failure
        $fallbackOutput = Join-Path $root "$failure.json.gz"
        $result = & $updater -OutputPath $fallbackOutput -FallbackPath $source -AllowCached -WarningAction SilentlyContinue
        Assert-Equal 'CachedFallback' $result.Status
        Assert-Equal ('a' * 40) $result.Revision
        Assert-Equal $sourceHash (Get-FileHash $source).Hash
    }
    $global:lfzCatalogTestState.Mode = 'offline'
    $before = (Get-FileHash $output).Hash
    $threw = $false
    try { $null = & $updater -OutputPath $output -FallbackPath $source } catch { $threw = $true }
    Assert-Equal $true $threw
    Assert-Equal $before (Get-FileHash $output).Hash
    $invalid = Join-Path $root 'invalid.gz'
    Set-Content -LiteralPath $invalid -Value 'broken'
    $threw = $false
    try { $null = & $updater -OutputPath (Join-Path $root 'no-cache.gz') -FallbackPath $invalid -AllowCached -WarningAction SilentlyContinue } catch { $threw = $true }
    Assert-Equal $true $threw
    Write-Output 'VEHICLE_NAME_PACKAGING_TESTS_PASSED'
} finally {
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup outside test directory.' }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
