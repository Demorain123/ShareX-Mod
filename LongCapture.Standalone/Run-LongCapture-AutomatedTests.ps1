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

function Try-ReadCompletedReport([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $candidate = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($candidate.Status -in @('PASS_AUTOMATED', 'FAIL_AUTOMATED')) {
            return $candidate
        }
    }
    catch {
        # The writer may have created the file but not finished the atomic-sized JSON write yet.
        # Retry on the next short polling interval instead of treating a partial read as failure.
    }
    return $null
}

try {
    $profile = if ($Deep) { 'DEEP' } else { 'QUICK' }
    $timeoutSeconds = if ($Deep) { 900 } else { 180 }

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

    $process = $null
    $report = $null
    $processExitCode = $null
    $residualProcessTerminated = $false
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $exitObservedAt = $null

    try {
        # Do NOT use Start-Process -Wait here. LongCapture initializes WinForms/Avalonia desktop
        # infrastructure during the functional tests, and those hosts can keep the process alive
        # after AutomationTestRunner has already completed and written its final report. The report
        # is the authoritative completion signal for this test command.
        $process = Start-Process -FilePath (Join-Path $root 'LongCapture.exe') `
            -ArgumentList '--automation-test' `
            -WorkingDirectory $root `
            -PassThru

        while ($timer.Elapsed.TotalSeconds -lt $timeoutSeconds) {
            $report = Try-ReadCompletedReport -Path $jsonPath
            if ($null -ne $report) { break }

            try {
                $process.Refresh()
                if ($process.HasExited) {
                    if ($null -eq $processExitCode) { $processExitCode = $process.ExitCode }
                    if ($null -eq $exitObservedAt) { $exitObservedAt = [DateTime]::UtcNow }
                    # Allow a brief filesystem flush grace period if the process exited just before
                    # the completed JSON became visible.
                    if (([DateTime]::UtcNow - $exitObservedAt).TotalSeconds -ge 5) { break }
                }
            }
            catch {
                if ($null -eq $exitObservedAt) { $exitObservedAt = [DateTime]::UtcNow }
            }

            Start-Sleep -Milliseconds 250
        }

        # One final read after the loop handles a report that landed on the timeout/exit boundary.
        if ($null -eq $report) {
            $report = Try-ReadCompletedReport -Path $jsonPath
        }

        if ($null -eq $report) {
            $state = if ($null -ne $process -and $process.HasExited) { "process exited code=$($process.ExitCode)" } else { 'process still running' }
            throw "LongCapture automation did not produce a completed report within $timeoutSeconds seconds ($state)."
        }

        # The automated cases are complete once the report has a terminal status. If desktop-host
        # lifetime keeps LongCapture alive, terminate only this spawned test process so the launcher
        # and CI can continue immediately instead of waiting on GUI infrastructure indefinitely.
        if ($null -ne $process) {
            $process.Refresh()
            if ($process.HasExited) {
                $processExitCode = $process.ExitCode
            }
            else {
                Start-Sleep -Milliseconds 500
                $process.Refresh()
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    $residualProcessTerminated = $true
                    try { $process.WaitForExit(5000) | Out-Null } catch { }
                }
            }
        }
    }
    finally {
        Remove-Item Env:LONGCAPTURE_AUTOMATION_REPORT -ErrorAction SilentlyContinue
        Remove-Item Env:LONGCAPTURE_AUTOMATION_PROFILE -ErrorAction SilentlyContinue

        # On launcher exceptions/timeouts, do not leak the test copy of LongCapture.
        if ($null -ne $process) {
            try {
                $process.Refresh()
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    $residualProcessTerminated = $true
                }
            }
            catch { }
        }
    }

    $timer.Stop()
    $exitCode = if ($report.Status -eq 'PASS_AUTOMATED' -and [int]$report.FailedCount -eq 0) { 0 } else { 90 }

    $lines = @(
        "LongCapture automated acceptance launcher",
        "Time: $(Get-Date -Format o)",
        "Package: $root",
        "Profile: $($report.Profile)",
        "DurationSeconds: $([Math]::Round($timer.Elapsed.TotalSeconds, 2))",
        "LogicalExitCode: $exitCode",
        "ProcessExitCode: $(if ($null -eq $processExitCode) { 'not-observed' } else { $processExitCode })",
        "ResidualProcessTerminated: $residualProcessTerminated",
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
    if ($residualProcessTerminated) {
        Write-Status 'Automation completed normally; the launcher closed a leftover test-host process after the final report was written.' Yellow
    }
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
