[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.4.9',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller,
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
$packageName = "LazyForza-$Version-$Runtime"
$workRoot = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot '_work'))
$publishPath = [System.IO.Path]::GetFullPath((Join-Path $workRoot 'publish'))
$stagePath = [System.IO.Path]::GetFullPath((Join-Path $workRoot $packageName))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot "$packageName.zip"))
$hashPath = "$archivePath.sha256"
$setupPath = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot "LazyForza-$Version-$Runtime-setup.exe"))
$setupHashPath = "$setupPath.sha256"

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
        throw "Refusing to modify path outside release root: $normalizedPath"
    }
}

Assert-ChildPath -Path $workRoot -Parent $releaseRoot
Assert-ChildPath -Path $publishPath -Parent $releaseRoot
Assert-ChildPath -Path $stagePath -Parent $releaseRoot
Assert-ChildPath -Path $archivePath -Parent $releaseRoot
Assert-ChildPath -Path $setupPath -Parent $releaseRoot
Assert-ChildPath -Path $setupHashPath -Parent $releaseRoot

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $hashPath) {
    Remove-Item -LiteralPath $hashPath -Force
}
if (Test-Path -LiteralPath $setupPath) {
    Remove-Item -LiteralPath $setupPath -Force
}
if (Test-Path -LiteralPath $setupHashPath) {
    Remove-Item -LiteralPath $setupHashPath -Force
}
New-Item -ItemType Directory -Force -Path $publishPath, $stagePath | Out-Null

$catalogPath = Join-Path $repositoryRoot 'src\LazyForza.Storage\Assets\PlaygroundOfficialTracks.json.gz'
$catalogHash = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash
$expectedCatalogHash = '9EFD7EC2A8799E733E8CDB60819245534E45B6F0A6BF0FA2B334831A7E0330B6'
if ($catalogHash -ne $expectedCatalogHash) {
    throw "Embedded official track catalog hash mismatch. Expected $expectedCatalogHash, found $catalogHash."
}

# Keep packages flat while 1.1.0-1.1.1 update clients remain in use; those clients reject nested satellite paths.
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
    throw "Release publish contains nested files that are incompatible with LazyForza 1.1.0-1.1.1 updaters: $($nestedPublishFiles.FullName -join ', ')"
}

Get-ChildItem -LiteralPath $publishPath -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Copy-Item -Path (Join-Path $publishPath '*') -Destination $stagePath -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\README.txt') -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') `
    -Destination (Join-Path $stagePath 'THIRD_PARTY_NOTICES.txt')
$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
if (-not (Test-Path -LiteralPath $dotnetLicense) -or
    -not (Test-Path -LiteralPath $dotnetNotices)) {
    throw "The self-contained release requires the .NET license and third-party notices from $dotnetRoot."
}
Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $stagePath 'DOTNET_LICENSE.txt')
Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $stagePath 'DOTNET_THIRD_PARTY_NOTICES.txt')

if ($Sign) {
    & (Join-Path $repositoryRoot 'scripts\Sign-WindowsArtifacts.ps1') `
        -Path (Join-Path $stagePath 'LazyForza.App.exe')
    if ($LASTEXITCODE -ne 0) {
        throw "Application signing failed with exit code $LASTEXITCODE."
    }
}

$buildInfo = @(
    "LazyForza $Version"
    "Runtime: $Runtime self-contained"
    "BuiltUtc: $([DateTimeOffset]::UtcNow.ToString('O'))"
    "OfficialTrackCatalog: 2026.08.11.1"
    "OfficialTrackCatalogSha256: $catalogHash"
    "OfficialTracks: 86"
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
    throw "Release staging contains user-data candidates: $($forbidden.FullName -join ', ')"
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

if (-not $SkipInstaller) {
    $innoCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $innoCompiler) {
        $innoCandidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $innoCompiler = $innoCandidates |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
    if ($null -eq $innoCompiler) {
        throw 'Inno Setup 6 compiler was not found. Install Inno Setup 6 or use -SkipInstaller for portable-only local checks.'
    }
    $innoCompilerPath = if ($innoCompiler.PSObject.Properties.Name -contains 'Source') {
        $innoCompiler.Source
    } elseif ($innoCompiler.PSObject.Properties.Name -contains 'FullName') {
        $innoCompiler.FullName
    } else {
        [string]$innoCompiler
    }
    $numericVersion = ($Version -split '[-+]')[0]
    & $innoCompilerPath `
        "-dAppVersion=$Version" `
        "-dNumericVersion=$numericVersion" `
        "-dPublishDir=$stagePath" `
        "-dOutputDir=$releaseRoot" `
        (Join-Path $repositoryRoot 'packaging\installer\LazyForza.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Installer output is missing: $setupPath"
    }
    if ($Sign) {
        & (Join-Path $repositoryRoot 'scripts\Sign-WindowsArtifacts.ps1') -Path $setupPath
        if ($LASTEXITCODE -ne 0) {
            throw "Installer signing failed with exit code $LASTEXITCODE."
        }
    }
    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    Set-Content -LiteralPath $setupHashPath `
        -Value "$setupHash  $([System.IO.Path]::GetFileName($setupPath))" `
        -Encoding ASCII
}

Remove-Item -LiteralPath $workRoot -Recurse -Force

Write-Output "PACKAGE=$archivePath"
Write-Output "SHA256=$archiveHash"
Write-Output "SIZE=$((Get-Item -LiteralPath $archivePath).Length)"
if (-not $SkipInstaller) {
    Write-Output "INSTALLER=$setupPath"
    Write-Output "INSTALLER_SHA256=$setupHash"
    Write-Output "INSTALLER_SIZE=$((Get-Item -LiteralPath $setupPath).Length)"
}
