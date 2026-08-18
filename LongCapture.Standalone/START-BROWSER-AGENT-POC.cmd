@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0LongCapture.exe" (
  echo LongCapture.exe not found next to this script.
  echo.
  pause
  exit /b 2
)
start "LongCapture Browser Agent v0.1 PoC" "%~dp0LongCapture.exe" --browser-agent-poc
