[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
$text = [IO.File]::ReadAllText($automation)
$legacy = '        "ShareX.ScreenCaptureLib.ShareXModV016ReplaySelfTests",' + [Environment]::NewLine
$marker = '"ShareX.ScreenCaptureLib.ShareXModV018TrustSplitSelfTests",'

if (-not $text.Contains($marker)) { throw "v0.1.8 trust-split regression is missing before retiring v0.1.6 output-policy test." }
if ($text.Contains('"ShareX.ScreenCaptureLib.ShareXModV016ReplaySelfTests",')) {
    if (-not $CheckOnly) {
        $text = $text.Replace($legacy, '')
        [IO.File]::WriteAllText($automation, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "Retired v0.1.6 destructive fixed-overlay replay acceptance from v0.1.8 QUICK." -ForegroundColor Cyan
} else {
    Write-Host "v0.1.6 replay acceptance already retired from v0.1.8 QUICK." -ForegroundColor DarkYellow
}

# v0.1.6's fixture asserts that repeated fixed controls are removed from raster output. v0.1.8
# deliberately reverses that safety policy: accepted document pixels are immutable, fixed/sticky
# raster evidence is diagnostic-only, and browser-aware providers own semantic fixed-element removal.
Write-Host "v0.1.8 replacement gate: ShareXModV018TrustSplitSelfTests." -ForegroundColor Green
