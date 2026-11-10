param(
    [switch]$DownloadEngine,
    [string]$RoutedEngine,
    # Windows 11 Smart App Control may block unknown or unsigned executables.
    # Use a trusted code-signing certificate for distribution. Signing runs before
    # the bundled tests, because an unsigned NetlaneTests.exe is blocked there as well.
    # See clients/windows/sign.ps1 and docs/SIGNING.md.
    [switch]$Sign,
    [switch]$TestSign,
    [string]$SignThumbprint,
    [string]$SignPfx,
    [string]$SignPfxPassword,
    [switch]$NoTest
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# Rules for applications and bare process names live in our patched engine. When the local build
# of that engine exists, use it by default: an official engine silently ignores application rules.
$taskRoutedDefault = Join-Path $taskRoot 'research/windows-client/cmake-build-msvc-relwithdebinfo/trusttunnel/trusttunnel_client.exe'
if (-not $RoutedEngine -and (Test-Path -LiteralPath $taskRoutedDefault)) { $RoutedEngine = $taskRoutedDefault }
$taskCache = Join-Path $taskRoot '.tools/windows-engine'
$taskArchive = Join-Path $taskCache 'engine.zip'
$taskArchiveHash = 'c6db992c1164abef1a19a8289173be23d87c6f8c039360dbe9b7fdef89a9e526'
New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
if (!(Test-Path -LiteralPath $taskArchive)) {
    if (!$DownloadEngine) { throw 'Run build.ps1 -DownloadEngine to obtain the pinned official engine.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 120 -Uri 'https://github.com/TrustTunnel/TrustTunnelClient/releases/download/v1.1.11/trusttunnel_client-v1.1.11-windows-x86_64.zip' -OutFile $taskArchive
}
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskArchiveHash) { throw 'Official engine archive checksum mismatch.' }
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskCache -Force
$taskOutput = Join-Path $taskRoot 'artifacts/windows/Netlane'
New-Item -ItemType Directory -Force -Path (Join-Path $taskOutput 'engine'), (Join-Path $taskOutput 'licenses') | Out-Null
foreach ($taskName in @('trusttunnel_client.exe', 'wintun.dll')) { Copy-Item -LiteralPath (Join-Path $taskCache $taskName) -Destination (Join-Path $taskOutput "engine/$taskName") -Force }
$taskEngineSpec = Join-Path $PSScriptRoot 'EngineSpec.cs'
if ($RoutedEngine) {
    $taskExpected = Join-Path $taskRoot 'research/windows-client/cmake-build-msvc-relwithdebinfo/trusttunnel/trusttunnel_client.exe'
    if ([IO.Path]::GetFullPath($RoutedEngine) -ne [IO.Path]::GetFullPath($taskExpected) -or !(Test-Path -LiteralPath $taskExpected)) { throw 'Use the engine built from our pinned source and routing patch.' }
    Copy-Item -LiteralPath $taskExpected -Destination (Join-Path $taskOutput 'engine/trusttunnel_client.exe') -Force
    # Sign the engine before its hash is measured. The client verifies the engine byte for byte, so a
    # signature added after EngineSpec is generated would invalidate the pinned hash. The official
    # engine is already signed by its publisher and only our own build ever reaches this branch.
    if ($Sign -or $TestSign) {
        $taskEngineSignArguments = @{ Path = (Join-Path $taskOutput 'engine') }
        if ($TestSign) { $taskEngineSignArguments['TestSign'] = $true }
        if ($SignThumbprint) { $taskEngineSignArguments['Thumbprint'] = $SignThumbprint }
        if ($SignPfx) { $taskEngineSignArguments['PfxPath'] = $SignPfx }
        if ($SignPfxPassword) { $taskEngineSignArguments['PfxPassword'] = $SignPfxPassword }
        & (Join-Path $PSScriptRoot 'sign.ps1') @taskEngineSignArguments
    }
    $taskEngineHash = (Get-FileHash -LiteralPath (Join-Path $taskOutput 'engine/trusttunnel_client.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskGenerated = [IO.File]::ReadAllText($taskEngineSpec).Replace('SupportsApplications = false', 'SupportsApplications = true').Replace('SupportsApplicationNames = false', 'SupportsApplicationNames = true').Replace('Version = "1.1.11"', 'Version = "1.1.11-netlane.1"').Replace('2572f6397ec4a39e018858da593ab278facf9b2e86c7e276753f4845ed78968d', $taskEngineHash)
    $taskEngineSpec = Join-Path $taskCache 'EngineSpec.routed.cs'
    [IO.File]::WriteAllText($taskEngineSpec, $taskGenerated, [Text.UTF8Encoding]::new($false))
}
foreach ($taskName in @('LICENSE.txt', 'WINTUN_LICENSE.txt')) { Copy-Item -LiteralPath (Join-Path $taskCache $taskName) -Destination (Join-Path $taskOutput "licenses/$taskName") -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/FLAG_ICONS_LICENSE.txt') -Destination (Join-Path $taskOutput 'licenses/FLAG_ICONS_LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/ONEST_LICENSE.txt') -Destination (Join-Path $taskOutput 'licenses/ONEST_LICENSE.txt') -Force
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework 4.8 compiler is required.' }
$taskSources = @('DnsSettings.cs','SettingsDns.cs','Theme.cs','AppFonts.cs','SessionStats.cs','Controls.cs','MainControls.cs','HomeControls.cs','PulseControls.cs','ServerMenu.cs','TrayController.cs','SettingsForm.cs','SettingsRules.cs','SettingsPages.cs','SettingsDrop.cs','Autostart.cs','Flags.cs','RuleSetEditor.cs','CountryPicker.cs','CountryNames.cs','ImportForm.cs','Latency.cs','Routing.cs','Profile.cs','ServerName.cs','ClientState.cs','Subscription.cs','StateStore.cs','EngineSpec.cs','RouteException.cs','Connection.cs','DarkControls.cs','MainForm.cs','UpdateFeed.cs','Updater.cs','UpdateDialog.cs','Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$taskSources = @($taskSources | Where-Object { $_ -ne (Join-Path $PSScriptRoot 'EngineSpec.cs') }) + @($taskEngineSpec)
$taskReferences = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$taskResources = @("/resource:$(Join-Path $PSScriptRoot 'Netlane.ico'),Netlane.ico", "/resource:$(Join-Path $taskRoot 'assets/icon.png'),Netlane.png") + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'fonts') -Filter '*.ttf' | Sort-Object Name | ForEach-Object { "/resource:$($_.FullName),font.$($_.Name)" }) + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'flags') -Filter '*.png' | Sort-Object Name | ForEach-Object { "/resource:$($_.FullName),flag.$($_.Name)" })
# 500 flag resources exceed the Windows command-line limit: pass them to csc in a response file.
$taskResponse = Join-Path ([IO.Path]::GetTempPath()) ('netlane-resources-' + [Guid]::NewGuid().ToString('N') + '.rsp')
Set-Content -LiteralPath $taskResponse -Encoding UTF8 -Value ($taskResources | ForEach-Object { '"' + $_ + '"' })
& $taskCompiler /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:winexe "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'Netlane.ico')" "@$taskResponse" "/out:$(Join-Path $taskOutput 'Netlane.exe')" @taskReferences @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $taskOutput 'Netlane.exe.config') -Force
& $taskCompiler /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:exe /main:Netlane.Tests "@$taskResponse" "/out:$(Join-Path $taskOutput 'NetlaneTests.exe')" @taskReferences @taskSources (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if ($Sign -or $TestSign) {
    $taskSignArguments = @{ Path = $taskOutput }
    if ($TestSign) { $taskSignArguments['TestSign'] = $true }
    if ($SignThumbprint) { $taskSignArguments['Thumbprint'] = $SignThumbprint }
    if ($SignPfx) { $taskSignArguments['PfxPath'] = $SignPfx }
    if ($SignPfxPassword) { $taskSignArguments['PfxPassword'] = $SignPfxPassword }
    & (Join-Path $PSScriptRoot 'sign.ps1') @taskSignArguments
}
if (-not $NoTest) {
    & (Join-Path $taskOutput 'NetlaneTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Client tests failed.' }
}
$taskSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $taskOutput 'Netlane.exe')
# The machine that signs need not trust its own certificate: a signature made with the certificate we
# signed with is enough here, and every user gets trust by installing the exported .cer.
$taskSignedByUs = $SignThumbprint -and $taskSignature.SignerCertificate -and $taskSignature.SignerCertificate.Thumbprint -eq $SignThumbprint
if ($taskSignature.Status -ne 'Valid' -and -not $taskSignedByUs) {
    Write-Warning 'Netlane.exe has no valid Authenticode signature. Smart App Control may block it. Rebuild with -Sign and a code-signing certificate; see docs/SIGNING.md.'
}
$taskZip = Join-Path $taskRoot 'artifacts/Netlane-windows-x64.zip'
$taskPack = @('Netlane.exe','Netlane.exe.config','engine','licenses') | ForEach-Object { Join-Path $taskOutput $_ }
Compress-Archive -Path $taskPack -DestinationPath $taskZip -Force
$taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskZip + '.sha256', $taskHash + '  Netlane-windows-x64.zip' + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ('Windows bundle: ' + $taskZip)
Write-Output ('SHA256: ' + $taskHash)
Write-Output ('Publisher: ' + $(if ($taskSignature.SignerCertificate) { $taskSignature.SignerCertificate.Subject } else { 'unsigned (may be blocked by Smart App Control)' }))
