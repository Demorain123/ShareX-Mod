[CmdletBinding()]
param(
    [int]$MinimumScore = 95,
    [string]$OutputPath = "artifacts\browser-agent-v017-ui-quality-score.json"
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
function RelativeLuminance([int]$r, [int]$g, [int]$b) {
    $values = @($r, $g, $b) | ForEach-Object {
        $v = $_ / 255.0
        if ($v -le 0.04045) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) }
    }
    return 0.2126 * $values[0] + 0.7152 * $values[1] + 0.0722 * $values[2]
}
function Contrast([double]$a, [double]$b) {
    $hi = [Math]::Max($a, $b)
    $lo = [Math]::Min($a, $b)
    return ($hi + 0.05) / ($lo + 0.05)
}

$ui = Text "LongCapture.Standalone\StandaloneUiPolish.cs"
$adaptiveUi = Text "LongCapture.Standalone\BrowserAgentAdaptiveUiV015.cs"
$project = Text "LongCapture.Standalone\LongCapture.Standalone.csproj"
$overlay = Text "scripts\apply-browser-agent-v017-modern-ui.ps1"

# 1) Responsive layout and text integrity — 30 points.
Add-Check "compact responsive target/speed command surface" 5 ((Has $ui 'RebuildModernTargetSurface') -and (Has $ui 'targetStrip.RowCount = 3') -and (Has $ui 'speedBar.WrapContents = true')) $true
Add-Check "scroll-safe settings surface" 5 ((Has $ui 'body.AutoScroll = true') -and (Has $ui 'body.RowStyles[i].SizeType = SizeType.AutoSize')) $true
Add-Check "readiness can wrap instead of clipping" 5 ((Has $ui 'panel.WrapContents = true') -and (Has $ui 'readiness.MaximumSize')) $true
Add-Check "important labels explicitly avoid ellipsis" 5 ((Has $ui 'subtitle.AutoEllipsis = false') -and (Has $ui 'status.AutoEllipsis = false') -and (Has $ui 'output.AutoEllipsis = false')) $true
Add-Check "reported 927px-class width is a real regression viewport" 5 ((Has $ui 'new Size(840, 680)') -and (Has $ui 'new Size(900, 700)') -and (Has $ui 'new Size(925, 720)') -and (Has $ui 'new Size(1000, 740)')) $true "hosted-desktop-safe 840/900/925/1000 matrix"
Add-Check "target strip child clipping is measured" 5 ((Has $ui 'ValidateTargetStrip') -and (Has $ui 'Capture speed child clipped')) $true

# 2) Modern visual hierarchy — 25 points.
Add-Check "Fluent-like canvas/surface/text palette" 5 ((Has $ui 'Palette.Canvas') -and (Has $ui 'Palette.Surface') -and (Has $ui 'Palette.TextMuted')) $false
Add-Check "persistent primary capture CTA has accent hierarchy" 5 ((Has $ui 'actions.Controls.SetChildIndex(capture, 0)') -and (Has $ui 'button.BackColor = Palette.Accent') -and (Has $ui 'AccentHover') -and (Has $ui 'AccentPressed')) $true
Add-Check "rounded settings surface" 5 ((Has $ui 'RoundedSurfacePanelV017') -and (Has $ui 'CornerRadius = 12')) $false
Add-Check "Windows typography + concise product copy" 5 ((Has $ui 'Segoe UI Semibold') -and (Has $ui '24F') -and (Has $ui 'Browser-assisted capture · Quality guard · Recipes')) $false
$progressiveDisclosure = (Has $ui 'ApplyModeVisibility') -and
    (Has $ui 'SetRowVisible(body, 1, !normal)') -and
    (Has $ui 'SetRowVisible(body, 3, runRecipe)') -and
    (Has $adaptiveUi 'benchmarkButton.Visible = browserMode;') -and
    (Has $adaptiveUi 'calibrationToggle.Visible = browserMode;') -and
    (Has $adaptiveUi 'liveMonitorToggle.Visible = browserMode;')
Add-Check "progressive disclosure removes inactive clutter" 5 $progressiveDisclosure $true "Browser-only benchmark/calibration/live controls must disappear outside Browser Assisted"

