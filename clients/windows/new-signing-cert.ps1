<#
.SYNOPSIS
Creates the self-signed code-signing certificate used to sign the Netlane Windows client.

.DESCRIPTION
Smart App Control blocks a binary it cannot attribute to a publisher Windows trusts.
A self-signed certificate is for controlled development tests and does not guarantee
acceptance by Smart App Control. Use a publicly trusted certificate for distribution.

This script creates the certificate once, keeps the private key in your certificate
store, and exports only the public .cer for distribution. Sign with it through
  clients\windows\build.ps1 -DownloadEngine -Sign -SignThumbprint <thumbprint>
  clients\windows\sign.ps1 -Thumbprint <thumbprint>

Treat the private key as a secret: whoever holds it can sign programs that every user
who installed the .cer will accept. Keep it out of the repository and out of backups
that other people can read.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File clients\windows\new-signing-cert.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File clients\windows\new-signing-cert.ps1 -Years 5 -PfxOut C:\keys\netlane.pfx
#>
param(
    [string]$Subject = 'CN=Netlane Self-Signed Code Signing',
    [int]$Years = 3,
    [string]$CerPath,
    [string]$PfxOut,
    [string]$PfxPassword,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $CerPath) { $CerPath = Join-Path $root 'artifacts/Netlane-code-signing.cer' }
$CerPath = [IO.Path]::GetFullPath($CerPath)

$existing = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) })
if ($existing.Count -gt 0 -and -not $Force) {
    $certificate = $existing[0]
    Write-Host ("Reusing the existing certificate {0} (valid until {1:yyyy-MM-dd})." -f $certificate.Thumbprint, $certificate.NotAfter)
    Write-Host 'Pass -Force to create a new one; existing builds would then need re-signing.'
} else {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears($Years)
    Write-Host ("Created certificate {0}, valid until {1:yyyy-MM-dd}." -f $certificate.Thumbprint, $certificate.NotAfter)
}

New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($CerPath)) | Out-Null
Export-Certificate -Cert $certificate -FilePath $CerPath -Type CERT | Out-Null
Write-Host ("Public certificate for users: {0}" -f $CerPath)

if ($PfxOut) {
    # Only for signing on another machine or in CI. Keep this file secret.
    $PfxOut = [IO.Path]::GetFullPath($PfxOut)
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($PfxOut)) | Out-Null
    if ($PfxPassword) {
        Export-PfxCertificate -Cert $certificate -FilePath $PfxOut -Password (ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force) | Out-Null
    } else {
        Export-PfxCertificate -Cert $certificate -FilePath $PfxOut | Out-Null
    }
    Write-Host ("Private key exported to {0} - keep it secret." -f $PfxOut) -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Next steps:'
Write-Host ("  1. Sign a build:  clients\windows\build.ps1 -DownloadEngine -Sign -SignThumbprint {0}" -f $certificate.Thumbprint)
Write-Host '  2. Keep test certificates within a controlled test environment.'
Write-Host  '  3. Self-signed certificates are for controlled testing; Smart App Control may still block the app.'
