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
$progressPath = Join-Path $reportDir 'automation-progress.txt'
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
        # A reader can race the final write. Retry on the next short polling interval.
    }
    return $null
}

function Get-LastProgress([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'no progress marker was written' }
    try {
        $line = Get-Content -LiteralPath $Path | Select-Object -Last 1
        if ([string]::IsNullOrWhiteSpace($line)) { return 'progress file is empty' }
        return $line
    }
    catch {
        return "progress read failed: $($_.Exception.Message)"
    }
}

function Get-MarkOfWebZoneId([string]$Path) {
    try {
        $stream = Get-Content -LiteralPath $Path -Stream Zone.Identifier -ErrorAction Stop
        $zoneLine = $stream | Where-Object { $_ -match '^ZoneId=' } | Select-Object -First 1
        if ($null -ne $zoneLine) { return $zoneLine.Trim() }
        return 'Zone.Identifier present (ZoneId unavailable)'
    }
    catch {
        return 'none-or-unavailable'
    }
}

function Start-LongCaptureDirect([string]$ExePath, [string]$WorkingDirectory) {
    # Start-Process normally uses the Windows shell on desktop Windows. On a freshly downloaded,
    # unsigned portable build that shell path can be intercepted by reputation/security UI and
    # surface Win32 ERROR_CANCELLED (1223) before LongCapture writes even its first progress marker.
    # UseShellExecute=false goes straight through Process.Start/CreateProcess instead. This keeps
    # the exact executable/arguments/environment used by CI while avoiding an unnecessary shell hop.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ExePath
    $psi.Arguments = '--automation-test'
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false

    try {
        $started = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $started) {
            throw 'Process.Start returned null.'
        }
        return $started
    }
    catch {
        $nativeCode = 'n/a'
        try {
            if ($_.Exception -is [System.ComponentModel.Win32Exception]) {
                $nativeCode = $_.Exception.NativeErrorCode
            }
        }
        catch { }
        $zoneId = Get-MarkOfWebZoneId -Path $ExePath
        throw "Direct LongCapture.exe launch failed. NativeErrorCode=$nativeCode; MarkOfWeb=$zoneId; $($_.Exception.Message)"
    }
}

try {
    $profile = if ($Deep) { 'DEEP' } else { 'QUICK' }
    # Quick covers each functional area once and should be fast. A 90-second ceiling catches a
    # deadlock promptly instead of making the user wait for a test that is no longer progressing.
    $timeoutSeconds = if ($Deep) { 600 } else { 90 }

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
    $env:LONGCAPTURE_AUTOMATION_PROGRESS = $progressPath
    $env:LONGCAPTURE_AUTOMATION_PROFILE = if ($Deep) { 'deep' } else { 'quick' }

    $process = $null
    $report = $null
    $processExitCode = $null
    $residualProcessTerminated = $false
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $exitObservedAt = $null
    $exePath = Join-Path $root 'LongCapture.exe'
    $markOfWeb = Get-MarkOfWebZoneId -Path $exePath

    try {
        # Do NOT use Start-Process here. The local downloaded-package path can be intercepted by
        # shell reputation/security UI before the test process starts. The terminal automation JSON
        # remains the authoritative completion signal after the direct process launch.
        $process = Start-LongCaptureDirect -ExePath $exePath -WorkingDirectory $root

        while ($timer.Elapsed.TotalSeconds -lt $timeoutSeconds) {
            $report = Try-ReadCompletedReport -Path $jsonPath
            if ($null -ne $report) { break }

            try {
                $process.Refresh()
                if ($process.HasExited) {
                    if ($null -eq $processExitCode) { $processExitCode = $process.ExitCode }
                    if ($null -eq $exitObservedAt) { $exitObservedAt = [DateTime]::UtcNow }
                    if (([DateTime]::UtcNow - $exitObservedAt).TotalSeconds -ge 5) { break }
                }
            }
            catch {
                if ($null -eq $exitObservedAt) { $exitObservedAt = [DateTime]::UtcNow }
            }

            Start-Sleep -Milliseconds 250
        }

        if ($null -eq $report) {
            $report = Try-ReadCompletedReport -Path $jsonPath
        }

        if ($null -eq $report) {
            $state = if ($null -ne $process -and $process.HasExited) { "process exited code=$($process.ExitCode)" } else { 'process still running' }
            $lastProgress = Get-LastProgress -Path $progressPath
            throw "LongCapture automation did not produce a completed report within $timeoutSeconds seconds ($state). Last progress: $lastProgress"
        }

        # Once a terminal report exists, every requested case has finished. Close only the spawned
        # test host if WinForms/Avalonia lifetime still keeps it alive.
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
        Remove-Item Env:LONGCAPTURE_AUTOMATION_PROGRESS -ErrorAction SilentlyContinue
        Remove-Item Env:LONGCAPTURE_AUTOMATION_PROFILE -ErrorAction SilentlyContinue

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
        "MarkOfWeb: $markOfWeb",
        "LaunchMode: ProcessStartInfo UseShellExecute=false",
        "Status: $($report.Status)",
        "PASS: $($report.PassedCount)",
        "FAIL: $($report.FailedCount)",
        "MANUAL_REQUIRED: $($report.ManualRequiredCount)",
        "Progress: $progressPath",
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
    $lastProgress = Get-LastProgress -Path $progressPath
    $message = "AUTOMATED ACCEPTANCE LAUNCHER FAILED`r`n$($_.Exception.ToString())`r`nLastProgress: $lastProgress"
    $message | Set-Content -LiteralPath $summaryPath -Encoding UTF8 -ErrorAction SilentlyContinue
    Write-Status ''
    Write-Status 'AUTOMATED ACCEPTANCE: FAIL' Red
    Write-Status $_.Exception.Message Red
    Write-Status "Last progress: $lastProgress" Yellow
    Write-Status 'Stop here. Do NOT continue to real-world testing.' Red
    Write-Status "Launcher report: $summaryPath" Yellow
    exit 2
}
