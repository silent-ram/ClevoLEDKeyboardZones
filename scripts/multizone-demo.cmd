@echo off
rem ============================================================
rem  Multi-zone local demo launcher (dev only, zones fork)
rem  Starts: simulator + service (simulator-pipe mode) + tray.
rem  Nothing here writes to the real EC: the service forwards all
rem  DCHU commands to the simulator over a named pipe.
rem
rem  Next steps after launch:
rem    1. In the settings window: 灯效设置 -> mode "多分区".
rem    2. Multi-zone page: configure left/center/right/lightbar.
rem    3. Bottom bar: 保存并应用. The simulator renders the zones.
rem
rem  Notes:
rem    - If a previous demo service console is still open, close it
rem      first (only one client can hold the pipe).
rem    - The installed production service also reacts to settings.json:
rem      your real keyboard falls back to its lighting effect while
rem      testing. To keep it fully untouched, stop it first (admin):
rem        sc stop ClevoLEDKeyboardControlService
rem ============================================================
setlocal
set ROOT=%~dp0..
set SIM=%ROOT%\ColorfulLedKeyboard.Simulator\bin\Release\net8.0-windows\ColorfulLedKeyboard.Simulator.exe
set SVC=%ROOT%\ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe
set TRAY=%ROOT%\ColorfulLedKeyboard.Tray.Wpf\bin\Release\net8.0-windows10.0.22621.0\ColorfulLedKeyboard.Tray.exe

if not exist "%SIM%" echo [ERROR] simulator not built: "%SIM%" & pause & exit /b 1
if not exist "%SVC%" echo [ERROR] service not built: "%SVC%" & pause & exit /b 1
if not exist "%TRAY%" echo [ERROR] tray not built: "%TRAY%" & pause & exit /b 1

rem The tray is single-instance (mutex): the installed production tray makes the
rem dev tray exit immediately and pops the OLD settings window. Quit it first.
taskkill /f /im ColorfulLedKeyboard.Simulator.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
start "" "%SIM%"
ping -n 2 127.0.0.1 >nul
set CLEVO_LED_SIMULATOR_PIPE=1
start "ClevoLEDKeyboardZones Service (simulator pipe mode)" "%SVC%"
start "" "%TRAY%" --settings
echo.
echo Started: simulator + service (simulator pipe mode) + tray.
echo Next: settings window - lighting page - choose Multi-zone, configure zones, Apply.
echo To stop: close the service console window, then quit the tray and simulator.
echo To restore the production tray afterwards, run:
echo   "C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe"
pause
