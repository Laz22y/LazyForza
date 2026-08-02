[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')]
    [string]$Version,

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previewRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\dev-preview'))
$packageName = "LazyForza-$Version-$Runtime"
$workRoot = [System.IO.Path]::GetFullPath((Join-Path $previewRoot "_work-$Version"))
$publishPath = [System.IO.Path]::GetFullPath((Join-Path $workRoot 'publish'))
$stagePath = [System.IO.Path]::GetFullPath((Join-Path $workRoot $packageName))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $previewRoot "$packageName.zip"))
$hashPath = "$archivePath.sha256"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Parent
    )

    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $normalizedPath.StartsWith($normalizedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside development preview root: $normalizedPath"
    }
}

foreach ($path in @($workRoot, $publishPath, $stagePath, $archivePath, $hashPath)) {
    Assert-ChildPath -Path $path -Parent $previewRoot
}

New-Item -ItemType Directory -Force -Path $previewRoot | Out-Null
foreach ($path in @($workRoot, $archivePath, $hashPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $publishPath, $stagePath | Out-Null

# Keep the preview package flat for the same legacy-updater compatibility as a formal release.
& dotnet publish (Join-Path $repositoryRoot 'src\LazyForza.App\LazyForza.App.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishTrimmed=false `
    -p:SatelliteResourceLanguages=en `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$nestedPublishFiles = Get-ChildItem -LiteralPath $publishPath -Recurse -File |
    Where-Object { $_.DirectoryName -ne $publishPath }
if ($nestedPublishFiles) {
    throw "Development preview contains nested publish files: $($nestedPublishFiles.FullName -join ', ')"
}

Get-ChildItem -LiteralPath $publishPath -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Copy-Item -Path (Join-Path $publishPath '*') -Destination $stagePath -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\README.txt') -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\Start-Isolated.cmd') -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') `
    -Destination (Join-Path $stagePath 'THIRD_PARTY_NOTICES.txt')

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $stagePath 'DOTNET_LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $stagePath 'DOTNET_THIRD_PARTY_NOTICES.txt')

$buildInfo = @(
    "LazyForza $Version"
    'Development preview - not a formal release'
    "Runtime: $Runtime self-contained"
    "BuiltUtc: $([DateTimeOffset]::UtcNow.ToString('O'))"
)
Set-Content -LiteralPath (Join-Path $stagePath 'BUILDINFO.txt') -Value $buildInfo -Encoding UTF8

$forbidden = Get-ChildItem -LiteralPath $stagePath -Recurse -Force |
    Where-Object {
        $_.PSIsContainer -and $_.Name -in @('Logs', 'Recordings', 'Data') -or
        -not $_.PSIsContainer -and (
            $_.Extension -in @('.db', '.db-wal', '.db-shm', '.lfztelemetry', '.log', '.user') -or
            $_.Name -match '(^|[._-])(settings|config)([._-]|$)'
        )
    }
if ($forbidden) {
    throw "Development preview contains user-data candidates: $($forbidden.FullName -join ', ')"
}

$manifestLines = Get-ChildItem -LiteralPath $stagePath -Recurse -File |
    Where-Object Name -ne 'MANIFEST.sha256' |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stagePath.TrimEnd('\').Length + 1).Replace('\', '/')
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $relative"
    }
Set-Content -LiteralPath (Join-Path $stagePath 'MANIFEST.sha256') -Value $manifestLines -Encoding ASCII

Compress-Archive -LiteralPath $stagePath -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Set-Content -LiteralPath $hashPath -Value "$archiveHash  $([System.IO.Path]::GetFileName($archivePath))" -Encoding ASCII

Remove-Item -LiteralPath $workRoot -Recurse -Force

Write-Output "PACKAGE=$archivePath"
Write-Output "SHA256=$archiveHash"
Write-Output "SIZE=$((Get-Item -LiteralPath $archivePath).Length)"
