[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\LazyForza.Storage\Assets\Fh6VehicleNames.json.gz')
)

$ErrorActionPreference = 'Stop'
$gistId = '0659d1717bc61504bf83750628963f4f'
$sourceUrl = "https://gist.github.com/HDR/$gistId"
$apiUrl = "https://api.github.com/gists/$gistId"
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'LazyForza-catalog-updater'
    'X-GitHub-Api-Version' = '2022-11-28'
}

$gist = Invoke-RestMethod -Uri $apiUrl -Headers $headers
$sourceFile = $gist.files.PSObject.Properties.Value |
    Where-Object { $_.filename -eq 'Forza Horizon 6 Car Ordinals.json' } |
    Select-Object -First 1
if ($null -eq $sourceFile -or [string]::IsNullOrWhiteSpace($sourceFile.content)) {
    throw 'The expected FH6 car ordinal JSON file was not found in the community gist.'
}
if ($sourceFile.truncated) {
    throw 'The GitHub Gist API returned truncated content; refusing to build an incomplete catalog.'
}

$sourceMap = $sourceFile.content | ConvertFrom-Json
$cars = @{}
foreach ($property in $sourceMap.PSObject.Properties) {
    $ordinal = 0
    if (-not [int]::TryParse([string]$property.Value, [ref]$ordinal) -or $ordinal -le 0) {
        continue
    }

    $name = ([string]$property.Name).Trim()
    if ([string]::IsNullOrWhiteSpace($name) -or
        $name.IndexOfAny([char[]]@(0x202A, 0x202B, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069)) -ge 0) {
        throw "Unsafe or empty vehicle name for ordinal $ordinal."
    }
    if ($cars.ContainsKey($ordinal) -and $cars[$ordinal] -ne $name) {
        throw "Duplicate ordinal $ordinal maps to both '$($cars[$ordinal])' and '$name'."
    }

    $cars[$ordinal] = $name
}
if ($cars.Count -lt 100) {
    throw "Only $($cars.Count) vehicle names were parsed; refusing to replace the embedded catalog."
}

$orderedCars = [ordered]@{}
foreach ($ordinal in ($cars.Keys | Sort-Object)) {
    $orderedCars[[string]$ordinal] = $cars[$ordinal]
}
$document = [ordered]@{
    source = $sourceUrl
    author = 'HDR'
    revision = [string]$gist.history[0].version
    updatedAtUtc = ([DateTimeOffset]$gist.updated_at).ToUniversalTime().ToString('O')
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    cars = $orderedCars
}
$json = $document | ConvertTo-Json -Depth 4

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $resolvedOutput
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$temporary = "$resolvedOutput.tmp"
$file = [System.IO.File]::Create($temporary)
try {
    $gzip = [System.IO.Compression.GZipStream]::new(
        $file,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    try {
        $writer = [System.IO.StreamWriter]::new(
            $gzip,
            [System.Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($json)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $gzip.Dispose()
    }
}
finally {
    $file.Dispose()
}

if ([System.IO.File]::Exists($resolvedOutput)) {
    [System.IO.File]::Delete($resolvedOutput)
}
[System.IO.File]::Move($temporary, $resolvedOutput)
Write-Output "Wrote $($cars.Count) vehicle names to $resolvedOutput"
Write-Output "Source revision: $($document.revision)"
Write-Output "Source updated: $($document.updatedAtUtc)"
