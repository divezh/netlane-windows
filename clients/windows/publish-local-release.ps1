param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $taskRoot
$taskVersionMatch = Select-String -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Pattern 'internal const string Version = "(\d+\.\d+\.\d+)"'
if (-not $taskVersionMatch) { throw 'Cannot determine the client version.' }
$taskVersion = $taskVersionMatch.Matches[0].Groups[1].Value
$taskTag = "v$taskVersion"
$taskRepo = 'divezh/netlane-windows'
$taskNames = @('Netlane-windows-x64.zip', "Netlane-Setup-$taskVersion-x64.exe")
$taskAssets = @()
foreach ($taskName in $taskNames) {
    $taskAsset = Join-Path $taskRoot "dist/$taskName"
    foreach ($taskSuffix in @('', '.sha256', '.sig')) {
        if (-not (Test-Path -LiteralPath ($taskAsset + $taskSuffix) -PathType Leaf)) { throw "Missing release file: $taskName$taskSuffix" }
        $taskAssets += $taskAsset + $taskSuffix
    }
    $taskExpected = ((Get-Content -LiteralPath ($taskAsset + '.sha256') -Raw).Trim() -split '\s+')[0]
    if ((Get-FileHash -LiteralPath $taskAsset -Algorithm SHA256).Hash -ne $taskExpected) { throw "Checksum mismatch: $taskName" }
    & openssl dgst -sha256 -verify (Join-Path $taskRoot 'netlane-release.pub') -signature ($taskAsset + '.sig') $taskAsset
    if ($LASTEXITCODE -ne 0) { throw "Invalid owner signature: $taskName" }
}
$taskTests = Join-Path $taskRoot 'artifacts/windows/Netlane/NetlaneTests.exe'
if (-not (Test-Path -LiteralPath $taskTests)) { throw 'Build the Windows client before publishing.' }
$taskTestTemp = Join-Path $taskRoot ('.tools/release-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTestTemp -Force | Out-Null
$taskProbe = Join-Path $taskTestTemp 'write-check.txt'
[IO.File]::WriteAllText($taskProbe, 'Netlane release test workspace')
Remove-Item -LiteralPath $taskProbe -Force
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$taskElevated = ([Security.Principal.WindowsPrincipal]::new($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output "Running the complete Windows client tests; elevated=$taskElevated; TEMP=$taskTestTemp"
$taskOldTemp = $env:TEMP
$taskOldTmp = $env:TMP
try {
    $env:TEMP = $taskTestTemp
    $env:TMP = $taskTestTemp
    & $taskTests
    $taskTestExit = $LASTEXITCODE
} finally {
    $env:TEMP = $taskOldTemp
    $env:TMP = $taskOldTmp
}
if ($taskTestExit -ne 0) { throw 'Client tests failed. Nothing has been published. Test files remain under .tools/release-tests for diagnosis.' }
$taskStatus = & git status --porcelain
if ($LASTEXITCODE -ne 0 -or $taskStatus) { throw 'Commit the reviewed release sources before publication.' }
& gh api user --jq .login
if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication failed. Run gh auth login in this terminal.' }
$taskReleases = & gh release list --repo $taskRepo --limit 100 --json tagName,isDraft
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect releases.' }
$taskExisting = @($taskReleases | ConvertFrom-Json | Where-Object { $_.tagName -eq $taskTag })
if ($taskExisting.Count -gt 0 -and -not $taskExisting[0].isDraft) { throw "$taskTag is already published; immutable releases must not be changed." }
& git fetch origin main
if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch main.' }
& git merge-base --is-ancestor origin/main HEAD
if ($LASTEXITCODE -ne 0) { throw 'Remote main has new changes. Reconcile them and rebuild before publication.' }
$taskHead = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit.' }
& git push origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw 'Cannot publish the release sources. The release has not been published.' }
$taskNotes = Join-Path $PSScriptRoot "installer/RELEASE-$taskVersion.md"
if (-not (Test-Path -LiteralPath $taskNotes)) { throw 'Release notes are missing.' }
if ($taskExisting.Count -eq 0) {
    & gh release create $taskTag @taskAssets --repo $taskRepo --target $taskHead --title "Netlane $taskVersion" --notes-file $taskNotes --draft
    if ($LASTEXITCODE -ne 0) { throw 'Draft release creation/upload failed; do not publish an incomplete draft.' }
} else {
    $taskRelease = (& gh release view $taskTag --repo $taskRepo --json assets | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the existing draft.' }
    $taskExistingNames = @($taskRelease.assets | ForEach-Object { $_.name })
    foreach ($taskAsset in $taskAssets) {
        if ($taskExistingNames -contains [IO.Path]::GetFileName($taskAsset)) {
            throw 'The existing draft already has release files. Verify its contents before retrying; files will not be overwritten automatically.'
        }
    }
    & gh release upload $taskTag @taskAssets --repo $taskRepo
    if ($LASTEXITCODE -ne 0) { throw 'Draft upload failed; do not publish an incomplete draft.' }
}
$taskRelease = (& gh release view $taskTag --repo $taskRepo --json assets | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify draft assets.' }
foreach ($taskAsset in $taskAssets) {
    $taskMatch = @($taskRelease.assets | Where-Object { $_.name -eq [IO.Path]::GetFileName($taskAsset) })
    if ($taskMatch.Count -ne 1 -or $taskMatch[0].size -ne (Get-Item -LiteralPath $taskAsset).Length) { throw 'Draft assets are incomplete; publication stopped.' }
    $taskDigest = 'sha256:' + (Get-FileHash -LiteralPath $taskAsset -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($taskMatch[0].digest -ne $taskDigest) { throw 'Uploaded asset digest mismatch; publication stopped.' }
}
& gh release edit $taskTag --repo $taskRepo --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Publication failed; inspect the draft before retrying.' }
Write-Output "Published: https://github.com/$taskRepo/releases/tag/$taskTag"
