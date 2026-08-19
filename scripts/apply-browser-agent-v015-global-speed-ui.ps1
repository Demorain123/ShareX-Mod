[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$text = [IO.File]::ReadAllText($main)
$marker = 'browserAgentAdaptiveUi.MountGlobalSpeedStrip(targetStrip);'

if ($text.Contains($marker)) {
    Write-Host "[BrowserAgent-v0.1.5-global-speed] already present." -ForegroundColor DarkYellow
    exit 0
}

$old = @'
        BrowserAgentUiModeAdapterV013.ReflowTargetStrip(
            targetStrip, targetLabel, targetSelector, foregroundTargetButton, refreshTargetsButton, openLogsButton);
'@
$new = @'
        BrowserAgentUiModeAdapterV013.ReflowTargetStrip(
            targetStrip, targetLabel, targetSelector, foregroundTargetButton, refreshTargetsButton, openLogsButton);
        browserAgentAdaptiveUi.MountGlobalSpeedStrip(targetStrip);
'@

if (-not $text.Contains($old)) {
    throw "Browser Agent v0.1.5 global speed-strip anchor missing."
}
Write-Host "[BrowserAgent-v0.1.5-global-speed] compatible." -ForegroundColor Green
if (-not $CheckOnly) {
    [IO.File]::WriteAllText($main, $text.Replace($old, $new), [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.5-global-speed] applied." -ForegroundColor Cyan
}
