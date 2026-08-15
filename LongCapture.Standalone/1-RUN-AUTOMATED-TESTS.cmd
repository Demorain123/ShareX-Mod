@echo off
setlocal
cd /d "%~dp0"

set "LC_CI=0"
set "LC_DEEP=0"
for %%A in (%*) do (
  if /I "%%~A"=="--ci" set "LC_CI=1"
  if /I "%%~A"=="--deep" set "LC_DEEP=1"
)

if not exist "%~dp0Run-LongCapture-AutomatedTests.ps1" (
  echo [FAIL] Run-LongCapture-AutomatedTests.ps1 is missing.
  if "%LC_CI%"=="0" pause
  exit /b 2
)

set "LC_ARGS="
if "%LC_CI%"=="1" set "LC_ARGS=%LC_ARGS% -Ci"
if "%LC_DEEP%"=="1" set "LC_ARGS=%LC_ARGS% -Deep"

if "%LC_CI%"=="1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-LongCapture-AutomatedTests.ps1" %LC_ARGS%
) else (
  if "%LC_DEEP%"=="1" (
    title LongCapture Deep Automated Acceptance
  ) else (
    title LongCapture Quick Automated Acceptance
  )
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-LongCapture-AutomatedTests.ps1" %LC_ARGS%
)

set "LC_EXIT=%ERRORLEVEL%"
if not "%LC_CI%"=="1" (
  echo.
  if "%LC_EXIT%"=="0" (
    echo Automated acceptance PASSED. Continue with the real-world tests in TESTING.md.
    if "%LC_DEEP%"=="0" echo Optional: run this script with --deep only when you specifically want the 10x stress/memory test.
  ) else (
    echo Automated acceptance FAILED. Do NOT continue to real-world testing yet.
  )
  echo.
  pause
)
exit /b %LC_EXIT%
