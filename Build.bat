@echo off
cd /d "%~dp0"
if not exist "%~dp0app\build.ps1" goto notsource
if not exist "%~dp0app\package.json" goto notsource

set "KEEP="
if /i "%~1"=="keep" set "KEEP=-Keep"

start "" powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0app\build.ps1" %KEEP%
exit /b 0

:notsource
echo.
echo   Build.bat must sit next to the "app" folder.
echo   Nothing was changed.
echo.
pause
exit /b 1
