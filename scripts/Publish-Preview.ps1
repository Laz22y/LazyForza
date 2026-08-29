[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-[0-9A-Za-z-]+([.-][0-9A-Za-z-]+)*$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ReleaseNotesPath,

    [string]$TagMessage,

    [switch]$SkipBuild,

    [switch]$ValidateOnly,

    [switch]$GitCodeDirect
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previewRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\dev-preview'))
$logRoot = [IO.Path]::GetFullPath((Join-Path $previewRoot 'logs'))
$verifyRoot = [IO.Path]::GetFullPath((Join-Path $previewRoot "_verify-$Version"))
$notesPath = [IO.Path]::GetFullPath($ReleaseNotesPath)
$tag = "v$Version"
$title = "LazyForza $Version"
$packageName = "LazyForza-$Version-win-x64.zip"
$packagePath = Join-Path $previewRoot $packageName
$checksumPath = "$packagePath.sha256"
$githubRepository = 'Laz22y/LazyForza'
$gitCodeRepositoryUri = 'https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza'
$gitCodeCredentialTarget = 'LazyForza-GitCode-Release'

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

function New-GitCodeHttpClient {
    if (-not $GitCodeDirect) {
        return [Net.Http.HttpClient]::new()
    }

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    return [Net.Http.HttpClient]::new($handler)
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

function Receive-GitCodePublicAsset {
    param(
        [Parameter(Mandatory)]
        [Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $response = $Client.GetAsync(
        $Uri,
        [Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw "GitCode public download failed with HTTP $([int]$response.StatusCode): $Uri"
        }
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $output = [IO.File]::Create($DestinationPath)
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
            }
        }
        finally {
            $input.Dispose()
        }
    }
    finally {
        $response.Dispose()
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
        if (-not $counters -or
            [int]$counters.failed -ne 0 -or
            [int]$counters.error -ne 0) {
            throw "TRX contains missing counters or failed tests: $($file.FullName)."
        }
        $total += [int]$counters.passed
    }
    return $total
}

Add-Type -AssemblyName System.Net.Http
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LazyForzaPreviewCredentialReader
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

function Get-GitCodeToken {
    $credentialPtr = [IntPtr]::Zero
    if (-not [LazyForzaPreviewCredentialReader]::CredRead(
            $gitCodeCredentialTarget,
            1,
            0,
            [ref]$credentialPtr)) {
        throw "GitCode credential is unavailable. Error: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    try {
        $credential = [Runtime.InteropServices.Marshal]::PtrToStructure(
            $credentialPtr,
            [type][LazyForzaPreviewCredentialReader+Credential])
        $token = [Runtime.InteropServices.Marshal]::PtrToStringUni(
            $credential.CredentialBlob,
            [int]($credential.CredentialBlobSize / 2))
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw 'The saved GitCode token is empty.'
        }
        return $token
    }
    finally {
        if ($credentialPtr -ne [IntPtr]::Zero) {
            [LazyForzaPreviewCredentialReader]::CredFree($credentialPtr)
        }
    }
}

function Test-GitHubReleaseExists {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        gh release view $tag --repo $githubRepository *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Test-ReleaseDestinations {
    $env:GH_TOKEN = Get-GitHubToken
    try {
        $githubLogin = (gh api user --jq .login).Trim()
        Assert-LastExitCode -Operation 'GitHub authentication preflight'
        if ($githubLogin -ne 'Laz22y') {
            throw "Unexpected GitHub account: $githubLogin"
        }
        if (Test-GitHubReleaseExists) {
            throw "GitHub release $tag already exists; refusing to overwrite it."
        }
    }
    finally {
        $env:GH_TOKEN = $null
    }

    $gitCodeToken = Get-GitCodeToken
    $client = New-GitCodeHttpClient
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        $client.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $gitCodeToken)
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
        $gitCodeToken = $null
    }
}

if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Preview notes file was not found: $notesPath"
}
$notes = Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw 'Preview notes must not be empty.'
}
if ($notes -match "(?m)^#\s*$([regex]::Escape($title))\s*$") {
    throw 'Preview notes must not repeat the release title as a top-level heading.'
}
$validationHeading = -join @([char]0x9A8C, [char]0x8BC1)
if ($notes -match "(?m)^#{1,6}\s*$([regex]::Escape($validationHeading))\s*$") {
    throw 'Preview notes must not include a validation section.'
}
$forbiddenReleaseSentence = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String(
        '5Y+R6KGM5YyF5LiN5YyF5ZCr5byA5Y+R6ICF55qE6K6+572u44CB5ZyI6YCf44CB6L2m6L6G5a2m5Lmg44CB5pel5b+X44CB5b2V5Yi25oiW6Ieq5a6a5LmJ6LWb6YGT44CC'))