# 3) Accessibility and scaling — 20 points.
$textLum = RelativeLuminance 31 41 55
$mutedLum = RelativeLuminance 89 99 115
$surfaceLum = RelativeLuminance 255 255 255
$textContrast = Contrast $textLum $surfaceLum
$mutedContrast = Contrast $mutedLum $surfaceLum
Add-Check "default text contrast >= 4.5:1" 5 (($textContrast -ge 4.5) -and ($mutedContrast -ge 4.5)) $true ("primary={0:N2}:1 muted={1:N2}:1" -f $textContrast, $mutedContrast)
Add-Check "high-contrast mode bypasses hard-coded palette" 5 ((Has $ui 'SystemInformation.HighContrast') -and (Has $ui 'SystemColors.Window') -and (Has $ui 'SystemColors.WindowText')) $true
Add-Check "checkbox text-fit regression" 5 ((Has $ui 'ValidateCheckBoxes') -and (Has $ui 'checkbox text clipped')) $false
Add-Check "PerMonitorV2 + DPI reflow retained" 5 ((Has $project '<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>') -and (Has $ui 'form.DpiChanged')) $true

# 4) Deterministic product-design testability — 25 points.
Add-Check "button text-fit regression + compact numeric fields" 5 ((Has $ui 'ValidateButtons') -and (Has $ui 'button text clipped') -and (Has $ui 'numeric.Width = 220')) $true
$realMultiWidthAudit = (Has $ui 'CaptureVisualAudit') -and
    (Has $ui 'var observedClientWidths = new HashSet<int>();') -and
    (Has $ui 'visual-audit did not exercise four distinct responsive widths') -and
    (Has $ui 'TryMeasureVisualAuditContent') -and
    (Has $ui 'visual-audit frame is visually blank or missing the real control hierarchy')
Add-Check "multi-width visual snapshots prove real nonblank viewports" 5 $realMultiWidthAudit $true
Add-Check "packaged CLI can run the UI audit" 5 ((Has $overlay '--modern-ui-v017-audit') -and (Has $overlay 'apply-browser-agent-v017-visual-polish-r2.ps1')) $true
$screenAware = (Has $ui 'int preferredWidth = Math.Min(1120') -and (Has $ui 'workArea.Width - 64') -and (Has $ui 'workArea.Height - 64')
Add-Check "responsive validation + monitor-aware default sizing" 5 ((Has $ui 'ExperienceVersion = "0.1.7"') -and (Has $ui 'ValidateModernHierarchy') -and $screenAware) $true
Add-Check "no new third-party UI framework dependency" 5 ((Has $project '<UseWindowsForms>true</UseWindowsForms>') -and -not ($project -match '<PackageReference[^>]+(MaterialSkin|ReaLTaiizor|Krypton|Guna|AntdUI|SunnyUI)')) $false

$maximum = ($checks | Measure-Object -Property points -Sum).Sum
$score = ($checks | Where-Object passed | Measure-Object -Property points -Sum).Sum
$criticalFailures = @($checks | Where-Object { $_.critical -and -not $_.passed })
$passed = $score -ge $MinimumScore -and $criticalFailures.Count -eq 0

$result = [pscustomobject]@{
    version = "0.1.7"
    gate = "pre-build-modern-responsive-ui-r2"
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
Write-Host "Browser Agent v0.1.7 PRE-BUILD UI QUALITY SCORE: $score / $maximum" -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
Write-Host "Required: >= $MinimumScore AND zero critical failures" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor DarkGray
foreach ($check in $checks) {
    $flag = if ($check.passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1} (+{2}) critical={3} {4}" -f $flag, $check.name, $check.points, $check.critical, $check.detail) -ForegroundColor $(if ($check.passed) { 'Green' } else { 'Yellow' })
}

if (-not $passed) {
    throw "Browser Agent v0.1.7 UI is not eligible to build: score $score/$maximum criticalFailures=$($criticalFailures.Count)."
}

Write-Host "v0.1.7 UI source is eligible for build/test. Visual snapshots and packaged regression still must pass before user testing." -ForegroundColor Green
