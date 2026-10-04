@echo off
rem ============================================================
rem  Multi-zone local demo launcher (dev only, zones fork)
rem  Builds (Release), then starts: simulator + service (elevated,
rem  simulator-pipe mode) + tray with the settings window open.
rem  Nothing here writes to the real EC: the service forwards all
rem  DCHU commands to the simulator over a named pipe.
rem
rem  Next steps after launch:
rem    1. In the settings window: lighting page -> mode "Multi-zone".
rem    2. Multi-zone page: configure left/center/right/lightbar.
rem    3. Bottom bar: Apply. The simulator renders the zones.
rem
rem  Notes:
rem    - The UAC prompt is required: the service saves settings.json under
rem      ProgramData (ACL) and STOPS the installed production service while
rem      testing - it treats "MultiZone" settings as corrupt and would revert
rem      every save. The production service is restarted automatically when
rem      the demo service console is closed.
rem    - The installed production service also reacts to settings.json:
rem      your real keyboard falls back to its lighting effect while
rem      testing. To keep it fully untouched, stop it first (admin):
rem        sc stop ClevoLEDKeyboardControlService
rem    - To restore the production tray afterwards, run:
rem        "C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe"
rem ============================================================
setlocal
set ROOT=%~dp0..
set SIM=%ROOT%\ColorfulLedKeyboard.Simulator\bin\Release\net8.0-windows\ColorfulLedKeyboard.Simulator.exe
set SVC=%ROOT%\ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe
set TRAY=%ROOT%\ColorfulLedKeyboard.Tray.Wpf\bin\Release\net8.0-windows10.0.22621.0\ColorfulLedKeyboard.Tray.exe

echo [1/3] stopping leftovers (running apps lock the build outputs)...
taskkill /f /im ColorfulLedKeyboard.Simulator.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
taskkill /f /fi "WINDOWTITLE eq ClevoLEDKeyboardZones Service*" >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo [2/3] building (Release)...
dotnet build "%ROOT%\ColorfulLedKeyboard.Simulator\ColorfulLedKeyboard.Simulator.csproj" -c Release --nologo -v q || goto :buildfail
dotnet build "%ROOT%\ColorfulLedKeyboard.Service\ColorfulLedKeyboard.Service.csproj" -c Release --nologo -v q || goto :buildfail
dotnet build "%ROOT%\ColorfulLedKeyboard.Tray.Wpf\ColorfulLedKeyboard.Tray.Wpf.csproj" -c Release --nologo -v q || goto :buildfail

echo [3/3] starting...
rem Three-zone view: the simulator answers the 3-zone capability probe as SUPPORTED,
rem so the service multi-zone gate passes (single-zone view answers "unsupported").
start "" "%SIM%" --view-zones
ping -n 2 127.0.0.1 >nul
rem Elevated: the service saves settings.json under ProgramData (ACL).
powershell -NoProfile -Command "try { Start-Process -FilePath '%~dp0multizone-service.cmd' -Verb RunAs } catch { Write-Host ('ELEVATION DECLINED: ' + $_.Exception.Message) }"
ping -n 3 127.0.0.1 >nul
start "" "%TRAY%" --settings
echo.
echo Started: simulator + service (elevated, simulator pipe mode) + tray.
echo Check the service console says: IPC hosted on ClevoLEDKeyboardControlZones.v2
echo To stop: close the service console window (production service auto-restarts),
echo then quit the tray and simulator.
pause
exit /b 0

:buildfail
echo [ERROR] build failed - see output above.
pause
exit /b 1