if ($notes.Contains($forbiddenReleaseSentence)) {
    throw 'Preview notes contain the forbidden package-content sentence.'
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Force -Path $previewRoot, $logRoot | Out-Null
    Assert-SafeChildPath -Path $verifyRoot -Parent $previewRoot

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

    Test-ReleaseDestinations
    if ($ValidateOnly) {
        Write-Output 'PREVIEW_INPUTS_OK=True'
        Write-Output "VERSION=$Version"
        Write-Output "NOTES=$notesPath"
        return
    }

    $restoreLog = Join-Path $logRoot "restore-$Version.log"
    $buildLog = Join-Path $logRoot "build-$Version.log"
    $testLog = Join-Path $logRoot "test-$Version.log"
    $testResultsRoot = Join-Path $logRoot "test-results-$Version"
    $packageLog = Join-Path $logRoot "package-$Version.log"
    $websiteLog = Join-Path $logRoot "website-$Version.log"
    $testCount = 0

    if (-not $SkipBuild) {
        & node (Join-Path $PSScriptRoot 'Test-WebsiteLocalization.cjs') *>&1 |
            Out-File -LiteralPath $websiteLog -Encoding UTF8
        Assert-LastExitCode -Operation 'website localization checks' -LogPath $websiteLog

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
            --logger "trx;LogFilePrefix=preview-$Version" *>&1 |
            Out-File -LiteralPath $testLog -Encoding UTF8
        Assert-LastExitCode -Operation 'dotnet test' -LogPath $testLog
        $testCount = Get-TestCount -ResultsPath $testResultsRoot

        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'New-DevPreview.ps1') `
            -Version $Version *>&1 |
            Out-File -LiteralPath $packageLog -Encoding UTF8
        Assert-LastExitCode -Operation 'preview packaging' -LogPath $packageLog
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
        $TagMessage = "$title preview"
    }
    git tag -a $tag -m $TagMessage
    Assert-LastExitCode -Operation "Create tag $tag"

    git push origin main
    Assert-LastExitCode -Operation 'Push main'
    git push origin $tag
    Assert-LastExitCode -Operation "Push $tag"

    $env:GH_TOKEN = Get-GitHubToken
    try {
        gh release create $tag `
            $packagePath `
            $checksumPath `
            --repo $githubRepository `
            --title $title `
            --notes-file $notesPath `
            --prerelease `
            --verify-tag
        Assert-LastExitCode -Operation 'Create GitHub preview release'

        $githubRelease = gh release view $tag `
            --repo $githubRepository `
            --json tagName,isDraft,isPrerelease,assets,url |
            ConvertFrom-Json
        Assert-LastExitCode -Operation 'Verify GitHub preview release'
        $githubPackage = @($githubRelease.assets | Where-Object name -eq $packageName)
        $githubChecksum = @($githubRelease.assets | Where-Object name -eq "$packageName.sha256")
        if ($githubRelease.tagName -ne $tag -or
            $githubRelease.isDraft -or
            -not $githubRelease.isPrerelease -or
            $githubPackage.Count -ne 1 -or
            $githubChecksum.Count -ne 1) {
            throw 'GitHub preview release verification failed.'
        }
        if ($githubPackage[0].digest -and
            $githubPackage[0].digest -ne "sha256:$($localHash.ToLowerInvariant())") {
            throw 'GitHub preview package digest verification failed.'
        }
    }
    finally {
        $env:GH_TOKEN = $null
    }

    $gitCodeToken = Get-GitCodeToken
    $apiClient = New-GitCodeHttpClient
    $uploadClient = New-GitCodeHttpClient
    try {
        $apiClient.Timeout = [TimeSpan]::FromSeconds(60)
        $apiClient.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $gitCodeToken)
        $apiClient.DefaultRequestHeaders.Accept.ParseAdd('application/json')
        $uploadClient.Timeout = [TimeSpan]::FromMinutes(20)

        $gitCodePayload = @{
            tag_name = $tag
            name = $title
            body = $notes.Trim()
            target_commitish = 'main'
            release_status = 'pre'
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
        $gitCodeAssets = @($gitCodeRelease.assets |
            Where-Object { $_.name -in @($packageName, "$packageName.sha256") })
        if ($gitCodeRelease.tag_name -ne $tag -or
            -not $gitCodeRelease.prerelease -or
            $gitCodeRelease.release_status -ne 'pre' -or
            $gitCodeAssets.Count -ne 2) {
            throw 'GitCode preview release metadata verification failed.'
        }
    }
    finally {
        $apiClient.Dispose()
        $uploadClient.Dispose()
        $gitCodeToken = $null
    }

    if (Test-Path -LiteralPath $verifyRoot) {
        Remove-Item -LiteralPath $verifyRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $verifyRoot | Out-Null
    try {
        $downloadedPackage = Join-Path $verifyRoot $packageName
        $downloadedChecksum = "$downloadedPackage.sha256"
        $downloadBase = "$gitCodeRepositoryUri/releases/$tag/attach_files"
        $downloadClient = New-GitCodeHttpClient
        try {
            $downloadClient.Timeout = [TimeSpan]::FromMinutes(10)
            $downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                'LazyForza-Updater/preview-validation')
            Receive-GitCodePublicAsset `
                -Client $downloadClient `
                -Uri "$downloadBase/$packageName/download" `
                -DestinationPath $downloadedPackage
            Receive-GitCodePublicAsset `
                -Client $downloadClient `
                -Uri "$downloadBase/$packageName.sha256/download" `
                -DestinationPath $downloadedChecksum
        }
        finally {
            $downloadClient.Dispose()
        }

        $downloadedHash =
            (Get-FileHash -LiteralPath $downloadedPackage -Algorithm SHA256).Hash
        $downloadedDeclaredHash =
            ((Get-Content -LiteralPath $downloadedChecksum -Raw -Encoding ASCII).Trim() `
                -split '\s+')[0].ToUpperInvariant()
        if ($downloadedHash -ne $localHash -or
            $downloadedDeclaredHash -ne $localHash) {
            throw 'GitCode public preview download verification failed.'
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
        throw 'Remote main or annotated preview tag does not point to the published commit.'
    }

    Write-Output 'PREVIEW_RELEASE_OK=True'
    Write-Output "VERSION=$Version"
    Write-Output "COMMIT=$head"
    Write-Output "TESTS=$testCount"
    Write-Output "PACKAGE=$packagePath"
    Write-Output "SIZE=$((Get-Item -LiteralPath $packagePath).Length)"
    Write-Output "SHA256=$localHash"
    Write-Output "GITHUB=https://github.com/$githubRepository/releases/tag/$tag"
    Write-Output "GITCODE=https://gitcode.com/Laz22y/LazyForza/releases/$tag"
}
finally {
    Pop-Location
}
