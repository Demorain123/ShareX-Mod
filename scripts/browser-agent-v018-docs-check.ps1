[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$testing = [IO.File]::ReadAllText((Join-Path $repoRoot "LongCapture.Standalone\TESTING.md"))
$readme = [IO.File]::ReadAllText((Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\README.md"))

$requiredTesting = @(
  'Repair precision: Perfect',
  'Repair time: 3 min',
  '[BA_MARK]',
  '[BA_REPAIR]',
  'Quality Guard / repair review',
  'attempt=N/∞',
  'OCR fallback for repair verification or pixel/canvas-only endpoint recognition'
)
$requiredReadme = @(
  'Perfect',
  'no attempt-count limit',
  'repair-time selector',
  '[BA_MARK]',
  'semantic DOM-anchor',
  'manual F8 stop',
  'OCR/heavier visual recognition is **not bundled'
)
$forbidden = @(
  'manual F8 does not start a browser-moving post-review',
  'no page-moving post-review begins after F8',
  'Low / Medium / High repair precision;'
)

foreach ($marker in $requiredTesting) {
  if (-not $testing.Contains($marker)) { throw "v0.1.8 TESTING.md missing release-truth marker: $marker" }
}
foreach ($marker in $requiredReadme) {
  if (-not $readme.Contains($marker)) { throw "v0.1.8 BrowserAgent README missing release-truth marker: $marker" }
}
foreach ($marker in $forbidden) {
  if ($testing.Contains($marker) -or $readme.Contains($marker)) { throw "v0.1.8 docs contain superseded behavior: $marker" }
}

Write-Host "Browser Agent v0.1.8 release documentation truth gate passed: Perfect/time-budget/manual-stop-repair/OCR-limit claims match current scope." -ForegroundColor Green
