[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$text = [IO.File]::ReadAllText($manager)
$marker = 'legacy fallback is structurally disabled.'
if ($text.Contains($marker)) { Write-Host "[v0.1.8-precut] already present" -ForegroundColor DarkYellow; exit 0 }
$old = @'
                            bool fallbackCombined = false;

                            if (newResult == null && modRobustSession != null)
                            {
                                newResult = modRobustSession.TryFallbackCombine(Result, previousScreenshot, modCombineImage);
                                fallbackCombined = newResult != null;
                            }
'@
$new = @'
                            bool fallbackCombined = false; // v0.1.8: legacy fallback is structurally disabled.
'@
if (-not $text.Contains($old)) { throw "v0.1.8 precut could not find the generated v0.4/v0.1.5 fallback block." }
if (-not $CheckOnly) {
    $text = $text.Replace($old,$new)
    [IO.File]::WriteAllText($manager,$text,[Text.UTF8Encoding]::new($true))
}
Write-Host "LongCapture v0.1.8 legacy fallback precut compatibility passed." -ForegroundColor Green
