# CI build of the Netlane engine for Windows: TrustTunnel Client at the pinned commit with every patch from
# upstream/ (0001 — application routing and kill-switch retention, 0003 — dns_all_through_vpn).
# Runs in a Visual Studio developer shell (x64). Output: research/windows-client/cmake-build-msvc-relwithdebinfo/
# trusttunnel/trusttunnel_client.exe — the path build.ps1 takes the routed engine from.
# GNU make is not on the GitHub Windows image, so the preset is driven through cmake directly (as upstream CI does).
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $root 'research/windows-client'
$commit = 'c0990e174fc75b2e52acd15882c62e69a3022581'
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" } }

if (!(Test-Path -LiteralPath (Join-Path $source '.git'))) {
    git clone --depth 1 --branch v1.1.11 https://github.com/TrustTunnel/TrustTunnelClient.git $source; Check 'clone'
}
if ((git -C $source rev-parse HEAD) -ne $commit) { throw 'Unexpected upstream commit.' }
foreach ($patch in (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'upstream') -Filter '*.patch' | Sort-Object Name)) {
    git -C $source apply $patch.FullName; Check "patch $($patch.Name)"
    Write-Output "Applied $($patch.Name)"
}

Push-Location $source
try {
    $env:TT_CLIENT_VERSION = '1.1.11-netlane.2'
    python -m pip install -q -r requirements.txt "conan>=2.0.5,<3"; Check 'pip'
    conan profile detect --exist-ok | Out-Null
    python scripts/bootstrap_conan_deps.py; Check 'conan bootstrap'
    $build = 'cmake-build-msvc-relwithdebinfo'
    cmake --preset msvc-relwithdebinfo -B $build -DCMAKE_DISABLE_FIND_PACKAGE_Doxygen=TRUE; Check 'configure'
    cmake --build $build --target trusttunnel_client test_application_routes; Check 'build'
    & "$build/trusttunnel/test_application_routes.exe"; Check 'engine tests'
    Get-Item "$build/trusttunnel/trusttunnel_client.exe" | Format-List FullName, Length
} finally { Pop-Location }
