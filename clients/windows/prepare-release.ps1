param(
    [Parameter(Mandatory = $true)][string]$SigningKey,
    [string]$ArtifactsDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $taskRoot 'artifacts' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'dist' }
$taskVersionMatch = Select-String -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Pattern 'internal const string Version = "(\d+\.\d+\.\d+)"'
if (-not $taskVersionMatch) { throw 'Cannot determine the client version.' }
$taskVersion = $taskVersionMatch.Matches[0].Groups[1].Value
$taskArtifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$taskKey = (Resolve-Path -LiteralPath $SigningKey).Path
$taskPublicKey = Join-Path $taskRoot 'netlane-release.pub'
$taskZipName = 'Netlane-windows-x64.zip'
$taskSetupName = "Netlane-Setup-$taskVersion-x64.exe"
$taskFiles = @($taskZipName, $taskSetupName)
foreach ($taskFile in $taskFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskArtifacts $taskFile) -PathType Leaf)) { throw "Missing release asset: $taskFile" }
}
$taskSetupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskArtifacts $taskSetupName)).FileVersion.Trim()
if ($taskSetupVersion -ne "$taskVersion.0") { throw "Setup version does not match $taskVersion`: $taskSetupVersion" }
$taskProbe = Join-Path ([IO.Path]::GetTempPath()) ('netlane-release-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskProbe | Out-Null
$taskProbeExe = Join-Path $taskProbe 'Netlane.exe'
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $taskArtifacts $taskZipName))
    try {
        $taskEntry = $taskArchive.GetEntry('Netlane.exe')
        if (-not $taskEntry) { throw 'Update ZIP has no root Netlane.exe.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, $taskProbeExe)
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo($taskProbeExe).FileVersion -ne "$taskVersion.0") { throw 'Update ZIP version does not match the release.' }
    } finally { $taskArchive.Dispose() }
} finally {
    if (Test-Path -LiteralPath $taskProbeExe) { Remove-Item -LiteralPath $taskProbeExe -Force }
    Remove-Item -LiteralPath $taskProbe -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
foreach ($taskFile in $taskFiles) {
    $taskSource = Join-Path $taskArtifacts $taskFile
    $taskTarget = Join-Path $taskOutput $taskFile
    if ([IO.Path]::GetFullPath($taskSource) -ne [IO.Path]::GetFullPath($taskTarget)) { Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Force }
    & openssl dgst -sha256 -sign $taskKey -out ($taskTarget + '.sig') $taskTarget
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $taskFile" }
    & openssl dgst -sha256 -verify $taskPublicKey -signature ($taskTarget + '.sig') $taskTarget
    if ($LASTEXITCODE -ne 0) { throw "Release signature verification failed: $taskFile" }
    $taskHash = (Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($taskTarget + '.sha256', $taskHash + '  ' + $taskFile + "`n", [Text.UTF8Encoding]::new($false))
}
Write-Output "Signed release v$taskVersion`: $taskOutput"
Write-Output 'Upload both assets, both .sha256 files and both .sig files to the draft BEFORE publishing the immutable release.'
