#!/usr/bin/env bash
# Linux check of the Windows client: builds the app and Tests.cs with Mono (C# 5) and runs the
# tests under Xvfb. The engine and Wintun are Windows-only, so the run passes once it reaches
# the engine hash check. Needs mono-mcs, mono-runtime, libgdiplus and xvfb.
set -uo pipefail
cd "$(dirname "$0")"
out=$(mktemp -d)
trap 'rm -rf -- "$out"' EXIT
refs=(-r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Security.dll -r:System.Web.Extensions.dll)
res=(-resource:Netlane.ico,Netlane.ico -resource:../../web/icon.png,Netlane.png)
if [[ -d flags ]]; then for f in flags/*.png; do res+=("-resource:$f,flag.$(basename "$f")"); done; fi
mapfile -t app < <(ls ./*.cs | grep -v '/Tests.cs$')
mcs -nologo -langversion:5 -platform:x64 -target:winexe "${res[@]}" -out:"$out/app.exe" "${refs[@]}" "${app[@]}" 2>&1 | grep -v 'warning CS'
[[ -f $out/app.exe ]] || { echo 'APP BUILD FAILED'; exit 1; }
# Mono cannot load PNG-framed .ico frames: the test build draws the stock icon instead.
sed 's/internal static Icon ForSize(int size) {/internal static Icon ForSize(int size) { if (Environment.OSVersion.Platform == PlatformID.Unix) return SystemIcons.Application;/' DarkControls.cs >"$out/DarkControls.cs"
mapfile -t tests < <(ls ./*.cs | grep -v '/DarkControls.cs$')
mcs -nologo -langversion:5 -platform:x64 -target:exe -main:Netlane.Tests "${res[@]}" -out:"$out/t.exe" "${refs[@]}" "${tests[@]}" "$out/DarkControls.cs" 2>&1 | grep -v 'warning CS'
[[ -f $out/t.exe ]] || { echo 'TEST BUILD FAILED'; exit 1; }
result=$(cd "$out" && timeout 120 xvfb-run -a mono t.exe 2>&1)
printf '%s\n' "$result"
rm -rf /tmp/Netlane-test-*
if grep -q 'engine/trusttunnel_client.exe\|Windows client tests passed' <<<"$result"; then echo 'RESULT: pre-engine tests passed'; exit 0; fi
echo 'RESULT: FAILED'; exit 1
