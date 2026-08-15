@echo off
setlocal
cd /d "%~dp0"

where pwsh.exe >nul 2>nul
if %ERRORLEVEL%==0 (
  pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Replay-Last-Capture.ps1" %*
) else (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Replay-Last-Capture.ps1" %*
)
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo Replay failed. Exit code: %RC%
  pause
  exit /b %RC%
)

echo.
echo Replay completed successfully.
pause
exit /b 0
