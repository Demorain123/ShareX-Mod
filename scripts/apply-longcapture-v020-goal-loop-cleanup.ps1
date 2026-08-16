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
