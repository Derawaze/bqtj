@echo off
rem Interactive development preview; compiler location comes from environment or PATH.
cd /d "%~dp0.."
if not "%~1"=="" set "BQTJ_MINGW32_GCC=%~1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Watch-DevLauncher.ps1" -AutoRestart
pause
