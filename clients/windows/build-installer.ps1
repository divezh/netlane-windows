param(
    [string]$SourceDirectory,
    [string]$IsccPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $taskRoot 'artifacts/windows/Netlane' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'artifacts' }
$taskSource = (Resolve-Path -LiteralPath $SourceDirectory).Path
$taskRequired = @('Netlane.exe', 'Netlane.exe.config', 'engine/trusttunnel_client.exe', 'engine/wintun.dll', 'licenses/LICENSE.txt', 'licenses/WINTUN_LICENSE.txt', 'licenses/FLAG_ICONS_LICENSE.txt')
foreach ($taskFile in $taskRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskFile) -PathType Leaf)) { throw "Missing installer payload: $taskFile" }
}
$taskVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskSource 'Netlane.exe')).FileVersion
if ($taskVersion -notmatch '^(\d+\.\d+\.\d+)(?:\.0)?$') { throw "Unsupported client version: $taskVersion" }
$taskVersion = $Matches[1]
if (-not $IsccPath) {
    $taskCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($taskCommand) { $IsccPath = $taskCommand.Source }
    else {
        foreach ($taskCandidate in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe')
        )) {
            if (Test-Path -LiteralPath $taskCandidate -PathType Leaf) { $IsccPath = $taskCandidate; break }
        }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Install Inno Setup 6.7+ from https://jrsoftware.org/isdl.php or provide -IsccPath.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
& $IsccPath "/DPayloadDir=$taskSource" "/DSetupVersion=$taskVersion" "/DSetupOutputDir=$taskOutput" (Join-Path $PSScriptRoot 'installer/Netlane.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$taskInstaller = Join-Path $taskOutput "Netlane-Setup-$taskVersion-x64.exe"
if (-not (Test-Path -LiteralPath $taskInstaller -PathType Leaf)) { throw 'Compiler did not produce the installer.' }
$taskHash = (Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskInstaller + '.sha256', $taskHash + '  ' + [IO.Path]::GetFileName($taskInstaller) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Windows installer: $taskInstaller"
Write-Output "SHA256: $taskHash"
Write-Warning 'The setup executable is not Authenticode-signed. Sign it before public distribution; a checksum is not a publisher signature.'
