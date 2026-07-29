[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Update-SingleMatch {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Pattern,
        [Parameter(Mandatory)]
        [string]$Replacement
    )

    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $matches = [regex]::Matches($text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected one version match in $Path, found $($matches.Count)."
    }
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    [IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
}

function Update-ExpectedMatches {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Pattern,
        [Parameter(Mandatory)]
        [string]$Replacement,
        [Parameter(Mandatory)]
        [int]$ExpectedCount
    )

    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $matches = [regex]::Matches($text, $Pattern)
    if ($matches.Count -ne $ExpectedCount) {
        throw "Expected $ExpectedCount version matches in $Path, found $($matches.Count)."
    }
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    [IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
}

$releaseTimestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$releaseDate = [DateTimeOffset]::Now.ToString('yyyy-MM-dd')
$releaseDateLabel = [DateTimeOffset]::Now.ToString('yyyy.MM.dd')
$updatedLabel = ([string][char]0x66F4) + ([string][char]0x65B0)

Update-SingleMatch `
    -Path (Join-Path $repositoryRoot 'src\LazyForza.App\LazyForza.App.csproj') `
    -Pattern '(<Version>)[^<]+(</Version>)' `
    -Replacement "`${1}$Version`${2}"

Update-SingleMatch `
    -Path (Join-Path $repositoryRoot 'scripts\New-Release.ps1') `
    -Pattern "(\[string\]\`$Version\s*=\s*')[^']+(')" `
    -Replacement "`${1}$Version`${2}"

Update-SingleMatch `
    -Path (Join-Path $repositoryRoot 'tests\LazyForza.IntegrationTests\ModuleAndOverlayTests.cs') `
    -Pattern '(Assert\.AreEqual\(")[^"]+(",\s*LazyForza\.App\.ApplicationVersionInfo\.Display\);)' `
    -Replacement "`${1}$Version`${2}"

Update-SingleMatch `
    -Path (Join-Path $repositoryRoot 'website\app.js') `
    -Pattern '(const RELEASE_FALLBACK = \{\s*tag:\s*"v)[^"]+(",\s*version:\s*")[^"]+(",\s*publishedAt:\s*")[^"]+(",\s*assetName:\s*"LazyForza-)[^"]+(-win-x64\.zip",\s*\};)' `
    -Replacement "`${1}$Version`${2}$Version`${3}$releaseTimestamp`${4}$Version`${5}"

$websiteIndexPath = Join-Path $repositoryRoot 'website\index.html'
Update-ExpectedMatches `
    -Path $websiteIndexPath `
    -Pattern 'releases/v\d+\.\d+\.\d+/attach_files/LazyForza-\d+\.\d+\.\d+-win-x64\.zip/download' `
    -Replacement "releases/v$Version/attach_files/LazyForza-$Version-win-x64.zip/download" `
    -ExpectedCount 1
Update-ExpectedMatches `
    -Path $websiteIndexPath `
    -Pattern 'releases/download/v\d+\.\d+\.\d+/LazyForza-\d+\.\d+\.\d+-win-x64\.zip' `
    -Replacement "releases/download/v$Version/LazyForza-$Version-win-x64.zip" `
    -ExpectedCount 1
Update-ExpectedMatches `
    -Path $websiteIndexPath `
    -Pattern '(<strong data-release-version>)v\d+\.\d+\.\d+(</strong>)' `
    -Replacement "`${1}v$Version`${2}" `
    -ExpectedCount 2
Update-ExpectedMatches `
    -Path $websiteIndexPath `
    -Pattern '(<time data-release-date datetime=")\d{4}-\d{2}-\d{2}(">)[^<]+(</time>)' `
    -Replacement "`${1}$releaseDate`${2}$releaseDateLabel $updatedLabel`${3}" `
    -ExpectedCount 2

Write-Output "VERSION_UPDATED=$Version"
