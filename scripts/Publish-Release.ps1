[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ReleaseNotesPath,

    [string]$TagMessage,

    [switch]$SkipBuild,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
$logRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'logs'))
$notesPath = [IO.Path]::GetFullPath($ReleaseNotesPath)
$tag = "v$Version"
$packageName = "LazyForza-$Version-win-x64.zip"
$packagePath = Join-Path $releaseRoot $packageName
$checksumPath = "$packagePath.sha256"
$expectedTitle = "LazyForza $Version"
$githubRepository = 'Laz22y/LazyForza'
$gitCodeRepositoryUri = 'https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza'
$gitCodeCredentialTarget = 'LazyForza-GitCode-Release'
$verifyRoot = Join-Path $releaseRoot "_verify-$Version"

function Assert-LastExitCode {
    param(
        [Parameter(Mandatory)]
        [string]$Operation,
        [string]$LogPath
    )

    if ($LASTEXITCODE -eq 0) {
        return
    }

    if ($LogPath -and (Test-Path -LiteralPath $LogPath)) {
        Get-Content -LiteralPath $LogPath -Tail 80
    }
    throw "$Operation failed with exit code $LASTEXITCODE."
}

function Assert-SafeChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Parent
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith(
            $resolvedParent,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside $resolvedParent`: $resolvedPath"
    }
}

function Get-GitHubToken {
    $filled = @('protocol=https', 'host=github.com', '') |
        git credential-manager get
    Assert-LastExitCode -Operation 'Git Credential Manager lookup'
    $passwordLine = $filled |
        Where-Object { $_ -like 'password=*' } |
        Select-Object -First 1
    if (-not $passwordLine) {
        throw 'Git Credential Manager did not return a GitHub credential.'
    }
    return $passwordLine.Substring(9)
}

function Invoke-GitCodeRequest {
    param(
        [Parameter(Mandatory)]
        [Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)]
        [Net.Http.HttpMethod]$Method,
        [Parameter(Mandatory)]
        [string]$Uri,
        [Net.Http.HttpContent]$Content,
        [int[]]$AllowedStatusCodes = @(200)
    )

    $request = [Net.Http.HttpRequestMessage]::new($Method, $Uri)
    try {
        if ($Content) {
            $request.Content = $Content
        }
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -notin $AllowedStatusCodes) {
                throw "GitCode returned HTTP $([int]$response.StatusCode) for $Method $Uri`: $body"
            }
            return @{
                StatusCode = [int]$response.StatusCode
                Body = $body
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Send-GitCodeAsset {
    param(
        [Parameter(Mandatory)]
        [Net.Http.HttpClient]$ApiClient,
        [Parameter(Mandatory)]
        [Net.Http.HttpClient]$UploadClient,
        [Parameter(Mandatory)]
        [IO.FileInfo]$File
    )

    $encodedName = [Uri]::EscapeDataString($File.Name)
    $specResult = Invoke-GitCodeRequest `
        -Client $ApiClient `
        -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$gitCodeRepositoryUri/releases/$tag/upload_url?file_name=$encodedName"
    $spec = $specResult.Body | ConvertFrom-Json

    $stream = [IO.File]::Open(
        $File.FullName,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $content = [Net.Http.StreamContent]::new($stream, 131072)
        try {
            $content.Headers.ContentLength = $stream.Length
            $request = [Net.Http.HttpRequestMessage]::new(
                [Net.Http.HttpMethod]::Put,
                [Uri]$spec.url)
            try {
                foreach ($header in $spec.headers.PSObject.Properties) {
                    if ($header.Name -eq 'Content-Type') {
                        $content.Headers.ContentType =
                            [Net.Http.Headers.MediaTypeHeaderValue]::Parse(
                                [string]$header.Value)
                    }
                    else {
                        $request.Headers.TryAddWithoutValidation(
                            $header.Name,
                            [string]$header.Value) | Out-Null
                    }
                }
                $request.Content = $content
                $response = $UploadClient.SendAsync(
                    $request,
                    [Net.Http.HttpCompletionOption]::ResponseHeadersRead
                ).GetAwaiter().GetResult()
                try {
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if (-not $response.IsSuccessStatusCode) {
                        throw "GitCode upload failed for $($File.Name) with HTTP $([int]$response.StatusCode): $body"
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            finally {
                $request.Dispose()
            }
        }
        finally {
            $content.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-TestCount {
    param([Parameter(Mandatory)][string]$ResultsPath)

    $resultFiles = @(Get-ChildItem -LiteralPath $ResultsPath -Recurse -Filter '*.trx')
    if ($resultFiles.Count -eq 0) {
        throw "No TRX test results found under $ResultsPath."
    }
    $total = 0
    foreach ($file in $resultFiles) {
        [xml]$result = Get-Content -LiteralPath $file.FullName -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if (-not $counters) {
            throw "TRX counters are missing from $($file.FullName)."
        }
        if ([int]$counters.failed -ne 0 -or [int]$counters.error -ne 0) {
            throw "TRX contains failed tests: $($file.FullName)."
        }
        $total += [int]$counters.passed
    }
    return $total
}

Add-Type -AssemblyName System.Net.Http
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LazyForzaReleaseCredentialReader
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(
        string target, uint type, uint reserved, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern void CredFree(IntPtr buffer);
}
'@

function Test-ReleaseDestinations {
    $env:GH_TOKEN = Get-GitHubToken
    try {
        $githubLogin = (gh api user --jq .login).Trim()
        Assert-LastExitCode -Operation 'GitHub authentication preflight'
        if ($githubLogin -ne 'Laz22y') {
            throw "Unexpected GitHub account: $githubLogin"
        }
        if (Test-GitHubReleaseExists -ReleaseTag $tag) {
            throw "GitHub release $tag already exists; refusing to overwrite it."
        }
    }
    finally {
        $env:GH_TOKEN = $null
    }

    $preflightCredentialPtr = [IntPtr]::Zero
    $preflightToken = $null
    if (-not [LazyForzaReleaseCredentialReader]::CredRead(
            $gitCodeCredentialTarget,
            1,
            0,
            [ref]$preflightCredentialPtr)) {
        throw "GitCode credential is unavailable. Error: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    try {
        $credential = [Runtime.InteropServices.Marshal]::PtrToStructure(
            $preflightCredentialPtr,
            [type][LazyForzaReleaseCredentialReader+Credential])
        $preflightToken = [Runtime.InteropServices.Marshal]::PtrToStringUni(
            $credential.CredentialBlob,
            [int]($credential.CredentialBlobSize / 2))
        if ([string]::IsNullOrWhiteSpace($preflightToken)) {
            throw 'The saved GitCode token is empty.'
        }

        $client = [Net.Http.HttpClient]::new()
        try {
            $client.Timeout = [TimeSpan]::FromSeconds(30)
            $client.DefaultRequestHeaders.Authorization =
                [Net.Http.Headers.AuthenticationHeaderValue]::new(
                    'Bearer',
                    $preflightToken)
            $result = Invoke-GitCodeRequest `
                -Client $client `
                -Method ([Net.Http.HttpMethod]::Get) `
                -Uri "$gitCodeRepositoryUri/releases/tags/$tag" `
                -AllowedStatusCodes @(200, 404)
            if ($result.StatusCode -eq 200) {
                throw "GitCode release $tag already exists; refusing to overwrite it."
            }
        }
        finally {
            $client.Dispose()
        }
    }
    finally {
        if ($preflightCredentialPtr -ne [IntPtr]::Zero) {
            [LazyForzaReleaseCredentialReader]::CredFree($preflightCredentialPtr)
        }
        $preflightToken = $null
    }
}

function Test-GitHubReleaseExists {
    param(
        [Parameter(Mandatory)]
        [string]$ReleaseTag
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        gh release view $ReleaseTag --repo $githubRepository *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

Set-Location -LiteralPath $repositoryRoot
New-Item -ItemType Directory -Force -Path $releaseRoot, $logRoot | Out-Null
Assert-SafeChildPath -Path $verifyRoot -Parent $releaseRoot

if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Release notes not found: $notesPath"
}
$notes = [string](Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8)
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw 'Release notes are empty.'
}
if ($notes -match "(?m)^#\s*$([regex]::Escape($expectedTitle))\s*$") {
    throw 'Release notes must not repeat the release title as a top-level heading.'
}
$validationHeading = -join @([char]0x9A8C, [char]0x8BC1)
if ($notes -match "(?m)^#{1,6}\s*$([regex]::Escape($validationHeading))\s*$") {
    throw 'Release notes must not include a validation section.'
}
$forbiddenReleaseSentence = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String(
        '5Y+R6KGM5YyF5LiN5YyF5ZCr5byA5Y+R6ICF55qE6K6+572u44CB5ZyI6YCf44CB6L2m6L6G5a2m5Lmg44CB5pel5b+X44CB5b2V5Yi25oiW6Ieq5a6a5LmJ6LWb6YGT44CC'))
if ($notes.Contains($forbiddenReleaseSentence)) {
    throw 'Release notes contain the forbidden package-content sentence.'
}

$branch = (git branch --show-current).Trim()
Assert-LastExitCode -Operation 'Current branch lookup'
if ($branch -ne 'main') {
    throw "Release must run from main; current branch is $branch."
}
$appProject = Join-Path $repositoryRoot 'src\LazyForza.App\LazyForza.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
$projectVersion = [string]$projectXml.Project.PropertyGroup.Version |
    Select-Object -First 1
if ($projectVersion -ne $Version) {
    throw "App version is $projectVersion; expected $Version."
}
if ($projectVersion -match '-') {
    throw "Prerelease app version cannot be published as stable: $projectVersion"
}
if ($ValidateOnly) {
    Write-Output 'RELEASE_INPUTS_OK=True'
    Write-Output "VERSION=$Version"
    Write-Output "NOTES=$notesPath"
    return
}

$status = @(git status --porcelain)
Assert-LastExitCode -Operation 'Git status'
if ($status.Count -ne 0) {
    throw "Working tree must be clean before publishing:`n$($status -join "`n")"
}
Test-ReleaseDestinations

$restoreLog = Join-Path $logRoot "restore-$Version.log"
$buildLog = Join-Path $logRoot "build-$Version.log"
$testLog = Join-Path $logRoot "test-$Version.log"
$testResultsRoot = Join-Path $logRoot "test-results-$Version"
$packageLog = Join-Path $logRoot "package-$Version.log"
$testCount = 0

if (-not $SkipBuild) {
    & dotnet restore LazyForza.sln --configfile NuGet.Config --verbosity minimal *>&1 |
        Out-File -LiteralPath $restoreLog -Encoding UTF8
    Assert-LastExitCode -Operation 'dotnet restore' -LogPath $restoreLog

    & dotnet build LazyForza.sln --no-restore -c Release --verbosity minimal *>&1 |
        Out-File -LiteralPath $buildLog -Encoding UTF8
    Assert-LastExitCode -Operation 'dotnet build' -LogPath $buildLog

    if (Test-Path -LiteralPath $testResultsRoot) {
        Remove-Item -LiteralPath $testResultsRoot -Recurse -Force
    }
    & dotnet test LazyForza.sln `
        --no-build `
        --no-restore `
        -c Release `
        --verbosity minimal `
        --results-directory $testResultsRoot `
        --logger "trx;LogFilePrefix=release-$Version" *>&1 |
        Out-File -LiteralPath $testLog -Encoding UTF8
    Assert-LastExitCode -Operation 'dotnet test' -LogPath $testLog
    $testCount = Get-TestCount -ResultsPath $testResultsRoot

    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'New-Release.ps1') `
        -Version $Version `
        -Runtime win-x64 *>&1 |
        Out-File -LiteralPath $packageLog -Encoding UTF8
    Assert-LastExitCode -Operation 'release packaging' -LogPath $packageLog
}

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Release package or checksum is missing for $Version."
}
$localHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$declaredHash = ((Get-Content -LiteralPath $checksumPath -Raw -Encoding ASCII).Trim() `
        -split '\s+')[0].ToUpperInvariant()
if ($localHash -ne $declaredHash) {
    throw "Local package hash does not match its sidecar: $localHash / $declaredHash"
}

$head = (git rev-parse HEAD).Trim()
Assert-LastExitCode -Operation 'HEAD lookup'
$existingTagCommit = git rev-list -n 1 $tag 2>$null
if ($LASTEXITCODE -eq 0) {
    if ($existingTagCommit.Trim() -ne $head) {
        throw "$tag already points to a different commit."
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($TagMessage)) {
        $TagMessage = "$expectedTitle release"
    }
    git tag -a $tag -m $TagMessage
    Assert-LastExitCode -Operation "Create tag $tag"
}

git push origin main
Assert-LastExitCode -Operation 'Push main'
git push origin $tag
Assert-LastExitCode -Operation "Push $tag"

$env:GH_TOKEN = Get-GitHubToken
try {
    $githubLogin = (gh api user --jq .login).Trim()
    Assert-LastExitCode -Operation 'GitHub authentication'
    if ($githubLogin -ne 'Laz22y') {
        throw "Unexpected GitHub account: $githubLogin"
    }

    if (Test-GitHubReleaseExists -ReleaseTag $tag) {
        throw "GitHub release $tag already exists; refusing to overwrite it."
    }
    gh release create $tag `
        $packagePath `
        $checksumPath `
        --repo $githubRepository `
        --title $expectedTitle `
        --notes-file $notesPath `
        --verify-tag
    Assert-LastExitCode -Operation 'Create GitHub release'

    $githubRelease = gh release view $tag `
        --repo $githubRepository `
        --json tagName,name,isDraft,isPrerelease,url,body,assets |
        ConvertFrom-Json
    Assert-LastExitCode -Operation 'Verify GitHub release'
    $githubPackage = @($githubRelease.assets |
        Where-Object name -eq $packageName)
    if ($githubPackage.Count -ne 1 -or
        $githubPackage[0].digest -ne "sha256:$($localHash.ToLowerInvariant())") {
        throw 'GitHub package digest verification failed.'
    }
}
finally {
    $env:GH_TOKEN = $null
}

$credentialPtr = [IntPtr]::Zero
$gitCodeToken = $null
if (-not [LazyForzaReleaseCredentialReader]::CredRead(
        $gitCodeCredentialTarget,
        1,
        0,
        [ref]$credentialPtr)) {
    throw "GitCode credential is unavailable. Error: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}

try {
    $credential = [Runtime.InteropServices.Marshal]::PtrToStructure(
        $credentialPtr,
        [type][LazyForzaReleaseCredentialReader+Credential])
    $gitCodeToken = [Runtime.InteropServices.Marshal]::PtrToStringUni(
        $credential.CredentialBlob,
        [int]($credential.CredentialBlobSize / 2))
    if ([string]::IsNullOrWhiteSpace($gitCodeToken)) {
        throw 'The saved GitCode token is empty.'
    }

    $apiClient = [Net.Http.HttpClient]::new()
    $uploadClient = [Net.Http.HttpClient]::new()
    try {
        $apiClient.Timeout = [TimeSpan]::FromSeconds(60)
        $apiClient.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new(
                'Bearer',
                $gitCodeToken)
        $apiClient.DefaultRequestHeaders.Accept.ParseAdd('application/json')
        $uploadClient.Timeout = [TimeSpan]::FromMinutes(20)

        $preflight = Invoke-GitCodeRequest `
            -Client $apiClient `
            -Method ([Net.Http.HttpMethod]::Get) `
            -Uri "$gitCodeRepositoryUri/releases/tags/$tag" `
            -AllowedStatusCodes @(200, 404)
        if ($preflight.StatusCode -eq 200) {
            throw "GitCode release $tag already exists; refusing to overwrite it."
        }

        $gitCodePayload = @{
            tag_name = $tag
            name = $expectedTitle
            body = $notes
            target_commitish = 'main'
            release_status = 'latest'
        } | ConvertTo-Json
        $releaseContent = [Net.Http.ByteArrayContent]::new(
            [Text.Encoding]::UTF8.GetBytes($gitCodePayload))
        try {
            $releaseContent.Headers.ContentType =
                [Net.Http.Headers.MediaTypeHeaderValue]::Parse(
                    'application/json; charset=utf-8')
            Invoke-GitCodeRequest `
                -Client $apiClient `
                -Method ([Net.Http.HttpMethod]::Post) `
                -Uri "$gitCodeRepositoryUri/releases" `
                -Content $releaseContent | Out-Null
        }
        finally {
            $releaseContent.Dispose()
        }

        Send-GitCodeAsset `
            -ApiClient $apiClient `
            -UploadClient $uploadClient `
            -File (Get-Item -LiteralPath $packagePath)
        Send-GitCodeAsset `
            -ApiClient $apiClient `
            -UploadClient $uploadClient `
            -File (Get-Item -LiteralPath $checksumPath)

        $gitCodeResult = Invoke-GitCodeRequest `
            -Client $apiClient `
            -Method ([Net.Http.HttpMethod]::Get) `
            -Uri "$gitCodeRepositoryUri/releases/tags/$tag"
        $gitCodeRelease = $gitCodeResult.Body | ConvertFrom-Json
        $binaryAssets = @($gitCodeRelease.assets |
            Where-Object { $_.name -in @($packageName, "$packageName.sha256") })
        if ($gitCodeRelease.tag_name -ne $tag -or
            $gitCodeRelease.name -ne $expectedTitle -or
            $binaryAssets.Count -ne 2) {
            throw 'GitCode release metadata verification failed.'
        }
    }
    finally {
        $apiClient.Dispose()
        $uploadClient.Dispose()
    }
}
finally {
    if ($credentialPtr -ne [IntPtr]::Zero) {
        [LazyForzaReleaseCredentialReader]::CredFree($credentialPtr)
    }
    $gitCodeToken = $null
}

if (Test-Path -LiteralPath $verifyRoot) {
    Remove-Item -LiteralPath $verifyRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $verifyRoot | Out-Null
try {
    $downloadedPackage = Join-Path $verifyRoot $packageName
    $downloadedChecksum = "$downloadedPackage.sha256"
    $downloadBase =
        "$gitCodeRepositoryUri/releases/$tag/attach_files"
    & curl.exe -sS -fL --retry 3 --connect-timeout 30 `
        -A 'LazyForza-Updater/release-validation' `
        -o $downloadedPackage `
        "$downloadBase/$packageName/download"
    Assert-LastExitCode -Operation 'Download GitCode package'
    & curl.exe -sS -fL --retry 3 --connect-timeout 30 `
        -A 'LazyForza-Updater/release-validation' `
        -o $downloadedChecksum `
        "$downloadBase/$packageName.sha256/download"
    Assert-LastExitCode -Operation 'Download GitCode checksum'

    $downloadedHash =
        (Get-FileHash -LiteralPath $downloadedPackage -Algorithm SHA256).Hash
    $downloadedDeclaredHash =
        ((Get-Content -LiteralPath $downloadedChecksum -Raw -Encoding ASCII).Trim() `
            -split '\s+')[0].ToUpperInvariant()
    if ($downloadedHash -ne $localHash -or
        $downloadedDeclaredHash -ne $localHash) {
        throw 'GitCode public download verification failed.'
    }
}
finally {
    if (Test-Path -LiteralPath $verifyRoot) {
        Remove-Item -LiteralPath $verifyRoot -Recurse -Force
    }
}

$remoteRefs = @(git ls-remote origin `
    refs/heads/main `
    "refs/tags/$tag" `
    "refs/tags/$tag^{}")
Assert-LastExitCode -Operation 'Verify remote refs'
if (-not ($remoteRefs -match "^$head\s+refs/heads/main$") -or
    -not ($remoteRefs -match "^$head\s+refs/tags/$([regex]::Escape($tag))\^\{\}$")) {
    throw 'Remote main or annotated tag does not point to the released commit.'
}

Write-Output "RELEASE_OK=True"
Write-Output "VERSION=$Version"
Write-Output "COMMIT=$head"
Write-Output "TESTS=$testCount"
Write-Output "PACKAGE=$packagePath"
Write-Output "SIZE=$((Get-Item -LiteralPath $packagePath).Length)"
Write-Output "SHA256=$localHash"
Write-Output "GITHUB=https://github.com/$githubRepository/releases/tag/$tag"
Write-Output "GITCODE=https://gitcode.com/Laz22y/LazyForza/releases/$tag"
