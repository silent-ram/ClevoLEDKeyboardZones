@echo off
rem ============================================================
rem  Multi-zone local demo launcher (dev only, zones fork)
rem  Self-elevating (one UAC prompt): the service needs to write
rem  status files under ProgramData, and cleanup must be able to
rem  kill a previously elevated service.
rem
rem  Isolation: the demo stack uses its own settings file
rem  (%LOCALAPPDATA%\ClevoLEDKeyboardControlZones\settings.json),
rem  so the installed production service never sees multi-zone
rem  settings - your real keyboard and production config stay
rem  untouched. All DCHU commands go to the simulator (no real EC).
rem
rem  Steps after launch:
rem    1. Settings window: lighting page -> mode "Multi-zone".
rem    2. Multi-zone page: configure left/center/right/lightbar.
rem    3. Bottom bar: Apply. The simulator renders the zones.
rem ============================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights - please accept the UAC prompt...
  powershell -NoProfile -Command "try { Start-Process -FilePath '%~f0' -Verb RunAs } catch { Write-Host ('ELEVATION DECLINED: ' + $_.Exception.Message) }"
  exit /b
)
cd /d "%~dp0.."
setlocal
set ROOT=%CD%\
set SIM=%ROOT%ColorfulLedKeyboard.Simulator\bin\Release\net8.0-windows\ColorfulLedKeyboard.Simulator.exe
set SVC=%ROOT%ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe
set TRAY=%ROOT%ColorfulLedKeyboard.Tray.Wpf\bin\Release\net8.0-windows10.0.22621.0\ColorfulLedKeyboard.Tray.exe
set CLEVO_LED_SIMULATOR_PIPE=1
set CLEVO_LED_SETTINGS_PATH=%LOCALAPPDATA%\ClevoLEDKeyboardControlZones\settings.json

echo [1/4] stopping leftovers (elevated cleanup - kills old demo instances too)...
taskkill /f /im ColorfulLedKeyboard.Simulator.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Service.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul

echo [2/4] building (Release)...
dotnet build "%ROOT%ColorfulLedKeyboard.Simulator\ColorfulLedKeyboard.Simulator.csproj" -c Release --nologo -v q || goto :buildfail
dotnet build "%ROOT%ColorfulLedKeyboard.Service\ColorfulLedKeyboard.Service.csproj" -c Release --nologo -v q || goto :buildfail
dotnet build "%ROOT%ColorfulLedKeyboard.Tray.Wpf\ColorfulLedKeyboard.Tray.Wpf.csproj" -c Release --nologo -v q || goto :buildfail

echo [3/4] starting simulator + tray...
start "" "%SIM%" --view-zones
ping -n 2 127.0.0.1 >nul
start "" "%TRAY%" --settings

echo [4/4] starting service in this console (close the window or press Ctrl+C to stop)...
"%SVC%"
echo.
echo Service exited. Demo stopped - the production service was never stopped.
echo Production tray: "C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe"
pause
exit /b 0

:buildfail
echo [ERROR] build failed - see output above.
pause
exit /b 1
