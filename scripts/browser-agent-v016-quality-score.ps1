[CmdletBinding()]
param(
    [int]$MinimumScore = 95,
    [string]$OutputPath = "artifacts\browser-agent-v016-quality-score.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param([string]$Name, [int]$Points, [bool]$Passed, [bool]$Critical = $false, [string]$Detail = "")
    $checks.Add([pscustomobject]@{
        name = $Name
        points = $Points
        passed = $Passed
        critical = $Critical
        detail = $Detail
    })
}

function Text([string]$Path) { [IO.File]::ReadAllText((Join-Path $repoRoot $Path)) }
function Has([string]$Text, [string]$Marker) { $Text.Contains($Marker) }
function Matches([string]$Text, [string]$Pattern) { [regex]::IsMatch($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::Singleline) }

$policy = Text "LongCapture.Standalone\BrowserAgentAdaptivePolicy.cs"
$calib = Text "LongCapture.Standalone\BrowserAgentAdaptiveCalibrationV016.cs"
$ui = Text "LongCapture.Standalone\BrowserAgentAdaptiveUiV015.cs"
$monitor = Text "LongCapture.Standalone\BrowserAgentAdaptiveMonitorV016.cs"
$options = Text "LongCapture.Standalone\BrowserAgentCaptureOptions.cs"
$models = Text "LongCapture.Standalone\BrowserAgentModels.cs"
$main = Text "LongCapture.Standalone\MainForm.cs"
$session = Text "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$worker = Text "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$manifest = Get-Content "LongCapture.Standalone\BrowserAgent\Extension\manifest.json" -Raw | ConvertFrom-Json
$diag = Text "LongCapture.Standalone\BrowserAgentDiagnosticsExporter.cs"
$selftest = Text "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"

# 1) Adaptive/calibration math and safety: 25 points.
Add-Check "five bounded gears" 5 (([regex]::Matches($policy, 'Gear = [0-4]')).Count -ge 5) $true
Add-Check "calibrated controller constructor" 5 (Has $policy 'BrowserAgentCalibrationProfile? calibration = null') $true
Add-Check "AIMD-like slow-fast recovery" 5 ((Has $policy 'currentGear = Math.Max(0, currentGear - 2)') -and (Has $policy 'currentGear++')) $true
Add-Check "RFC-style smoothing + variation" 5 ((Has $calib '0.875 * old') -and (Has $calib '0.75 * variation') -and (Has $calib '4 * FalseQuietVariationMs')) $true
$clampsOk = (Has $calib 'Math.Clamp(overlap, 0.20, 0.50)') -and
    (Matches $calib 'ClampInt\s*\(\s*fastSettle\s*\*\s*settleScale\s*,\s*450\s*,\s*3000\s*\)') -and
    (Matches $calib 'int\s+maxWait\s*=\s*ClampInt\s*\([\s\S]*?3000\s*,\s*12000\s*\)')
Add-Check "calibration clamps" 5 $clampsOk $true "settle 450-3000ms, overlap 20-50%, maxWait 3000-12000ms"

# 2) Real benchmark and live evidence: 25 points.
Add-Check "real benchmark command" 5 ((Has $worker 'case "benchmark":') -and (Has $worker 'benchmarkCapturePipeline')) $true
Add-Check "captureVisibleTab measured" 5 ((Has $worker 'captureVisibleTabMeasured') -and (Has $worker 'captureDurationMs')) $true
Add-Check "Chrome 2/s throttle floor" 5 ((Has $worker 'MIN_CAPTURE_INTERVAL_MS = 520') -and (Has $worker 'MIN_CAPTURE_INTERVAL_MS - elapsed')) $true
Add-Check "false quiet measurement" 5 ((Has $worker 'maxFalseQuietMs') -and (Has $session 'StabilityMaxFalseQuietMs')) $false
Add-Check "per-frame live effective values" 5 ((Has $session 'BrowserAgentAdaptiveTelemetryHub.Publish') -and (Has $models 'EffectiveMaxWaitMs') -and (Has $models 'CaptureVisibleTabMs')) $true

