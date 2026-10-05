@echo off
rem ============================================================
rem  Multi-zone REAL KEYBOARD demo (dev only, zones fork)
rem  Gatekeeper + cleanup-restoring launcher.
rem
rem  Runs the SAME pipeline as the simulator demo - tray ->
rem  service -> real EC through InsydeDCHU.dll.
rem
rem  Policy: the fork is the development mainline; the installed
rem  production stays UNTOUCHED (no code/config edits) and is kept
rem  STOPPED while the demo runs - and stays stopped on exit
rem  (restart it yourself whenever you want it back).
rem
rem  Before starting this script verifies:
rem    - no leftover fork service/tray/simulator processes
rem      (multiple service instances fight over the EC/IPC and
rem      show up as "mode does not switch" ghosts)
rem    - the installed production service is stopped (the EC is a
rem      shared resource - two writers fight)
rem    - the isolated settings/state directory is writable
rem
rem  On exit (close this console or Ctrl+C): all demo processes
rem  are killed; the production service stays stopped.
rem ============================================================
setlocal EnableExtensions
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights - please accept the UAC prompt...
  powershell -NoProfile -Command "try { Start-Process -FilePath '%~f0' -Verb RunAs } catch { Write-Host ('ELEVATION DECLINED: ' + $_.Exception.Message) }"
  exit /b
)
cd /d "%~dp0.."
set ROOT=%CD%\
set SVC=%ROOT%ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe
set TRAY=%ROOT%ColorfulLedKeyboard.Tray.Wpf\bin\Release\net8.0-windows10.0.22621.0\ColorfulLedKeyboard.Tray.exe
set ISOLATED_DIR=%LOCALAPPDATA%\ClevoLEDKeyboardControlZones
set CLEVO_LED_SETTINGS_PATH=%ISOLATED_DIR%\settings.json

echo [1/6] cleanup: killing leftover fork processes...
taskkill /f /im ColorfulLedKeyboard.Simulator.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Service.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul
tasklist /fi "imagename eq ColorfulLedKeyboard.Service.exe" 2>nul | find /i "ColorfulLedKeyboard.Service.exe" >nul && (
  echo [FAIL] a service process survived the kill - close its console window manually and rerun.
  pause
  exit /b 1
)
tasklist /fi "imagename eq ColorfulLedKeyboard.Tray.exe" 2>nul | find /i "ColorfulLedKeyboard.Tray.exe" >nul && (
  echo [FAIL] a tray process survived the kill - close it manually and rerun.
  pause
  exit /b 1
)
echo       clean.

echo [2/6] stopping the installed production service...
sc stop ClevoLEDKeyboardControlService >nul 2>&1
ping -n 4 127.0.0.1 >nul
sc query ClevoLEDKeyboardControlService | find "STOPPED" >nul
if errorlevel 1 (
  echo [WARN] production service did not report STOPPED yet - continuing anyway.
) else (
  echo       stopped.
)

echo [3/6] checking the isolated settings directory is writable...
mkdir "%ISOLATED_DIR%" 2>nul
echo probe > "%ISOLATED_DIR%\.write-probe" 2>nul
if not exist "%ISOLATED_DIR%\.write-probe" (
  echo [FAIL] cannot write to "%ISOLATED_DIR%" - fix permissions and rerun.
  pause
  exit /b 1
)
del "%ISOLATED_DIR%\.write-probe" >nul 2>&1
echo       writable.

echo [4/6] building (Release)...
dotnet build "%ROOT%ColorfulLedKeyboard.Service\ColorfulLedKeyboard.Service.csproj" -c Release --nologo -v q || goto :buildfail
dotnet build "%ROOT%ColorfulLedKeyboard.Tray.Wpf\ColorfulLedKeyboard.Tray.Wpf.csproj" -c Release --nologo -v q || goto :buildfail

echo [5/6] starting tray + service...
start "" "%TRAY%" --settings
set CLEVO_LED_SIMULATOR_PIPE=
echo [6/6] service running in this console (close the window or press Ctrl+C to stop and restore)...
"%SVC%"

echo.
echo === demo stopped - cleaning up demo processes ===
taskkill /f /im ColorfulLedKeyboard.Service.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Simulator.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
tasklist /fi "imagename eq ColorfulLedKeyboard.Service.exe" 2>nul | find /i "ColorfulLedKeyboard.Service.exe" >nul && (
  echo [WARN] a service process is still alive - close its console window manually.
)
echo Production service intentionally left STOPPED. To use the production stack again:
echo   sc start ClevoLEDKeyboardControlService
echo   then run "C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe"
pause
exit /b 0

:buildfail
echo [ERROR] build failed.
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
pause
exit /b 1
