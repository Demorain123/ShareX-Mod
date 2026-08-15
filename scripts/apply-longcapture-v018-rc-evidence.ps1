[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"

$text = [IO.File]::ReadAllText($main)
$marker = 'ShareXModReplayDiagnostics.Configure(true); // v0.1.8 RC automatic replay evidence'
if ($text.Contains($marker)) {
    Write-Host "[v0.1.8-evidence] already present" -ForegroundColor DarkYellow
    exit 0
}

$old = @'
        CaptureExclusion.SetIncludeInternalDebugWindows(
            debugCaptureUi.Checked && includeInternalDebugWindows.Checked,
            "capture-request-internal-window-policy");
        ShareXModReplayDiagnostics.Configure(debugCaptureUi.Checked);
        if (debugCaptureUi.Checked)
'@
$new = @'
        CaptureExclusion.SetIncludeInternalDebugWindows(
            debugCaptureUi.Checked && includeInternalDebugWindows.Checked,
            "capture-request-internal-window-policy");
        ShareXModReplayDiagnostics.Configure(true); // v0.1.8 RC automatic replay evidence
        if (debugCaptureUi.Checked)
'@

if (-not $text.Contains($old)) { throw "v0.1.8 RC evidence capture-request anchor not found." }
if (-not $CheckOnly) {
    $text = $text.Replace($old,$new)
    [IO.File]::WriteAllText($main,$text,[Text.UTF8Encoding]::new($true))
}
Write-Host "LongCapture v0.1.8 RC automatic raw-frame evidence compatibility passed." -ForegroundColor Green
