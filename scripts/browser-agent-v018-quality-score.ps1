[CmdletBinding()]
param(
    [int]$MinimumScore = 95,
    [string]$OutputPath = "artifacts\browser-agent-v018-quality-score.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param([string]$Name, [int]$Points, [bool]$Passed, [bool]$Critical = $false, [string]$Detail = "")
    $checks.Add([pscustomobject]@{ name=$Name; points=$Points; passed=$Passed; critical=$Critical; detail=$Detail })
}
function Text([string]$Path) { [IO.File]::ReadAllText((Join-Path $repoRoot $Path)) }
function Has([string]$Text, [string]$Marker) { $Text.Contains($Marker) }

$overlap = Text "LongCapture.Standalone\BrowserAgentOverlapVerifier.cs"
$options = Text "LongCapture.Standalone\BrowserAgentCaptureOptions.cs"
$policy = Text "LongCapture.Standalone\BrowserAgentStopPolicyV018.cs"
$models = Text "LongCapture.Standalone\BrowserAgentModels.cs"
$endUi = Text "LongCapture.Standalone\BrowserAgentEndConditionUiV018.cs"
$main = Text "LongCapture.Standalone\MainForm.cs"
$session = Text "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$worker = Text "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$manifest = Get-Content "LongCapture.Standalone\BrowserAgent\Extension\manifest.json" -Raw | ConvertFrom-Json
$selftest = Text "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"
$ui = Text "LongCapture.Standalone\StandaloneUiPolish.cs"
$project = Text "LongCapture.Standalone\LongCapture.Standalone.csproj"

# A. Real-evidence seam/fixed reliability — 30.
Add-Check "outer rails are measured instead of cropped away" 5 ((Has $overlap 'previous.Width * 0.02') -and (Has $overlap 'normalizedX < 0.22')) $true
Add-Check "edge contamination blocks a seemingly-good median overlap" 5 ((Has $overlap '!leftEdgeContamination && !rightEdgeContamination') -and (Has $overlap 'MinimumEdgeMaeForContamination')) $true
Add-Check "left/center/right evidence is persisted per frame" 5 ((Has $session 'OverlapLeftBandMae = check.LeftBandMeanAbsoluteError') -and (Has $models 'OverlapRightEdgeContamination')) $true
Add-Check "synthetic side-only regression is in packaged Browser self-test" 5 ((Has $selftest 'WriteLeftRailMutatedFrame') -and (Has $selftest '!railOverlap.LeftEdgeContamination')) $true
Add-Check "Discourse sticky-avatar semantic suppression" 5 ((Has $worker '.topic-post.sticky-avatar .topic-avatar') -and (Has $worker 'visibility", "hidden", "important"')) $true
Add-Check "edge failures enter adaptive repair evidence" 5 ((Has $session 'record.RepairCandidate = adaptiveDecision.RepairCandidate ||') -and (Has $session '[BA_EDGE]')) $true

# B. Background-window capture contract — 20.
Add-Check "background window option is explicit and default-on" 5 ((Has $options 'AllowBackgroundWindow { get; init; } = true') -and (Has $endUi 'Allow target browser window behind other apps')) $false
Add-Check "target tab must remain active in its browser window" 5 ((Has $worker 'The attached Chromium tab is no longer active in its browser window') -and (Has $worker 'tab.active')) $true
Add-Check "minimized browser fails closed on visibleTab backend" 5 ((Has $worker 'browserWindow.state === "minimized"') -and (Has $worker 'future CDP provider')) $true
Add-Check "background/focus state is auditable in session.json" 5 ((Has $models 'BackgroundWindowCapture') -and (Has $session 'BackgroundWindowFrames++')) $false

# C. User-requested end conditions — 20.
Add-Check "Auto/DOM/frame/time stop modes are normalized" 5 ((Has $policy 'DomCounter') -and (Has $policy 'FrameCount') -and (Has $policy 'ElapsedMinutes') -and (Has $options 'StopMode')) $true
Add-Check "requested-end policy has deterministic self-test" 5 ((Has $policy 'internal static bool SelfTest()') -and (Has $selftest 'self-test failed requested-end policy')) $true
Add-Check "requested end is checked after an accepted frame" 5 ((Has $session 'BrowserAgentStopPolicyV018.TryMatch(') -and (Has $session '[BA_END] requested stop matched')) $true
Add-Check "requested range is successful while F8 remains Partial manual stop" 5 ((Has $session 'completed-requested-range') -and (Has $session 'partial-manual-stop') -and (Has $session 'BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason)')) $true

# D. Existing adaptive/UI/security architecture remains intact — 30.
Add-Check "manual F8 never enters browser-moving post-review" 5 ((Has $session 'if (!cancelled &&') -and (Has $session 'RunAdaptivePostReviewAsync(')) $true
Add-Check "v0.1.6 local calibration remains connected" 5 ((Has $session 'BrowserAgentCalibrationProfile? calibration') -and (Has $session 'BrowserAgentAdaptiveTelemetryHub.Publish')) $true
Add-Check "v0.1.7 responsive UI remains active" 5 ((Has $ui 'ExperienceVersion = "0.1.7"') -and (Has $ui 'CaptureVisualAudit') -and (Has $ui 'browserAgent || runRecipe')) $true
Add-Check "Capture end is progressive-disclosure Browser UI" 5 ((Has $main 'BrowserAgentEndConditionUiV018 browserAgentEndUi') -and (Has $endUi 'Capture end')) $false
$permissions = @($manifest.permissions)
$permissionBoundary = $permissions.Count -eq 3 -and
    $permissions -contains 'activeTab' -and
    $permissions -contains 'scripting' -and
    $permissions -contains 'nativeMessaging' -and
    $permissions -notcontains 'debugger' -and -not $manifest.host_permissions
Add-Check "extension permission boundary unchanged" 5 $permissionBoundary $true
$noHeavyOcr = -not ($project -match '<PackageReference[^>]+(Tesseract|Paddle|OnnxRuntime)') -and
    -not ($worker -match 'OCR|tesseract')
Add-Check "OCR/heavy model is not added to the capture hot path" 5 $noHeavyOcr $false "DOM counter is the lightweight first endpoint signal"

$maximum = ($checks | Measure-Object -Property points -Sum).Sum
$score = ($checks | Where-Object passed | Measure-Object -Property points -Sum).Sum
$criticalFailures = @($checks | Where-Object { $_.critical -and -not $_.passed })
$passed = $score -ge $MinimumScore -and $criticalFailures.Count -eq 0

$result = [pscustomobject]@{
    version = "0.1.8"
    gate = "pre-build-real-evidence-reliability-background-requested-end"
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
Write-Host "Browser Agent v0.1.8 PRE-BUILD QUALITY SCORE: $score / $maximum" -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
Write-Host "Required: >= $MinimumScore AND zero critical failures" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor DarkGray
foreach ($check in $checks) {
    $flag = if ($check.passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1} (+{2}) critical={3} {4}" -f $flag, $check.name, $check.points, $check.critical, $check.detail) -ForegroundColor $(if ($check.passed) { 'Green' } else { 'Yellow' })
}

if (-not $passed) {
    throw "Browser Agent v0.1.8 is not eligible to build: score $score/$maximum criticalFailures=$($criticalFailures.Count)."
}

Write-Host "v0.1.8 is eligible for build/test. Packaged end-to-end regression and real desktop tests are still required." -ForegroundColor Green
