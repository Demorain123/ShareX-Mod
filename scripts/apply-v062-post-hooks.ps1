[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
if (-not (Test-Path -LiteralPath $manager)) {
    throw "ScrollingCaptureManager.cs not found."
}

$text = [IO.File]::ReadAllText($manager)
$marker = "ShareXModChromeRecipeCaptureEntryV062.TryCaptureAsync"

if ($text.Contains($marker)) {
    Write-Host "[v0.6.2-hook] already present: $marker" -ForegroundColor DarkYellow
    exit 0
}

$old = "ShareXModChromeRecipeCaptureEntry.TryCaptureAsync"
if (-not $text.Contains($old)) {
    throw "v0.6.2 compatibility check failed: Recipe entry hook from v0.6.1 was not found."
}

$text = $text.Replace($old, $marker)
[IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))

Write-Host "ShareX-Mod v0.6.2 post hooks applied." -ForegroundColor Green
