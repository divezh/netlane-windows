$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskSource = Join-Path $taskRoot 'research/windows-client'
$taskPatch = Join-Path $PSScriptRoot 'upstream/0001-application-routing.patch'
if (!(Test-Path -LiteralPath $taskSource)) {
    git clone --depth 1 --branch v1.1.11 https://github.com/TrustTunnel/TrustTunnelClient.git $taskSource
    if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch pinned client source.' }
}
if ((git -C $taskSource rev-parse HEAD) -ne 'c0990e174fc75b2e52acd15882c62e69a3022581') { throw 'Unexpected upstream commit.' }
# A local rebuild reuses the patched tree and its build cache; CI always starts clean.
# Windows PowerShell 5.1 turns redirected native stderr into a terminating error under 'Stop'.
$ErrorActionPreference = 'Continue'
git -C $taskSource apply --reverse --check $taskPatch 2>$null
$taskApplied = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($taskApplied) {
    Write-Output 'Routing patch already applied: incremental rebuild.'
} else {
    if (git -C $taskSource status --porcelain --untracked-files=no) { throw 'Upstream worktree must be clean; do not overwrite local changes.' }
    git -C $taskSource apply --check $taskPatch
    if ($LASTEXITCODE -ne 0) { throw 'Routing patch does not apply.' }
    git -C $taskSource apply $taskPatch
    if ($LASTEXITCODE -ne 0) { throw 'Routing patch failed.' }
}
# Every native step below checks its exit code. Under 'Stop', Windows PowerShell 5.1 aborts on
# the first stderr line of a native command whenever output is redirected (a log, Tee-Object).
$ErrorActionPreference = 'Continue'
# Include new files in upstream formatter/tidy discovery without committing the worktree.
git -C $taskSource add -N trusttunnel/include/vpn/trusttunnel/application_routes.h trusttunnel/include/vpn/trusttunnel/session_policy.h trusttunnel/src/application_routes.cpp trusttunnel/test/test_application_routes.cpp
if ($LASTEXITCODE -ne 0) { throw 'Unable to register new routing sources.' }
Push-Location $taskSource
try {
    # Git's POSIX link.exe must not shadow Microsoft's linker in Conan/nmake.
    $taskMsvcBin = Split-Path -Parent (Get-Command cl.exe -ErrorAction Stop).Source
    if (!(Test-Path -LiteralPath (Join-Path $taskMsvcBin 'link.exe'))) { throw 'MSVC linker is missing.' }
    $env:PATH = $taskMsvcBin + ';' + $env:PATH
    $env:SKIP_VENV = '1'; $env:SKIP_BOOTSTRAP = '1'; $env:PRESET = 'msvc-relwithdebinfo'; $env:TT_CLIENT_VERSION = '1.1.11-netlane.1'
    # Strawberry Perl's Doxygen cannot write under a non-ASCII path (e.g. a folder with an em dash);
    # the docs are not part of the build, so skip it there. CI paths are ASCII and keep the defaults.
    if ($taskSource -match '[^\x00-\x7F]') { $env:CMAKE_ARGS = (($env:CMAKE_ARGS, '-DCMAKE_DISABLE_FIND_PACKAGE_Doxygen=TRUE') -ne $null -ne '') -join ' ' }
    # A failed configure leaves a cache without build.ninja; the Makefile would then skip configuring.
    $taskBuild = Join-Path $taskSource 'cmake-build-msvc-relwithdebinfo'
    if ((Test-Path -LiteralPath (Join-Path $taskBuild 'CMakeCache.txt')) -and !(Test-Path -LiteralPath (Join-Path $taskBuild 'build.ninja'))) { Remove-Item -LiteralPath (Join-Path $taskBuild 'CMakeCache.txt') }
    python scripts/bootstrap_conan_deps.py
    if ($LASTEXITCODE -ne 0) { throw 'Conan bootstrap failed.' }
    make all
    if ($LASTEXITCODE -ne 0) { throw 'Native engine build failed.' }
    make test
    if ($LASTEXITCODE -ne 0) { throw 'Native engine tests failed.' }
    make lint
    if ($LASTEXITCODE -ne 0) { throw 'Native engine lint failed.' }
} finally { Pop-Location }
