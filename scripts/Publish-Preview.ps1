[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-[0-9A-Za-z-]+([.-][0-9A-Za-z-]+)*$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ReleaseNotesPath,

    [string]$TagMessage,

    [switch]$SkipBuild,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previewRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\dev-preview'))
$notesPath = [IO.Path]::GetFullPath($ReleaseNotesPath)
$tag = "v$Version"
$packageName = "LazyForza-$Version-win-x64.zip"
$packagePath = Join-Path $previewRoot $packageName
$checksumPath = "$packagePath.sha256"
$githubRepository = 'Laz22y/LazyForza'

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Preview notes file was not found: $notesPath"
}
$notes = Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw 'Preview notes must not be empty.'
}

Push-Location $repositoryRoot
try {
    $branch = (git branch --show-current).Trim()
    Assert-LastExitCode -Operation 'Current branch lookup'
    if ($branch -ne 'main') {
        throw "Preview publishing must run from main; current branch is $branch."
    }

    $status = @(git status --porcelain)
    Assert-LastExitCode -Operation 'Git status'
    if ($status.Count -ne 0) {
        throw "Working tree must be clean before publishing a preview:`n$($status -join "`n")"
    }

    if (@(git tag --list $tag).Count -ne 0) {
        throw "Local tag $tag already exists; refusing to overwrite it."
    }
    Assert-LastExitCode -Operation "Local tag lookup for $tag"

    gh release view $tag --repo $githubRepository *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub release $tag already exists; refusing to overwrite it."
    }

    if ($ValidateOnly) {
        Write-Output 'PREVIEW_INPUTS_OK=True'
        Write-Output "VERSION=$Version"
        Write-Output "NOTES=$notesPath"
        return
    }

    if (-not $SkipBuild) {
        & dotnet build LazyForza.sln --no-restore -c Release
        Assert-LastExitCode -Operation 'dotnet build'

        & dotnet test LazyForza.sln --no-build --no-restore -c Release
        Assert-LastExitCode -Operation 'dotnet test'

        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'New-DevPreview.ps1') `
            -Version $Version
        Assert-LastExitCode -Operation 'preview packaging'
    }

    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Preview package or checksum is missing for $Version."
    }
    $localHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    $declaredHash = ((Get-Content -LiteralPath $checksumPath -Raw -Encoding ASCII).Trim() `
            -split '\s+')[0].ToUpperInvariant()
    if ($localHash -ne $declaredHash) {
        throw "Preview package hash does not match its sidecar: $localHash / $declaredHash"
    }

    $head = (git rev-parse HEAD).Trim()
    Assert-LastExitCode -Operation 'HEAD lookup'
    if ([string]::IsNullOrWhiteSpace($TagMessage)) {
        $TagMessage = "LazyForza $Version preview"
    }
    git tag -a $tag -m $TagMessage
    Assert-LastExitCode -Operation "Create tag $tag"

    git push origin main
    Assert-LastExitCode -Operation 'Push main'
    git push origin $tag
    Assert-LastExitCode -Operation "Push $tag"

    gh release create $tag `
        $packagePath `
        $checksumPath `
        --repo $githubRepository `
        --title "LazyForza $Version" `
        --notes-file $notesPath `
        --prerelease `
        --verify-tag
    Assert-LastExitCode -Operation 'Create GitHub preview release'

    $release = gh release view $tag `
        --repo $githubRepository `
        --json tagName,isDraft,isPrerelease,targetCommitish,assets,url |
        ConvertFrom-Json
    Assert-LastExitCode -Operation 'Verify GitHub preview release'
    $package = @($release.assets | Where-Object name -eq $packageName)
    $checksum = @($release.assets | Where-Object name -eq "$packageName.sha256")
    if ($release.tagName -ne $tag -or
        $release.isDraft -or
        -not $release.isPrerelease -or
        $package.Count -ne 1 -or
        $checksum.Count -ne 1) {
        throw 'GitHub preview release verification failed.'
    }
    if ($package[0].digest -and
        $package[0].digest -ne "sha256:$($localHash.ToLowerInvariant())") {
        throw 'GitHub preview package digest verification failed.'
    }

    $remoteMain = (git ls-remote origin refs/heads/main).Split("`t")[0]
    $remoteTag = (git ls-remote origin "refs/tags/$tag^{}" ).Split("`t")[0]
    if ($remoteMain -ne $head -or $remoteTag -ne $head) {
        throw 'Remote main or annotated preview tag does not point to the published commit.'
    }

    Write-Output "PREVIEW_RELEASE=$($release.url)"
    Write-Output "PACKAGE=$packagePath"
    Write-Output "SHA256=$localHash"
}
finally {
    Pop-Location
}
