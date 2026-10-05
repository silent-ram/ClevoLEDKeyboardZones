@echo off
rem ============================================================
rem  Multi-zone REAL KEYBOARD demo (dev only, zones fork)
rem  Same pipeline as the simulator demo - tray -> service -> the
rem  exact same DCHU commands - but the last hop goes to the REAL
rem  EC through InsydeDCHU.dll (your actual laptop keyboard).
rem
rem  KNOW BEFORE YOU RUN:
rem   - On single-zone hardware (e.g. P955ET1) the three zone slot
rem     writes collapse onto one register: expect ONE visible color
rem     (last write wins), not per-zone colors. True per-zone
rem     rendering needs a real 3-zone machine; the software path is
rem     identical either way (the simulator already proved it).
rem   - Lightbar (0xF3) stays OFF unless you enable it in the UI;
rem     its behavior on lightbar-less machines is unknown.
rem   - The installed production service is STOPPED during the run
rem     (the EC is a shared resource - two writers would fight) and
rem     is restarted automatically when this console closes.
rem   - Settings are isolated in %LOCALAPPDATA%\ClevoLEDKeyboardControlZones
rem     so the production settings.json is never touched.
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
set SVC=%ROOT%ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe
set TRAY=%ROOT%ColorfulLedKeyboard.Tray.Wpf\bin\Release\net8.0-windows10.0.22621.0\ColorfulLedKeyboard.Tray.exe
set CLEVO_LED_SETTINGS_PATH=%LOCALAPPDATA%\ClevoLEDKeyboardControlZones\settings.json
rem Explicitly clear the simulator switch: an elevated console may inherit it
rem from a previous simulator demo session - real mode must send to the real EC.
set CLEVO_LED_SIMULATOR_PIPE=

echo [1/5] stopping the installed production service (EC is shared)...
sc stop ClevoLEDKeyboardControlService
ping -n 4 127.0.0.1 >nul

echo [2/5] stopping leftovers...
taskkill /f /im ColorfulLedKeyboard.Simulator.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Service.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
rem Verify zero leftovers - multiple service instances fight over the EC/IPC
tasklist /fi "imagename eq ColorfulLedKeyboard.Service.exe" 2>nul | find /i "ColorfulLedKeyboard.Service.exe" >nul && (
  echo [WARN] service processes still alive - killing again...
  taskkill /f /im ColorfulLedKeyboard.Service.exe
  ping -n 2 127.0.0.1 >nul
)

echo [3/5] building (Release)...
dotnet build "%ROOT%ColorfulLedKeyboard.Service\ColorfulLedKeyboard.Service.csproj" -c Release --nologo -v q || goto :buildfail
dotnet build "%ROOT%ColorfulLedKeyboard.Tray.Wpf\ColorfulLedKeyboard.Tray.Wpf.csproj" -c Release --nologo -v q || goto :buildfail

echo [4/5] starting tray...
start "" "%TRAY%" --settings

echo NOTE: if you enabled the lightbar switch (0xF3) in the simulator demo, turn it
echo OFF in the multi-zone page before saving - its behavior on this machine is unknown.
echo [5/5] starting service in REAL mode (commands go to the real keyboard)...
"%SVC%"
echo.
echo Service exited - restarting the production service...
sc start ClevoLEDKeyboardControlService
echo Done. Production tray: "C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe"
pause
exit /b 0

:buildfail
echo [ERROR] build failed - see output above.
echo The production service was NOT restarted - run: sc start ClevoLEDKeyboardControlService
pause
exit /b 1
