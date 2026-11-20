# CI build of the Netlane engine for Windows: TrustTunnel Client at the pinned commit with every patch from
# upstream/ (0001 — application routing and kill-switch retention, 0003 — dns_all_through_vpn).
# Runs in a Visual Studio developer shell (x64). Output: research/windows-client/cmake-build-msvc-relwithdebinfo/
# trusttunnel/trusttunnel_client.exe — the path build.ps1 takes the routed engine from.
# Build and tests use the same upstream Makefile/presets as local builds.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $root 'research/windows-client'
$commit = '232f151ea9864849a422666f1ce73ddc1bb4de3a'
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" } }

if (!(Test-Path -LiteralPath (Join-Path $source '.git'))) {
    git clone --depth 1 --branch v1.3.0 https://github.com/TrustTunnel/TrustTunnelClient.git $source; Check 'clone'
}
if ((git -C $source rev-parse HEAD) -ne $commit) { throw 'Unexpected upstream commit.' }
foreach ($patch in (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'upstream') -Filter '*.patch' | Sort-Object Name)) {
    git -C $source apply $patch.FullName; Check "patch $($patch.Name)"
    Write-Output "Applied $($patch.Name)"
}
git -C $source add -N trusttunnel/include/vpn/trusttunnel/application_routes.h trusttunnel/include/vpn/trusttunnel/session_policy.h trusttunnel/src/application_routes.cpp trusttunnel/test/test_application_routes.cpp
Check 'register routing sources for lint'

Push-Location $source
try {
    $env:TT_CLIENT_VERSION = '1.3.0-netlane.1'
    python -m pip install -q -r requirements.txt "conan>=2.0.5,<3"; Check 'pip'
    conan profile detect --exist-ok | Out-Null
    python scripts/bootstrap_conan_deps.py; Check 'conan bootstrap'
    $env:SKIP_VENV = '1'
    $env:SKIP_BOOTSTRAP = '1'
    $env:PRESET = 'msvc-relwithdebinfo'
    $env:CMAKE_ARGS = '-DCMAKE_DISABLE_FIND_PACKAGE_Doxygen=TRUE'
    $msvcBin = Split-Path -Parent (Get-Command cl.exe -ErrorAction Stop).Source
    $pythonScripts = python -c "import sysconfig; print(sysconfig.get_path('scripts'))"
    $gitTools = Join-Path $env:ProgramFiles 'Git/usr/bin'
    $env:PATH = "$msvcBin;$pythonScripts;$gitTools;" + $env:PATH
    make SHELL=sh.exe all; Check 'build'
    make SHELL=sh.exe test; Check 'engine tests'
    make SHELL=sh.exe lint; Check 'engine lint'
    $build = 'cmake-build-msvc-relwithdebinfo'
    Get-Item "$build/trusttunnel/trusttunnel_client.exe" | Format-List FullName, Length
} finally { Pop-Location }
