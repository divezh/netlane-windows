<#
.SYNOPSIS
Signs the Netlane Windows binaries with Authenticode.

.DESCRIPTION
Windows 11 Smart App Control refuses to launch an executable it cannot attribute to a
publisher Windows trusts, and it has no per-app exception list and no "run anyway"
button. An unsigned Netlane.exe therefore never starts for such users. The only
supported remedy is an Authenticode signature from a trusted certificate; this script
produces it and verifies the result.

Only files without a valid signature are signed, so the pinned upstream engine (signed
by AdGuard Software Limited) and wintun.dll (signed by WireGuard LLC) keep their vendor
signatures. An engine we build ourselves for application routing is unsigned and does
get signed here, because Smart App Control checks the child process too.

Choose exactly one certificate source:
  -Thumbprint            a code-signing certificate already in the certificate store
  -PfxPath               a PKCS#12 file (-PfxPassword or NETLANE_SIGN_PFX_PASSWORD)
  -AzureTrustedSigning   Microsoft Trusted Signing, a cloud HSM; also needs
                         -TrustedSigningDlib, -TsaEndpoint, -CodeSigningAccount and
                         -CertificateProfile
  -TestSign              a throwaway self-signed certificate, deleted afterwards. Enough
                         to check that this tooling works, useless for users: Windows
                         never gets a chance to trust it. For a real self-signed release
                         use new-signing-cert.ps1 and ship the exported .cer.

Unattended builds may supply NETLANE_SIGN_THUMBPRINT, NETLANE_SIGN_PFX,
NETLANE_SIGN_PFX_PASSWORD, NETLANE_SIGN_TSA and NETLANE_SIGN_DLIB instead.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File clients\windows\sign.ps1 -Thumbprint 1A2B3C...

.EXAMPLE
powershell -ExecutionPolicy Bypass -File clients\windows\sign.ps1 -VerifyOnly -Path artifacts\windows\Netlane

.EXAMPLE
powershell -ExecutionPolicy Bypass -File clients\windows\sign.ps1 -AzureTrustedSigning `
    -TrustedSigningDlib C:\tools\Azure.CodeSigning.Dlib.dll -TsaEndpoint https://eus.codesigning.azure.net `
    -CodeSigningAccount netlane -CertificateProfile netlane-release
#>
param(
    [string]$Path,
    [switch]$VerifyOnly,
    [string]$Thumbprint,
    [switch]$MachineStore,
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$AzureTrustedSigning,
    [string]$TrustedSigningDlib,
    [string]$TsaEndpoint,
    [string]$CodeSigningAccount,
    [string]$CertificateProfile,
    [switch]$TestSign,
    [switch]$KeepTestCertificate,
    [string]$TimestampUrl,
    [string]$SignToolPath,
    [string]$Description = 'Netlane VPN client',
    [string]$InfoUrl = 'https://github.com/divezh/netlane-windows'
)
$ErrorActionPreference = 'Stop'

if (-not $Path) { $Path = Join-Path $PSScriptRoot '../../artifacts/windows/Netlane' }
$Path = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path)) { throw "Nothing to sign: $Path does not exist. Build the client first." }
if (-not $TimestampUrl) { $TimestampUrl = if ($env:NETLANE_SIGN_TSA) { $env:NETLANE_SIGN_TSA } else { 'http://timestamp.digicert.com' } }
if (-not $Thumbprint) { $Thumbprint = $env:NETLANE_SIGN_THUMBPRINT }
if (-not $PfxPath) { $PfxPath = $env:NETLANE_SIGN_PFX }
if (-not $PfxPassword) { $PfxPassword = $env:NETLANE_SIGN_PFX_PASSWORD }
if (-not $TrustedSigningDlib) { $TrustedSigningDlib = $env:NETLANE_SIGN_DLIB }

function Find-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path -LiteralPath $SignToolPath)) { throw "signtool.exe not found at $SignToolPath." }
        return [IO.Path]::GetFullPath($SignToolPath)
    }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    $found = foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
                Where-Object { Test-Path -LiteralPath $_ }
        }
    }
    $first = @($found)[0]
    if (-not $first) { throw 'signtool.exe was not found. Install the Windows SDK (Windows Kits\10\bin) or pass -SignToolPath.' }
    return $first
}

function Get-Targets {
    # Every executable we ship, except those a vendor already signed validly.
    $all = Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.exe', '.dll', '.msi' }
    $result = foreach ($file in $all) {
        $sig = Get-AuthenticodeSignature -LiteralPath $file.FullName
        [PSCustomObject]@{ File = $file; Status = $sig.Status; Signer = $sig.SignerCertificate }
    }
    return @($result)
}

$targets = Get-Targets
if ($targets.Count -eq 0) { throw "No .exe or .dll files under $Path." }

