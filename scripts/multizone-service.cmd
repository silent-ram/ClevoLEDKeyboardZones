@echo off
title ClevoLEDKeyboardZones Service (simulator pipe mode)
rem Launched elevated by multizone-demo.cmd. Runs the zones service in
rem simulator-pipe mode: every DCHU command is forwarded to the simulator,
rem zero real EC writes. Elevated because saving settings.json under
rem ProgramData requires it (production runs as LocalSystem for the same reason).
rem
rem Isolated settings file: the fork stack uses its own settings.json so the
rem installed production service (whose old parser treats "MultiZone" as corrupt
rem and restores its backup) never sees or reverts multi-zone saves.
set CLEVO_LED_SIMULATOR_PIPE=1
set CLEVO_LED_SETTINGS_PATH=%LOCALAPPDATA%\ClevoLEDKeyboardControlZones\settings.json
"%~dp0..\ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe"
echo.
echo Service exited. This window can be closed.
pause
