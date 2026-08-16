[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$loop11 = Join-Path $repoRoot "scripts\apply-longcapture-v020-loop11-deferred-tail.ps1"
if (-not (Test-Path -LiteralPath $loop11)) { throw "Loop11 deferred-tail hook is missing." }
if ($CheckOnly) {
    & pwsh -NoProfile -File $loop11 -CheckOnly
} else {
    & pwsh -NoProfile -File $loop11
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Exact lifecycle cleanup gate for the linked compositor source. Earlier marker-based hooks could see
# initialRawBeforeScroll?.Dispose() in setup code and incorrectly assume Session.Dispose already owned
# those bitmaps. Match the actual Dispose method instead, then enforce all v0.1.10-owned state cleanup.
$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$compositorText = [IO.File]::ReadAllText($compositor)
$disposeMarker = '// v0.1.10 exact lifecycle cleanup'
if (-not $compositorText.Contains($disposeMarker)) {
    $oldDispose = @'
        public void Dispose()
        {
            previousStationaryTiles.Clear();
            previousAppendDelta = 0;
        }
'@
    $newDispose = @'
        public void Dispose()
        {
            // v0.1.10 exact lifecycle cleanup
            previousStationaryTiles.Clear();
            previousAppendDelta = 0;
            initialRawBeforeScroll?.Dispose();
            initialRawAfterFirstScroll?.Dispose();
            initialRawBeforeScroll = null;
            initialRawAfterFirstScroll = null;
            initialRepairPending = false;
            deferredTailRepairs.Clear();
        }
'@
    if (-not $compositorText.Contains($oldDispose)) { throw "Exact compositor Dispose() shape changed; refusing to guess cleanup insertion." }
    if (-not $CheckOnly) {
        $compositorText = $compositorText.Replace($oldDispose, $newDispose)
        [IO.File]::WriteAllText($compositor, $compositorText, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.10-cleanup] exact compositor lifecycle cleanup applied/compatible." -ForegroundColor Cyan
} else {
    Write-Host "[v0.1.10-cleanup] exact compositor lifecycle cleanup already present." -ForegroundColor DarkYellow
}

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
if (-not (Test-Path -LiteralPath $manager)) { throw "ScrollingCaptureManager.cs not found." }

$text = [IO.File]::ReadAllText($manager)
$before = $text
$text = [regex]::Replace($text, '(?m)^\s*bool modV016DelayedCompositorUsed = false;\s*\r?\n', '')
$text = [regex]::Replace($text, '(?m)^\s*modV016DelayedCompositorUsed = false;\s*\r?\n', '')

if ($text.Contains('modV016DelayedCompositorUsed')) {
    throw "Retired v0.1.6 compositor flag still has a live reference after cleanup."
}

if ($before -eq $text) {
    Write-Host "[v0.1.10-cleanup] retired compositor flag already absent." -ForegroundColor DarkYellow
} elseif (-not $CheckOnly) {
    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.1.10-cleanup] removed retired compositor flag and CS0219 source." -ForegroundColor Cyan
} else {
    Write-Host "[v0.1.10-cleanup] retired compositor flag can be removed cleanly." -ForegroundColor Green
}
