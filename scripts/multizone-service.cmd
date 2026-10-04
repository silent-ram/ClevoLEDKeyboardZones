@echo off
title ClevoLEDKeyboardZones Service (simulator pipe mode)
rem Launched elevated by multizone-demo.cmd. Runs the zones service in
rem simulator-pipe mode: every DCHU command is forwarded to the simulator,
rem zero real EC writes. Elevated because saving settings.json under
rem ProgramData requires it (production runs as LocalSystem for the same reason).
rem
rem The INSTALLED production service must be stopped while testing: it treats
rem the fork's "MultiZone" settings as corrupt (unknown enum string in its old
rem parser) and auto-restores its backup, reverting multi-zone on every save.
set CLEVO_LED_SIMULATOR_PIPE=1
sc stop ClevoLEDKeyboardControlService >nul
ping -n 4 127.0.0.1 >nul
rem Restore the newest config the production service quarantined (best effort).
set NEWEST=
for /f "delims=" %%i in ('dir /b /o-d "C:\ProgramData\ClevoLEDKeyboardControl\settings.json.corrupt-*" 2^>nul') do if not defined NEWEST set "NEWEST=%%i"
if defined NEWEST copy /y "C:\ProgramData\ClevoLEDKeyboardControl\%NEWEST%" "C:\ProgramData\ClevoLEDKeyboardControl\settings.json" >nul
"%~dp0..\ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe"
echo.
echo Service exited - restarting the production service...
sc start ClevoLEDKeyboardControlService >nul
echo Done. Production tray can be started from:
echo   "C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe"
pause
