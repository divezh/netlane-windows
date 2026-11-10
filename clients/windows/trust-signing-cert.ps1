<#
.SYNOPSIS
Lets the current Windows user trust the Netlane code-signing certificate.

.DESCRIPTION
Run this once before starting a Netlane build that was signed with the project's
self-signed certificate. It adds the public certificate to this user's trusted root and
trusted publisher stores. This does not guarantee Smart App Control will allow the
application to start. No administrator rights are needed: the change stays inside the
current user profile.

The certificate defaults to Netlane-code-signing.cer next to this script, so running it
from the unpacked folder is enough.

Read this before you continue. A certificate in the trusted root store can sign any
program, not only Netlane. Only install a certificate that came from someone you trust,
and remove it if you stop using Netlane:

  .\trust-signing-cert.ps1 -Remove

.EXAMPLE
powershell -ExecutionPolicy Bypass -File trust-signing-cert.ps1
#>
param(
    [string]$CerPath,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if (-not $CerPath) { $CerPath = Join-Path $PSScriptRoot 'Netlane-code-signing.cer' }
$CerPath = [IO.Path]::GetFullPath($CerPath)
if (-not (Test-Path -LiteralPath $CerPath)) { throw "Certificate not found: $CerPath" }
$certutil = Join-Path $env:WINDIR 'System32/certutil.exe'
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPath)
Write-Host ("Certificate: {0}" -f $certificate.Subject)
Write-Host ("Thumbprint:  {0}" -f $certificate.Thumbprint)
Write-Host ("Expires:     {0:yyyy-MM-dd}" -f $certificate.NotAfter)

if (-not $Remove) {
    Write-Warning 'Installing a root certificate grants broad trust and does not guarantee Smart App Control compatibility. Use only on controlled test machines.'
    if ((Read-Host 'Type TRUST to continue') -cne 'TRUST') { throw 'Cancelled; certificate stores were not changed.' }
}

foreach ($store in 'Root', 'TrustedPublisher') {
    if ($Remove) {
        & $certutil -user -delstore $store $certificate.Thumbprint 2>&1 | Out-Null
        $action = if ($LASTEXITCODE -eq 0) { 'removed from' } else { 'was not present in' }
    } else {
        & $certutil -user -addstore -f $store $CerPath 2>&1 | Out-Null
        $action = if ($LASTEXITCODE -eq 0) { 'added to' } else { 'FAILED for' }
    }
    Write-Host ("  {0} {1}" -f $action, $store)
    if ($LASTEXITCODE -ne 0 -and -not $Remove) { throw "Could not add the certificate to $store." }
}

Write-Host ''
if ($Remove) {
    Write-Host 'Done. This user no longer trusts programs signed with this certificate.'
} else {
    Write-Host 'Done. The certificate was added; Windows may still block the application.'
    Write-Host 'If a download still shows "Windows protected your PC", use "More info" -> "Run anyway": that prompt is SmartScreen, a different check from Smart App Control.'
}
