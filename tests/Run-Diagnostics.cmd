@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose-Compatibility.ps1"
echo.
echo Diagnostic finished. See the JSON file in the diagnostics folder.
pause