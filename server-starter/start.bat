@echo off
rem Tabletop Craft AR server starter for Windows: runs start.ps1 next to this file (see its header for the options).
rem Double-click it, or run "start.bat -NoStart" etc. from a command prompt.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
if errorlevel 1 pause
