[CmdletBinding()]
param([switch]$CheckOnly)

$repoRoot = (& git rev-parse --show-toplevel).Trim()
$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$text = [IO.File]::ReadAllText($main)

$replacements = @(
    @('            startDelay.Enabled = false;', '            startDelay.Enabled = true; // v0.1.3 Browser quality option'),
    @('            scrollDelay.Enabled = false;', '            scrollDelay.Enabled = true; // v0.1.3 Browser quality option'),
    @('            scrollAmount.Enabled = false;', '            scrollAmount.Enabled = true; // v0.1.3 Browser quality option'),
    @('            autoScrollTop.Enabled = false;', '            autoScrollTop.Enabled = true; // v0.1.3 Browser preload option'),
    @('            wholeWindowCapture.Enabled = false;', '            wholeWindowCapture.Enabled = true; // v0.1.3 Browser region option')
)

foreach ($pair in $replacements) {
    if ($text.Contains($pair[1])) { continue }
    if (-not $text.Contains($pair[0])) { throw "v0.1.3 Browser UI option anchor missing: $($pair[0])" }
    if (-not $CheckOnly) { $text = $text.Replace($pair[0], $pair[1]) }
}

if (-not $CheckOnly) {
    [IO.File]::WriteAllText($main, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "Browser Agent v0.1.3 adjustable Browser-mode controls applied." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.3 adjustable-control compatibility passed." -ForegroundColor Green
}
