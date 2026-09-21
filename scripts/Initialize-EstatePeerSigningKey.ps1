#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$KeyName = 'LazyForza.ComponentSigning.v1',
    [string]$PublicKeyPath = (Join-Path $PSScriptRoot '../src/LazyForza.App/Assets/estate-peer-signing-public.pem')
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The component signing key uses the current Windows user key store.' }
$provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$exists = [Security.Cryptography.CngKey]::Exists($KeyName, $provider)
if (-not $exists -and (Test-Path -LiteralPath $PublicKeyPath)) {
    throw 'The public key already exists but its signing key is unavailable. Do not silently replace the trust root.'
}
if ($exists) { $key = [Security.Cryptography.CngKey]::Open($KeyName, $provider) }
else {
    $parameters = [Security.Cryptography.CngKeyCreationParameters]::new()
    $parameters.Provider = $provider
    $parameters.KeyUsage = [Security.Cryptography.CngKeyUsages]::Signing
    $parameters.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $KeyName, $parameters)
}
try {
    $signer = [Security.Cryptography.ECDsaCng]::new($key)
    try {
        $publicKey = $signer.ExportSubjectPublicKeyInfoPem()
        if ((Test-Path -LiteralPath $PublicKeyPath) -and (Get-Content -LiteralPath $PublicKeyPath -Raw).Trim() -ne $publicKey.Trim()) {
            throw 'The existing public key differs from the Windows signing key.'
        }
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($PublicKeyPath), $publicKey + "`n")
        Write-Output "Public key: $([IO.Path]::GetFullPath($PublicKeyPath))"
        Write-Output "Signing key stays in the current Windows user key store: $KeyName"
    }
    finally { $signer.Dispose() }
}
finally { $key.Dispose() }
