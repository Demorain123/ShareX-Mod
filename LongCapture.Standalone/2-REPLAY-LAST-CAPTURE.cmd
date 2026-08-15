@echo off
setlocal
cd /d "%~dp0"
set "SESSION=%~1"
if not "%SESSION%"=="" goto :run

echo Searching current and sibling LongCapture folders for the newest raw-frame session...
for /f "usebackq delims=" %%D in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$here=(Get-Location).Path; $parent=Split-Path $here -Parent; $roots=@((Join-Path $here 'ShareX-Mod\CaptureSessions'),(Join-Path $env:LOCALAPPDATA 'ShareX-Mod\CaptureSessions')); if(Test-Path $parent){Get-ChildItem $parent -Directory -ErrorAction SilentlyContinue ^| ForEach-Object {$roots += (Join-Path $_.FullName 'ShareX-Mod\CaptureSessions')}}; $d=$roots ^| Select-Object -Unique ^| %% {if(Test-Path $_){Get-ChildItem $_ -Directory -ErrorAction SilentlyContinue}} ^| Where-Object {Test-Path (Join-Path $_.FullName 'raw-frames-v016')} ^| Sort-Object LastWriteTime -Descending ^| Select-Object -First 1; if($d){$d.FullName}"`) do set "SESSION=%%D"

if "%SESSION%"=="" (
  echo No raw-frame capture session was found.
  echo If the old v0.1.6 folder still exists, you can also drag its capture session folder onto this CMD.
  echo New v0.1.7 diagnostics will include the raw frames directly so this lookup is only needed for older captures.
  pause
  exit /b 2
)

:run
echo Replaying with v0.1.7 anchor continuity + delayed compositor:
echo %SESSION%
"%~dp0LongCapture.exe" --replay "%SESSION%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo Replay failed with exit code %RC%.
  echo This is intentionally fail-closed: v0.1.7 will not use the old legacy mosaic matcher for an unresolved transition.
  pause
  exit /b %RC%
)

echo Replay completed. See the session folder for LongCapture-Replay-v017-*.png and *.json.
pause
exit /b 0
