@echo off
setlocal
cd /d "%~dp0"
set "SESSION=%~1"
if not "%SESSION%"=="" goto :run

for /f "usebackq delims=" %%D in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$roots=@((Join-Path $PWD 'ShareX-Mod\CaptureSessions'),(Join-Path $env:LOCALAPPDATA 'ShareX-Mod\CaptureSessions')); $d=$roots ^| %% { if(Test-Path $_){ Get-ChildItem $_ -Directory -ErrorAction SilentlyContinue } } ^| Where-Object { Test-Path (Join-Path $_.FullName 'raw-frames-v016') } ^| Sort-Object LastWriteTime -Descending ^| Select-Object -First 1; if($d){$d.FullName}"`) do set "SESSION=%%D"

if "%SESSION%"=="" (
  echo No v0.1.6 raw-frame capture session was found.
  echo Turn on Debug, perform one real capture, then run this again.
  pause
  exit /b 2
)

:run
echo Replaying: %SESSION%
"%~dp0LongCapture.exe" --replay "%SESSION%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo Replay failed with exit code %RC%.
  pause
  exit /b %RC%
)

echo Replay completed. See the capture session folder for LongCapture-Replay-v016-*.png.
pause
exit /b 0
