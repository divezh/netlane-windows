@echo off
rem Gives the default route back to the physical adapter so Netlane can work while
rem another VPN is connected. Run fix-other-vpn.ps1 with -Restore to undo it.
rem ASCII only: cmd.exe codepages make anything else unreliable.
echo.
echo   Netlane: preparing the physical adapter for another VPN...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fix-other-vpn.ps1"
echo.
pause
