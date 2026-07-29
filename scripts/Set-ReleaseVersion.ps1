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

    $text = [IO.File]::ReadAllText($Path)
    $matches = [regex]::Matches($text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected one version match in $Path, found $($matches.Count)."
    }
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    [IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
}

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

Write-Output "VERSION_UPDATED=$Version"
