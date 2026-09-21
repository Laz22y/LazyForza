#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CatalogPath,
    [Parameter(Mandatory)][string]$MinimumClientVersion,
    [Parameter(Mandatory)][string]$MaximumClientVersionExclusive,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long]$Sequence,
    [ValidateSet('stable','preview')][string]$Channel = 'preview',
    [DateTimeOffset]$ExpiresAt = [DateTimeOffset]::UtcNow.AddMonths(6),
    [string]$KeyName = 'LazyForza.ComponentSigning.v1',
    [string]$PublicKeyPath = (Join-Path $PSScriptRoot '../src/LazyForza.App/Assets/estate-peer-signing-public.pem'),
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json -AsHashtable
if ($catalog.Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$' -or $catalog.Runtime -ne 'win-x64') { throw 'Invalid component version or runtime.' }
if ($Channel -eq 'stable' -and $catalog.Version.Contains('-')) { throw 'A preview component cannot enter the stable channel.' }
if ($ExpiresAt -le [DateTimeOffset]::UtcNow) { throw 'The manifest expiration must be in the future.' }
if (-not $OutputDirectory) { $OutputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($CatalogPath)) }
if ($catalog.Revision -lt 1 -or $catalog.ServerRevision -notmatch '^[a-f0-9]{40}$') { throw 'Missing component revision or server provenance.' }
$releaseId = "$($catalog.Version)-r$($catalog.Revision)"
$archiveName = "LazyForza-EstatePeerHost-$releaseId-win-x64.zip"
$archive = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($CatalogPath))) $archiveName
if (-not (Test-Path -LiteralPath $archive) -or (Get-Item -LiteralPath $archive).Length -ne $catalog.DownloadBytes -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $catalog.ArchiveSha256) { throw 'The archive and catalog do not match.' }
$tag = "estate-peer-host-v$releaseId"
$catalog.DownloadUrls = @(
    "https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza.Components/releases/$tag/attach_files/$archiveName/download",
    "https://github.com/Laz22y/LazyForza.Components/releases/download/$tag/$archiveName"
)
$catalog.DownloadUrl = $catalog.DownloadUrls[1]
$payload = [ordered]@{
    SchemaVersion = 1; Sequence = $Sequence; Channel = $Channel
    MinimumClientVersion = $MinimumClientVersion; MaximumClientVersionExclusive = $MaximumClientVersionExclusive
    RaceProtocolVersion = 2; ProjectFormatVersion = 1; ExpiresAt = $ExpiresAt.ToUniversalTime().ToString('O'); Catalog = $catalog
} | ConvertTo-Json -Depth 8 -Compress
$bytes = [Text.Encoding]::UTF8.GetBytes($payload)
$key = [Security.Cryptography.CngKey]::Open($KeyName, [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider)
try {
    $signer = [Security.Cryptography.ECDsaCng]::new($key)
    try {
        if ($signer.ExportSubjectPublicKeyInfoPem().Trim() -ne (Get-Content -LiteralPath $PublicKeyPath -Raw).Trim()) {
            throw 'The signing key does not match the public key embedded in the client.'
        }
        $signature = $signer.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally { $signer.Dispose() }
}
finally { $key.Dispose() }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$signed = [ordered]@{ Payload = [Convert]::ToBase64String($bytes); Signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
$destination = Join-Path $OutputDirectory 'estate-peer-component.signed.json'
[IO.File]::WriteAllText($destination, $signed)
[IO.File]::WriteAllText((Join-Path $OutputDirectory "$archiveName.sha256"), "$($catalog.ArchiveSha256)  $archiveName`n")
# Useful for the bundled initial version; its trust remains anchored in the client binary.
$catalog | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'estate-peer-component.downloads.json') -Encoding utf8
Write-Output "Manifest: $destination"
Write-Output "Tag: $tag"
