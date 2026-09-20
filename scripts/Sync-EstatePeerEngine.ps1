param([string]$ServerRoot = (Join-Path $PSScriptRoot '../../LazyForza.RaceServer'))
$ErrorActionPreference = 'Stop'
$clientRoot = Split-Path $PSScriptRoot -Parent
$serverPath = (Resolve-Path -LiteralPath $ServerRoot).Path
$revision = (& git -c "safe.directory=$($serverPath.Replace('\', '/'))" -C $serverPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read RaceServer revision.' }
if (& git -c "safe.directory=$($serverPath.Replace('\', '/'))" -C $serverPath status --porcelain) {
    throw 'Commit RaceServer changes before generating a pinned engine.'
}
$version = '0.1.0-peer.' + $revision.Substring(0, 12)
$stage = Join-Path $clientRoot ('artifacts/estate-peer-engine/' + $revision)
$feed = Join-Path $clientRoot 'vendor/estate-peer-engine'
New-Item -ItemType Directory -Force -Path $stage, $feed | Out-Null
$hashes = [ordered]@{}
foreach ($part in @('Protocol', 'Core')) {
    $name = 'LazyForza.RaceServer.' + $part
    $directory = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $serverPath "src/$name") -Filter '*.cs') {
        Copy-Item -LiteralPath $source.FullName -Destination $directory
        $hashes["src/$name/$($source.Name)"] = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
    }
    $reference = if ($part -eq 'Core') { '<ItemGroup><ProjectReference Include="../LazyForza.RaceServer.Protocol/LazyForza.RaceServer.Protocol.csproj" /></ItemGroup>' } else { '' }
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Version>$version</Version>
    <RepositoryCommit>$revision</RepositoryCommit>
    <Description>Unmodified LazyForza RaceServer $part, pinned for offline peer hosting.</Description>
    <IncludeSymbols>false</IncludeSymbols>
  </PropertyGroup>
  $reference
  <ItemGroup><None Include="*.cs" Pack="true" PackagePath="source/" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $directory "$name.csproj") -Encoding utf8
    & dotnet pack (Join-Path $directory "$name.csproj") -c Release -o $feed --configfile (Join-Path $clientRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "Packing $name failed." }
}
$packages = [ordered]@{}
foreach ($package in Get-ChildItem -LiteralPath $feed -Filter "*.$version.nupkg") {
    $packages[$package.Name] = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
}
[ordered]@{ version = $version; revision = $revision; sources = $hashes; packages = $packages } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $feed 'provenance.json') -Encoding utf8
Write-Output "Pinned engine: $version. Update Directory.Packages.props when changing the revision."
