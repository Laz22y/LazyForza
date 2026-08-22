[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,

    [string]$CertificateThumbprint = $env:LAZYFORZA_SIGN_CERT_THUMBPRINT,

    [uri]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'Certificate thumbprint is required. Set LAZYFORZA_SIGN_CERT_THUMBPRINT or pass -CertificateThumbprint.'
}

$thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
$certificate = Get-ChildItem -LiteralPath Cert:\CurrentUser\My |
    Where-Object Thumbprint -eq $thumbprint |
    Select-Object -First 1
if ($null -eq $certificate) {
    throw "Code-signing certificate $thumbprint was not found in Cert:\CurrentUser\My."
}
if (-not $certificate.HasPrivateKey) {
    throw "Code-signing certificate $thumbprint does not have an accessible private key."
}

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $signTool) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signTool = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' |
        Sort-Object FullName -Descending |
        Select-Object -First 1
}
if ($null -eq $signTool) {
    throw 'signtool.exe was not found. Install the Windows SDK signing tools.'
}
$signToolPath = if ($signTool.PSObject.Properties.Name -contains 'Source') {
    $signTool.Source
} else {
    $signTool.FullName
}

foreach ($item in $Path) {
    $fullPath = [System.IO.Path]::GetFullPath($item)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Signing target does not exist: $fullPath"
    }
    & $signToolPath sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $fullPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed to sign $fullPath with exit code $LASTEXITCODE."
    }
    & $signToolPath verify /pa /all $fullPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verification failed for $fullPath with exit code $LASTEXITCODE."
    }
}