# 3) UX/observability/persistence: 20 points.
$benchmarkGuiSafe = (Has $ui 'Browser benchmark') -and
    (Has $ui 'AttachCalibrationHandler') -and
    (Has $ui 'targetStrip.GrowStyle = TableLayoutPanelGrowStyle.AddRows') -and
    (Has $ui 'targetStrip.SetColumnSpan(bar, Math.Max(1, targetStrip.ColumnCount))')
Add-Check "benchmark GUI + layout-safe global strip" 4 $benchmarkGuiSafe $true "must survive FixedSize target strip and DPI/layout pressure"
Add-Check "calibration user opt-out" 4 ((Has $ui 'Use local calibration') -and (Has $options 'UseLocalCalibration')) $true
Add-Check "live params user opt-out" 4 ((Has $ui 'Live params') -and (Has $options 'ShowLiveAdaptiveMonitor')) $false
Add-Check "no-activate capture-excluded monitor" 4 ((Has $monitor 'WS_EX_NOACTIVATE') -and (Has $monitor 'CaptureExclusion.Apply')) $true
Add-Check "atomic persistent profile + corrupt recovery" 4 ((Has $calib 'File.Move(temp, ProfilePath, overwrite: true)') -and (Has $calib '.invalid-')) $true

# 4) Regression and security boundary: 20 points.
$permissions = @($manifest.permissions)
$permissionOk = ($permissions.Count -eq 3) -and
    ($permissions -contains 'activeTab') -and
    ($permissions -contains 'scripting') -and
    ($permissions -contains 'nativeMessaging') -and
    -not $manifest.host_permissions -and
    ($permissions -notcontains 'debugger')
Add-Check "permission boundary unchanged" 5 $permissionOk $true
Add-Check "F8 remote cancellation retained" 5 ((Has $worker 'captureCancelled') -and (Has $main 'capture stop forwarded to integrated Browser Agent')) $true
Add-Check "adaptive repair retained" 5 ((Has $session '[BA_REPAIR]') -and (Has $session 'RunAdaptivePostReviewAsync')) $true
Add-Check "diagnostics learning markers" 5 ((Has $diag '[BA_CALIB]') -and (Has $diag '[BA_LIVE]') -and (Has $diag '[BA_ADAPT]')) $false

# 5) Deterministic testability: 10 points.
Add-Check "adaptive calibration self-test" 5 ((Has $policy 'CreateSyntheticForSelfTest') -and (Has $selftest 'PersistenceSelfTest')) $true
Add-Check "session audit fields" 5 ((Has $models 'CalibrationConfidenceStart') -and (Has $models 'CalibrationConfidenceEnd') -and (Has $models 'CalibrationFrameSamplesEnd')) $false

$maximum = ($checks | Measure-Object -Property points -Sum).Sum
$score = ($checks | Where-Object passed | Measure-Object -Property points -Sum).Sum
$criticalFailures = @($checks | Where-Object { $_.critical -and -not $_.passed })
$passed = $score -ge $MinimumScore -and $criticalFailures.Count -eq 0

$result = [pscustomobject]@{
    version = "0.1.6"
    gate = "pre-build-source-quality"
    score = $score
    maximum = $maximum
    minimum = $MinimumScore
    passed = $passed
    criticalFailures = $criticalFailures.Count
    evaluatedUtc = [DateTime]::UtcNow.ToString("O")
    checks = $checks
}

$directory = Split-Path -Parent $OutputPath
if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

Write-Host "============================================================" -ForegroundColor DarkGray
Write-Host "Browser Agent v0.1.6 PRE-BUILD QUALITY SCORE: $score / $maximum" -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
Write-Host "Required: >= $MinimumScore AND zero critical failures" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor DarkGray
foreach ($check in $checks) {
    $flag = if ($check.passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1} (+{2}) critical={3} {4}" -f $flag, $check.name, $check.points, $check.critical, $check.detail) -ForegroundColor $(if ($check.passed) { 'Green' } else { 'Yellow' })
}

if (-not $passed) {
    throw "Browser Agent v0.1.6 is not eligible to build: quality score $score/$maximum, criticalFailures=$($criticalFailures.Count)."
}

Write-Host "v0.1.6 source is eligible for build/test. This score is a deterministic release gate, not a claim that real-world screenshot quality is proven." -ForegroundColor Green
