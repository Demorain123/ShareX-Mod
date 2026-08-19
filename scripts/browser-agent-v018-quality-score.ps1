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
$adaptive = Text "LongCapture.Standalone\BrowserAgentAdaptivePolicy.cs"
$integrity = Text "LongCapture.Standalone\BrowserAgentIntegrityPolicyV018.cs"
$models = Text "LongCapture.Standalone\BrowserAgentModels.cs"
$endUi = Text "LongCapture.Standalone\BrowserAgentEndConditionUiV018.cs"
$adaptiveUi = Text "LongCapture.Standalone\BrowserAgentAdaptiveUiV015.cs"
$main = Text "LongCapture.Standalone\MainForm.cs"
$session = Text "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$worker = Text "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$manifest = Get-Content "LongCapture.Standalone\BrowserAgent\Extension\manifest.json" -Raw | ConvertFrom-Json
$selftest = Text "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"
$ui = Text "LongCapture.Standalone\StandaloneUiPolish.cs"
$project = Text "LongCapture.Standalone\LongCapture.Standalone.csproj"

# A. Capture integrity / suspect-frame ledger — 30.
Add-Check "outer rails and side contamination remain measured" 5 ((Has $overlap 'normalizedX < 0.22') -and (Has $overlap 'MinimumEdgeMaeForContamination') -and (Has $session '[BA_EDGE]')) $true
Add-Check "semantic anchors are hashed in-page and persisted without raw text" 5 ((Has $worker 'collectSemanticAnchorsV018') -and (Has $worker 'semanticAnchors,') -and (Has $models 'BrowserAgentDomAnchorRecord') -and (Has $session 'BrowserAgentIntegrityPolicyV018.ReadAnchors')) $true
Add-Check "robust scroll-delta outlier evidence uses median/MAD" 5 ((Has $integrity 'RobustZ(') -and (Has $integrity 'mad * 1.4826') -and (Has $integrity 'scroll-delta-outlier')) $true
Add-Check "DOM anchor continuity can mark layout drift" 5 ((Has $integrity 'CompareAnchors') -and (Has $integrity 'AnchorDriftFailCss') -and (Has $models 'AnchorMaxDocumentDriftCss')) $true
Add-Check "suspect frames become explicit provisional ledger entries" 5 ((Has $models 'QualityState') -and (Has $models 'SuspectMarkedUtc') -and (Has $session '[BA_MARK] frame=')) $true
Add-Check "packaged self-test covers integrity policy" 5 ((Has $integrity 'public static bool SelfTest()') -and (Has $selftest 'self-test failed v0.1.8 integrity/Perfect repair policy')) $true

# B. Post-capture repair semantics — 30.
Add-Check "manual F8 stops forward pass but still runs Quality Guard repair" 6 ((Has $session 'Forward capture stopped by F8. Running Quality Guard') -and (Has $session 'CancellationToken repairToken = cancelled ? CancellationToken.None') -and (Has $worker 'case "repairReset":')) $true
Add-Check "Perfect precision has no attempt-count limit" 6 ((Has $adaptive 'BrowserAgentRepairPrecision.Perfect') -and (Has $adaptive 'UnlimitedRepairAttempts') -and (Has $session 'unlimitedAttempts || attempt <= attempts')) $true
Add-Check "repair has independent user-time budget semantics" 6 ((Has $options 'RepairTimeLimitSeconds') -and (Has $adaptiveUi '"Time · Auto"') -and (Has $adaptiveUi '"Unlimited"') -and (Has $session 'repairDeadlineUtc')) $true
Add-Check "repair result states are repaired/unresolved, never silently verified" 6 ((Has $models 'UnresolvedRepairFrames') -and (Has $session 'frame.QualityState = "repaired"') -and (Has $session 'frame.QualityState = "unresolved"') -and (Has $session 'manual-stop-quality-unresolved')) $true
Add-Check "repair precision ladder includes Low/Medium/High/Perfect" 6 ((Has $adaptive 'PrecisionDisplayNames = ["Low", "Medium", "High", "Perfect"]') -and (Has $adaptive 'BrowserAgentRepairPrecision.High => 4') -and (Has $adaptive 'BrowserAgentRepairPrecision.Perfect => int.MaxValue')) $false

# C. Background + requested endpoint contract — 20.
Add-Check "background-window option explicit; minimized fails closed" 5 ((Has $options 'AllowBackgroundWindow { get; init; } = true') -and (Has $endUi 'Allow target browser window behind other apps') -and (Has $worker 'browserWindow.state === "minimized"')) $true
Add-Check "target tab remains locked active inside its browser window" 5 ((Has $worker 'The attached Chromium tab is no longer active in its browser window') -and (Has $worker 'tab.active')) $true
Add-Check "Auto/DOM/frame/time requested-end modes deterministic" 5 ((Has $policy 'DomCounter') -and (Has $policy 'FrameCount') -and (Has $policy 'ElapsedMinutes') -and (Has $policy 'internal static bool SelfTest()')) $true
Add-Check "requested range keeps completed-requested-range semantics" 5 ((Has $session 'completed-requested-range') -and (Has $session '[BA_END] requested stop matched')) $true

# D. Architecture/security/regression guard — 20.
Add-Check "v0.1.6 calibration and adaptive telemetry retained" 4 ((Has $session 'BrowserAgentCalibrationProfile? calibration') -and (Has $session 'BrowserAgentAdaptiveTelemetryHub.Publish')) $true
Add-Check "v0.1.7 responsive UI retained" 4 ((Has $ui 'ExperienceVersion = "0.1.7"') -and (Has $ui 'CaptureVisualAudit')) $true
Add-Check "capture end and repair precision are progressive Browser UI" 4 ((Has $main 'BrowserAgentEndConditionUiV018 browserAgentEndUi') -and (Has $endUi 'Capture end') -and (Has $adaptiveUi 'Repair precision')) $false
$permissions = @($manifest.permissions)
$permissionBoundary = $permissions.Count -eq 3 -and
    $permissions -contains 'activeTab' -and
    $permissions -contains 'scripting' -and
    $permissions -contains 'nativeMessaging' -and
    $permissions -notcontains 'debugger' -and -not $manifest.host_permissions
Add-Check "extension permission boundary unchanged" 4 $permissionBoundary $true
$noHeavyOcr = -not ($project -match '<PackageReference[^>]+(Tesseract|Paddle|OnnxRuntime)') -and
    -not ($worker -match 'tesseract')
Add-Check "OCR/heavy model stays out of the capture hot path" 4 $noHeavyOcr $false "DOM/hash/visual evidence is primary; OCR remains a post-capture fallback candidate"

$maximum = ($checks | Measure-Object -Property points -Sum).Sum
$score = ($checks | Where-Object passed | Measure-Object -Property points -Sum).Sum
$criticalFailures = @($checks | Where-Object { $_.critical -and -not $_.passed })
$passed = $score -ge $MinimumScore -and $criticalFailures.Count -eq 0

$result = [pscustomobject]@{
    version = "0.1.8"
    gate = "pre-build-integrity-ledger-perfect-repair-background-requested-end-r3"
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

Write-Host "v0.1.8 source is eligible for build/test. Packaged end-to-end regression and real desktop tests are still required." -ForegroundColor Green