if ($VerifyOnly) {
    foreach ($item in $targets) {
        $relative = $item.File.FullName.Substring($Path.Length).TrimStart('\')
        $signer = if ($item.Signer) { $item.Signer.Subject } else { '-' }
        Write-Host ("  {0,-8} {1,-32} {2}" -f $item.Status, $relative, $signer)
    }
    $unsigned = @($targets | Where-Object { $_.Status -eq 'NotSigned' })
    if ($unsigned.Count -gt 0) {
        Write-Warning ("$($unsigned.Count) file(s) are unsigned: Smart App Control may block them.")
        exit 1
    }
    Write-Host 'Every shipped binary carries a valid Authenticode signature.'
    exit 0
}

$signTool = Find-SignTool
$certificateArgs = @()
$testCertificate = $null
$metadataFile = $null
$signingThumbprint = $null
$sourcesPicked = @($Thumbprint, $PfxPath, [bool]$AzureTrustedSigning, [bool]$TestSign) | Where-Object { $_ }
if ($sourcesPicked.Count -ne 1) { throw 'Pass exactly one certificate source: -Thumbprint, -PfxPath, -AzureTrustedSigning or -TestSign.' }

try {
    if ($TestSign) {
        Write-Warning 'Test signing with a throwaway certificate that is deleted afterwards. The signature is for testing only and may be blocked by Windows.'
        $testCertificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq 'CN=Netlane Throwaway Test Signing' -and $_.NotAfter -gt (Get-Date).AddDays(1) } |
            Select-Object -First 1
        if (-not $testCertificate) {
            $testCertificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Netlane Throwaway Test Signing' `
                -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
                -NotAfter (Get-Date).AddDays(7)
        }
        $certificateArgs = @('/sha1', $testCertificate.Thumbprint)
        $signingThumbprint = $testCertificate.Thumbprint
    } elseif ($Thumbprint) {
        $store = if ($MachineStore) { 'Cert:\LocalMachine\My' } else { 'Cert:\CurrentUser\My' }
        $certificate = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint }
        if (-not $certificate) { throw "No certificate $Thumbprint in $store. Install the code-signing certificate (with its private key) first." }
        $certificateArgs = @('/sha1', $Thumbprint)
        if ($MachineStore) { $certificateArgs += '/sm' }
        $signingThumbprint = $certificate.Thumbprint
    } elseif ($PfxPath) {
        $PfxPath = [IO.Path]::GetFullPath($PfxPath)
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX not found: $PfxPath" }
        $certificateArgs = @('/f', $PfxPath)
        if ($PfxPassword) { $certificateArgs += @('/p', $PfxPassword) }
    } else {
        if (-not $TrustedSigningDlib) { throw '-AzureTrustedSigning needs -TrustedSigningDlib (Azure.CodeSigning.Dlib.dll from Microsoft.Trusted.Signing.Client).' }
        foreach ($required in 'TsaEndpoint', 'CodeSigningAccount', 'CertificateProfile') {
            if (-not (Get-Variable -Name $required -ValueOnly)) { throw "-AzureTrustedSigning needs -$required." }
        }
        $TrustedSigningDlib = [IO.Path]::GetFullPath($TrustedSigningDlib)
        if (-not (Test-Path -LiteralPath $TrustedSigningDlib)) { throw "Trusted Signing library not found: $TrustedSigningDlib" }
        $metadataFile = Join-Path ([IO.Path]::GetTempPath()) ('netlane-trusted-signing-' + [Guid]::NewGuid().ToString('N') + '.json')
        $metadata = [ordered]@{
            Endpoint                = $TsaEndpoint
            CodeSigningAccountName  = $CodeSigningAccount
            CertificateProfileName  = $CertificateProfile
        } | ConvertTo-Json
        [IO.File]::WriteAllText($metadataFile, $metadata, [Text.UTF8Encoding]::new($false))
        $certificateArgs = @('/dlib', $TrustedSigningDlib, '/dmdf', $metadataFile)
    }

    $signed = 0
    $skipped = 0
    foreach ($item in $targets) {
        $file = $item.File
        $relative = $file.FullName.Substring($Path.Length).TrimStart('\')
        # Re-signing our own file would change its bytes, and the client pins the engine hash.
        $alreadyOurs = $signingThumbprint -ne $null -and $item.Signer -ne $null -and $item.Signer.Thumbprint -eq $signingThumbprint
        if ($item.Status -eq 'Valid' -or $alreadyOurs) {
            Write-Host ("  [skip]   {0}  (already signed by {1})" -f $relative, $item.Signer.Subject)
            $skipped++
            continue
        }
        Write-Host ("  [sign]   {0}" -f $relative)
        $arguments = @('sign', '/v', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', $Description, '/du', $InfoUrl) + $certificateArgs + @($file.FullName)
        & $signTool @arguments
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $relative with exit code $LASTEXITCODE." }
        $after = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($TestSign) {
            if ($after.Status -eq 'NotSigned') { throw "Signing $relative produced no signature." }
            Write-Host ("  [ok]     {0}  (test signature: {1})" -f $relative, $after.Status) -ForegroundColor Yellow
        } else {
            # A publisher signs on a machine that need not trust its own certificate, so a signature
            # made by the certificate we signed with counts even when the chain is untrusted here.
            $byUs = $signingThumbprint -ne $null -and $after.SignerCertificate -ne $null -and $after.SignerCertificate.Thumbprint -eq $signingThumbprint
            if ($after.Status -ne 'Valid' -and -not ($byUs -and $after.Status -eq 'UnknownError')) { throw "Signature of $relative is $($after.Status): $($after.StatusMessage)" }
            Write-Host ("  [ok]     {0}  ({1})" -f $relative, $after.SignerCertificate.Subject) -ForegroundColor Green
        }
        $signed++
    }
    Write-Host ''
    Write-Host ("Signed $signed, skipped $skipped already-signed file(s).")
    if ($TestSign) {
        Write-Warning 'This was a test signature. Re-run with a real certificate before publishing, and check on a machine with Smart App Control on.'
    }
} finally {
    if ($metadataFile -and (Test-Path -LiteralPath $metadataFile)) { Remove-Item -LiteralPath $metadataFile -Force -ErrorAction SilentlyContinue }
    if ($testCertificate -and -not $KeepTestCertificate) {
        Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $testCertificate.Thumbprint) -Force -ErrorAction SilentlyContinue
    }
}
