param([string]$ServerRoot = (Join-Path $PSScriptRoot '../../LazyForza.RaceServer'))
$ErrorActionPreference = 'Stop'
$clientRoot = Split-Path $PSScriptRoot -Parent
$serverPath = (Resolve-Path -LiteralPath $ServerRoot).Path
$revision = (& git -c "safe.directory=$($serverPath.Replace('\', '/'))" -C $serverPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read RaceServer revision.' }
if (& git -c "safe.directory=$($serverPath.Replace('\', '/'))" -C $serverPath status --porcelain) { throw 'RaceServer must be clean.' }
$version = '0.1.0-peer.' + $revision.Substring(0, 12)
$stage = Join-Path $clientRoot "artifacts/estate-peer-control/$revision"
$feed = Join-Path $clientRoot 'vendor/estate-peer-engine'
New-Item -ItemType Directory -Force -Path $stage, $feed | Out-Null
$webRoot = Join-Path $serverPath 'src/LazyForza.RaceServer.Web'
$hashes = [ordered]@{}
foreach ($source in Get-ChildItem -LiteralPath $webRoot -Filter '*.cs') {
    $hashes[$source.Name] = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
    if ($source.Name -ne 'Program.cs') { Copy-Item -LiteralPath $source.FullName -Destination $stage }
}
# Extract the existing endpoint/middleware block verbatim. Startup, persistence and listeners are supplied by the host.
$program = Get-Content -LiteralPath (Join-Path $webRoot 'Program.cs') -Raw
$start = $program.IndexOf('app.Use(async (context, next) =>', [StringComparison]::Ordinal)
$finish = $program.IndexOf('app.Run();', [StringComparison]::Ordinal)
$helpers = $program.IndexOf('static bool Authorized(', [StringComparison]::Ordinal)
$end = $program.IndexOf('public partial class Program;', [StringComparison]::Ordinal)
if ($start -lt 0 -or $finish -lt $start -or $helpers -lt $finish -or $end -lt $helpers) { throw 'Native Web startup layout changed; review the adapter.' }
$prefix = $program.Substring(0, $program.IndexOf('var initializationRequested', [StringComparison]::Ordinal))
@"
#nullable enable
$prefix
namespace LazyForza.RaceServer.Web;
public static class PeerControlApplication
{
    public static void Map(WebApplication app, RaceServerOptions serverOptions)
    {
$($program.Substring($start, $finish - $start))
    }
$($program.Substring($helpers, $end - $helpers))
}
"@ | Set-Content -LiteralPath (Join-Path $stage 'PeerControlApplication.g.cs') -Encoding utf8
$assets = @()
foreach ($asset in Get-ChildItem -LiteralPath (Join-Path $webRoot 'wwwroot') -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath((Join-Path $webRoot 'wwwroot'), $asset.FullName).Replace('\', '/')
    $target = Join-Path $stage "wwwroot/$relative"
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $asset.FullName -Destination $target
    $hashes["wwwroot/$relative"] = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash
    $assets += "<EmbeddedResource Include=`"wwwroot/$relative`" LogicalName=`"PeerControl/$relative`" />"
}
@"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Library</OutputType>
    <Version>$version</Version><RepositoryCommit>$revision</RepositoryCommit><IsPackable>true</IsPackable>
    <EnableDefaultContentItems>false</EnableDefaultContentItems>
    <Description>Native RaceServer Web routes and assets from the pinned source revision.</Description>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="LazyForza.RaceServer.Core" Version="$version" /></ItemGroup>
  <ItemGroup>$($assets -join "`n")</ItemGroup>
  <ItemGroup><None Include="*.cs" Pack="true" PackagePath="source/" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $stage 'LazyForza.RaceServer.Control.csproj') -Encoding utf8
& dotnet pack (Join-Path $stage 'LazyForza.RaceServer.Control.csproj') -c Release -o $feed --configfile (Join-Path $clientRoot 'NuGet.Config') -p:ManagePackageVersionsCentrally=false
if ($LASTEXITCODE -ne 0) { throw 'Packing native Web control failed.' }
[ordered]@{ version = $version; revision = $revision; sources = $hashes; adapter = 'Verbatim middleware/routes/helpers wrapped in PeerControlApplication.Map; startup supplied by peer host.' } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $feed 'control-provenance.json') -Encoding utf8
