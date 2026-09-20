[CmdletBinding()]
param(
    [string]$Version = '0.1.0-experimental.1',
    [string]$DownloadUrl = '',
    [switch]$WriteClientCatalog
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+[-.a-zA-Z0-9]*$') { throw 'Invalid component version.' }
$output = Join-Path $repository ('artifacts/estate-peer-component/' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $output 'publish'
New-Item -ItemType Directory -Force -Path $publish | Out-Null
& dotnet publish (Join-Path $repository 'src/LazyForza.EstatePeer.Host/LazyForza.EstatePeer.Host.csproj') `
    -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false `
    -p:PublishTrimmed=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Component publish failed.' }
$dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $publish 'DOTNET_LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $publish 'DOTNET_THIRD_PARTY_NOTICES.txt')
$files = @(Get-ChildItem -LiteralPath $publish -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ Name = $_.Name; Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
if (Get-ChildItem -LiteralPath $publish -Directory) { throw 'Component publishing produced unsupported nested files.' }
$archive = Join-Path $output "LazyForza-EstatePeerHost-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
$catalog = [ordered]@{
    FormatVersion = 1; ControlVersion = 1; Version = $Version; Runtime = 'win-x64'
    DownloadBytes = (Get-Item -LiteralPath $archive).Length
    ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    DownloadUrl = $(if ($DownloadUrl) { $DownloadUrl } else { $null })
    Files = $files
}
$json = $catalog | ConvertTo-Json -Depth 5
$json | Set-Content -LiteralPath (Join-Path $output 'estate-peer-component.json') -Encoding utf8
if ($WriteClientCatalog) {
    $json | Set-Content -LiteralPath (Join-Path $repository 'src/LazyForza.App/Assets/estate-peer-component.json') -Encoding utf8
}
Write-Output ([pscustomobject]@{ Archive = $archive; DownloadBytes = $catalog.DownloadBytes; InstalledBytes = ($files | Measure-Object Size -Sum).Sum })
