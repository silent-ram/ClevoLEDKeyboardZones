@echo off
title ClevoLEDKeyboardZones Service (simulator pipe mode)
rem Launched elevated by multizone-demo.cmd. Runs the zones service in
rem simulator-pipe mode: every DCHU command is forwarded to the simulator,
rem zero real EC writes. Elevated because saving settings.json under
rem ProgramData requires it (production runs as LocalSystem for the same reason).
set CLEVO_LED_SIMULATOR_PIPE=1
"%~dp0..\ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\ColorfulLedKeyboard.Service.exe"
echo.
echo Service exited. This window can be closed.
pause
