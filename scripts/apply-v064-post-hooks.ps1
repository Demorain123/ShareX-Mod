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
$marker = "ShareXModChromeRecipeCaptureEntryV064.TryCaptureAsync"

if ($text.Contains($marker)) {
    Write-Host "[v0.6.4-hook] already present: $marker" -ForegroundColor DarkYellow
    exit 0
}

$replaced = $false
foreach ($old in @(
    "ShareXModChromeRecipeCaptureEntryV062.TryCaptureAsync",
    "ShareXModChromeRecipeCaptureEntry.TryCaptureAsync"
)) {
    if ($text.Contains($old)) {
        $text = $text.Replace($old, $marker)
        $replaced = $true
        break
    }
}

if (-not $replaced) {
    throw "v0.6.4 compatibility check failed: no known Recipe entry hook was found."
}

[IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
Write-Host "ShareX-Mod v0.6.4 post hooks applied." -ForegroundColor Green
