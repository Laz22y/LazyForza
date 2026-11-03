[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\LazyForza.Storage\Assets\Fh6VehicleNames.json.gz'),
    [string]$FallbackPath,
    [switch]$AllowCached
)

$ErrorActionPreference = 'Stop'
$gistId = '0659d1717bc61504bf83750628963f4f'
$sourceUrl = "https://gist.github.com/HDR/$gistId"
$apiUrl = "https://api.github.com/gists/$gistId"
$maximumBytes = 4 * 1024 * 1024
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'LazyForza-catalog-updater'; 'X-GitHub-Api-Version' = '2022-11-28' }

function Assert-Catalog {
    param($Document)
    try { $updated = [DateTimeOffset]$Document.updatedAtUtc } catch { throw 'Invalid vehicle catalog date.' }
    if ($Document.source -ne $sourceUrl -or $Document.author -ne 'HDR' -or
        $Document.revision -notmatch '^[0-9a-fA-F]{40}$' -or
        $updated.Year -le 1970 -or
        $null -eq $Document.cars -or $Document.cars -isnot [pscustomobject]) { throw 'Invalid vehicle catalog metadata.' }
    $count = 0
    foreach ($pair in $Document.cars.PSObject.Properties) {
        $ordinal = 0
        if (-not [int]::TryParse($pair.Name, [ref]$ordinal) -or $ordinal -le 0 -or
            $pair.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($pair.Value) -or $pair.Value.Length -gt 300 -or
            $pair.Value -match '[\p{Cc}\u202a-\u202e\u2066-\u2069]') { throw 'Invalid vehicle catalog entry.' }
        $count++
    }
    if ($count -lt 100 -or $count -gt 100000) { throw "Invalid vehicle count: $count" }
}

function Read-Catalog {
    param([string]$Path)
    $file = [IO.File]::OpenRead($Path)
    try {
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
        try {
            $buffer = [IO.MemoryStream]::new()
            try {
                $chunk = New-Object byte[] 16384
                while (($read = $gzip.Read($chunk, 0, $chunk.Length)) -gt 0) {
                    if ($buffer.Length + $read -gt $maximumBytes) { throw 'Vehicle catalog exceeds 4 MB.' }
                    $buffer.Write($chunk, 0, $read)
                }
                $document = [Text.UTF8Encoding]::new($false, $true).GetString($buffer.ToArray()) | ConvertFrom-Json
                Assert-Catalog $document
                $document.updatedAtUtc = ([DateTimeOffset]$document.updatedAtUtc).ToUniversalTime().ToString('O')
                return $document
            } finally { $buffer.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $file.Dispose() }
}

function Write-Catalog {
    param($Document, [string]$Path)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Document | ConvertTo-Json -Depth 4))
    if ($bytes.Length -gt $maximumBytes) { throw 'Vehicle catalog exceeds 4 MB.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $file = [IO.File]::Create($temporary)
        try {
            $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
            try { $gzip.Write($bytes, 0, $bytes.Length) } finally { $gzip.Dispose() }
        } finally { $file.Dispose() }
        $null = Read-Catalog $temporary
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if (-not $FallbackPath) { $FallbackPath = $resolvedOutput }
$retained = $null
foreach ($path in @($FallbackPath, $resolvedOutput) | Select-Object -Unique) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try {
            $candidate = Read-Catalog $path
            if ($null -eq $retained -or [DateTimeOffset]$candidate.updatedAtUtc -gt [DateTimeOffset]$retained.updatedAtUtc) { $retained = $candidate }
        } catch { Write-Warning "Cannot use vehicle catalog at ${path}: $($_.Exception.Message)" }
    }
}

$status = 'Refreshed'
try {
    # Pin the commit before downloading, so a concurrent upstream update cannot mix revisions.
    $commits = @(Invoke-RestMethod -Uri "$apiUrl/commits?per_page=1" -Headers $headers -TimeoutSec 20)
    $revision = [string]$commits[0].version
    if ($revision -notmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid GitHub revision.' }
    $updated = [DateTimeOffset]$commits[0].committed_at
    if ($updated.Year -le 1970) { throw 'Invalid GitHub revision date.' }
    if ($null -ne $retained -and ($retained.revision -eq $revision -or [DateTimeOffset]$retained.updatedAtUtc -ge $updated)) {
        $catalog = $retained; $status = 'Unchanged'
        if ($retained.revision -eq $revision) { $catalog.updatedAtUtc = $updated.ToUniversalTime().ToString('O') }
    } else {
        $gist = Invoke-RestMethod -Uri "$apiUrl/$revision" -Headers $headers -TimeoutSec 30
        $sourceFile = $gist.files.'Forza Horizon 6 Car Ordinals.json'
        if ($gist.id -ne $gistId -or $gist.history[0].version -ne $revision -or
            $null -eq $sourceFile -or $sourceFile.truncated -ne $false -or
            [string]::IsNullOrWhiteSpace($sourceFile.content) -or [Text.Encoding]::UTF8.GetByteCount($sourceFile.content) -gt $maximumBytes) {
            throw 'GitHub returned an incomplete or different vehicle name revision.'
        }
        $cars = @{}
        $sourceMap = $sourceFile.content | ConvertFrom-Json
        if ($sourceMap -isnot [pscustomobject]) { throw 'Vehicle map must be a JSON object.' }
        foreach ($property in $sourceMap.PSObject.Properties) {
            $ordinal = 0
            if (-not [int]::TryParse([string]$property.Value, [ref]$ordinal) -or $ordinal -le 0 -or $cars.ContainsKey($ordinal)) {
                throw 'Invalid or duplicate vehicle ordinal.'
            }
            $cars[$ordinal] = $property.Name.Trim()
        }
        $ordered = [ordered]@{}
        foreach ($ordinal in $cars.Keys | Sort-Object) { $ordered[[string]$ordinal] = $cars[$ordinal] }
        $catalog = [pscustomobject]@{
            source = $sourceUrl; author = 'HDR'; revision = $revision
            updatedAtUtc = $updated.ToUniversalTime().ToString('O')
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); cars = [pscustomobject]$ordered
        }
        Assert-Catalog $catalog
    }
} catch {
    if (-not $AllowCached -or $null -eq $retained) { throw }
    $catalog = $retained; $status = 'CachedFallback'
    Write-Warning "Vehicle catalog refresh failed: $($_.Exception.Message). Using verified revision $($catalog.revision), updated $($catalog.updatedAtUtc)."
}

Write-Catalog $catalog $resolvedOutput
[pscustomobject]@{
    Path = $resolvedOutput; Status = $status; Revision = $catalog.revision
    UpdatedAtUtc = $catalog.updatedAtUtc; VehicleCount = @($catalog.cars.PSObject.Properties).Count
    Sha256 = (Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256).Hash
}
