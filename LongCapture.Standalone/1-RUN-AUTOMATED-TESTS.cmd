@echo off
setlocal
cd /d "%~dp0"

set "LC_CI=0"
if /I "%~1"=="--ci" set "LC_CI=1"

if not exist "%~dp0Run-LongCapture-AutomatedTests.ps1" (
  echo [FAIL] Run-LongCapture-AutomatedTests.ps1 is missing.
  if "%LC_CI%"=="0" pause
  exit /b 2
)

if "%LC_CI%"=="1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-LongCapture-AutomatedTests.ps1" -Ci
) else (
  title LongCapture Automated Acceptance
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-LongCapture-AutomatedTests.ps1"
)

set "LC_EXIT=%ERRORLEVEL%"
if not "%LC_CI%"=="1" (
  echo.
  if "%LC_EXIT%"=="0" (
    echo Automated acceptance PASSED. You may continue with the real-world tests in TESTING.md.
  ) else (
    echo Automated acceptance FAILED. Do NOT continue to real-world testing yet.
  )
  echo.
  pause
)
exit /b %LC_EXIT%
