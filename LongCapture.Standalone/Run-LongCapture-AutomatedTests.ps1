[CmdletBinding()]
param(
    [switch]$Ci,
    [switch]$Deep
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportDir = Join-Path $root "AutomationReports\$stamp"
$jsonPath = Join-Path $reportDir 'automation-report.json'
$summaryPath = Join-Path $reportDir 'launcher-summary.txt'

function Write-Status([string]$Text, [ConsoleColor]$Color = [ConsoleColor]::Gray) {
    if (-not $Ci) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}

try {
    $profile = if ($Deep) { 'DEEP' } else { 'QUICK' }
    Write-Status '============================================================' Cyan
    Write-Status " LongCapture - Automated Acceptance [$profile]" Cyan
    Write-Status '============================================================' Cyan
    Write-Status "Package: $root"
    if (-not $Deep) {
        Write-Status 'Quick is the default pre-real-test gate. Use --deep only for the optional 10x stress/memory loop.' Yellow
    }
    Write-Status ''

    $required = @(
        'LongCapture.exe', 'LongCapture.dll', 'ShareX.ScreenCaptureLib.dll',
        'ShareX.Avalonia.dll', 'ShareX.Mod.v04.json', 'ShareX.Mod.VERSION.json',
        'TESTING.md', '1-RUN-AUTOMATED-TESTS.cmd', 'Run-LongCapture-AutomatedTests.ps1'
    )
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $name))) {
            throw "Portable package is incomplete: missing $name"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $root 'ShareX.exe')) {
        throw 'Standalone package unexpectedly contains ShareX.exe.'
    }

    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $env:LONGCAPTURE_AUTOMATION_REPORT = $jsonPath
    $env:LONGCAPTURE_AUTOMATION_PROFILE = if ($Deep) { 'deep' } else { 'quick' }
    try {
        $process = Start-Process -FilePath (Join-Path $root 'LongCapture.exe') `
            -ArgumentList '--automation-test' `
            -WorkingDirectory $root `
            -Wait -PassThru
        $exitCode = $process.ExitCode
    }
    finally {
        Remove-Item Env:LONGCAPTURE_AUTOMATION_REPORT -ErrorAction SilentlyContinue
        Remove-Item Env:LONGCAPTURE_AUTOMATION_PROFILE -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $jsonPath)) {
        throw "LongCapture did not create the automation JSON report. ExitCode=$exitCode"
    }

    $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $lines = @(
        "LongCapture automated acceptance launcher",
        "Time: $(Get-Date -Format o)",
        "Package: $root",
        "Profile: $($report.Profile)",
        "ExitCode: $exitCode",
        "Status: $($report.Status)",
        "PASS: $($report.PassedCount)",
        "FAIL: $($report.FailedCount)",
        "MANUAL_REQUIRED: $($report.ManualRequiredCount)",
        "JSON: $jsonPath"
    )
    $lines | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    Write-Status ''
    foreach ($case in $report.Cases) {
        $color = if ($case.Status -eq 'PASS') { 'Green' } elseif ($case.Status -eq 'FAIL') { 'Red' } else { 'Yellow' }
        Write-Status ("[{0}] {1} / {2}" -f $case.Status, $case.Category, $case.Name) $color
        Write-Status ("       {0}" -f $case.Detail)
    }
    Write-Status ''

    if ($exitCode -ne 0 -or $report.Status -ne 'PASS_AUTOMATED' -or [int]$report.FailedCount -ne 0) {
        Write-Status 'AUTOMATED ACCEPTANCE: FAIL' Red
        Write-Status 'Stop here. Do NOT start the real-world test yet.' Red
        Write-Status 'Send the newest AutomationReports folder and LongCapture logs/diagnostics for analysis.' Yellow
        Write-Status "Report: $jsonPath" Yellow
        exit 1
    }

    Write-Status 'AUTOMATED ACCEPTANCE: PASS' Green
    Write-Status ("PROFILE={0}  PASS={1}  FAIL={2}  MANUAL_REQUIRED={3}" -f $report.Profile, $report.PassedCount, $report.FailedCount, $report.ManualRequiredCount) Green
    Write-Status 'Automatable functional/regression checks passed. Continue immediately with the real desktop/site tests.' Yellow
    Write-Status "Report: $jsonPath"

    if (-not $Ci) {
        Start-Process -FilePath (Join-Path $root 'TESTING.md') | Out-Null
    }
    exit 0
}
catch {
    New-Item -ItemType Directory -Force -Path $reportDir -ErrorAction SilentlyContinue | Out-Null
    $message = "AUTOMATED ACCEPTANCE LAUNCHER FAILED`r`n$($_.Exception.ToString())"
    $message | Set-Content -LiteralPath $summaryPath -Encoding UTF8 -ErrorAction SilentlyContinue
    Write-Status ''
    Write-Status 'AUTOMATED ACCEPTANCE: FAIL' Red
    Write-Status $_.Exception.Message Red
    Write-Status 'Stop here. Do NOT continue to real-world testing.' Red
    Write-Status "Launcher report: $summaryPath" Yellow
    exit 2
}
