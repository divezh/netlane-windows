param(
    [switch]$DownloadEngine,
    [string]$RoutedEngine,
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
$taskArchiveHash = '56fb620f4cd30abbc1b4586c6645b56d90c8c0e977ba53d95fb0f542acb7cb1f'
New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
if (!(Test-Path -LiteralPath $taskArchive)) {
    if (!$DownloadEngine) { throw 'Run build.ps1 -DownloadEngine to obtain the pinned official engine.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 120 -Uri 'https://github.com/TrustTunnel/TrustTunnelClient/releases/download/v1.1.12/trusttunnel_client-v1.1.12-windows-x86_64.zip' -OutFile $taskArchive
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
    $taskEngineHash = (Get-FileHash -LiteralPath (Join-Path $taskOutput 'engine/trusttunnel_client.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskGenerated = [IO.File]::ReadAllText($taskEngineSpec).Replace('SupportsApplications = false', 'SupportsApplications = true').Replace('SupportsApplicationNames = false', 'SupportsApplicationNames = true').Replace('Version = "1.1.12"', 'Version = "1.1.12-netlane.1"').Replace('2572f6397ec4a39e018858da593ab278facf9b2e86c7e276753f4845ed78968d', $taskEngineHash)
    $taskEngineSpec = Join-Path $taskCache 'EngineSpec.routed.cs'
    [IO.File]::WriteAllText($taskEngineSpec, $taskGenerated, [Text.UTF8Encoding]::new($false))
}
foreach ($taskName in @('LICENSE.txt', 'WINTUN_LICENSE.txt')) { Copy-Item -LiteralPath (Join-Path $taskCache $taskName) -Destination (Join-Path $taskOutput "licenses/$taskName") -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/FLAG_ICONS_LICENSE.txt') -Destination (Join-Path $taskOutput 'licenses/FLAG_ICONS_LICENSE.txt') -Force
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework 4.8 compiler is required.' }
$taskSources = @('DnsSettings.cs','SettingsDns.cs','Theme.cs','SessionStats.cs','PublicIp.cs','Controls.cs','MainControls.cs','HomeControls.cs','PulseControls.cs','ServerMenu.cs','TrayController.cs','SettingsForm.cs','SettingsRules.cs','SettingsPages.cs','SettingsDrop.cs','Autostart.cs','Flags.cs','RuleSetEditor.cs','CountryPicker.cs','CountryNames.cs','ImportForm.cs','Latency.cs','Routing.cs','Profile.cs','ServerName.cs','ClientState.cs','Subscription.cs','StateStore.cs','EngineSpec.cs','RouteException.cs','Connection.cs','DarkControls.cs','MainForm.cs','UpdateFeed.cs','Updater.cs','UpdateDialog.cs','Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$taskSources = @($taskSources | Where-Object { $_ -ne (Join-Path $PSScriptRoot 'EngineSpec.cs') }) + @($taskEngineSpec)
$taskReferences = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$taskResources = @("/resource:$(Join-Path $PSScriptRoot 'Netlane.ico'),Netlane.ico", "/resource:$(Join-Path $taskRoot 'assets/icon.png'),Netlane.png") + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'flags') -Filter '*.png' | Sort-Object Name | ForEach-Object { "/resource:$($_.FullName),flag.$($_.Name)" })
# 500 flag resources exceed the Windows command-line limit: pass them to csc in a response file.
$taskResponse = Join-Path ([IO.Path]::GetTempPath()) ('netlane-resources-' + [Guid]::NewGuid().ToString('N') + '.rsp')
Set-Content -LiteralPath $taskResponse -Encoding UTF8 -Value ($taskResources | ForEach-Object { '"' + $_ + '"' })
& $taskCompiler /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:winexe "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'Netlane.ico')" "@$taskResponse" "/out:$(Join-Path $taskOutput 'Netlane.exe')" @taskReferences @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $taskOutput 'Netlane.exe.config') -Force
& $taskCompiler /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:exe /main:Netlane.Tests "@$taskResponse" "/out:$(Join-Path $taskOutput 'NetlaneTests.exe')" @taskReferences @taskSources (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if (-not $NoTest) {
    & (Join-Path $taskOutput 'NetlaneTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Client tests failed.' }
}
$taskZip = Join-Path $taskRoot 'artifacts/Netlane-windows-x64.zip'
$taskPack = @('Netlane.exe','Netlane.exe.config','engine','licenses') | ForEach-Object { Join-Path $taskOutput $_ }
Compress-Archive -Path $taskPack -DestinationPath $taskZip -Force
$taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskZip + '.sha256', $taskHash + '  Netlane-windows-x64.zip' + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ('Windows bundle: ' + $taskZip)
Write-Output ('SHA256: ' + $taskHash)
