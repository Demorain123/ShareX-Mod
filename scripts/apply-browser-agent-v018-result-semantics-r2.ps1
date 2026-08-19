[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.8-r2] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "Browser Agent v0.1.8-r2 anchor missing: $Marker" }
    Write-Host "[BrowserAgent-v0.1.8-r2] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.8-r2] applied: $Marker" -ForegroundColor Cyan
    }
}

# Do not call a user-requested partial range a true page-end completion.
Replace-One $session @'
        status?.Invoke(
            complete
                ? $"Complete: true page end confirmed after {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}"
                : $"Partial: {stopReason}, {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}");
'@ @'
        status?.Invoke(
            fullDocumentComplete
                ? $"Complete: true page end confirmed after {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}"
                : requestedRangeComplete
                    ? $"Requested range complete: {BrowserAgentStopPolicyV018.Describe(options.StopMode, options.StopValue)} · {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}"
                    : $"Partial: {stopReason}, {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}");
'@ 'Requested range complete: {BrowserAgentStopPolicyV018.Describe(options.StopMode, options.StopValue)}'

# The integrated main window uses the same semantic distinction.
Replace-One $main @'
            statusLabel.Text = result.IsComplete
                ? $"Complete: confirmed page end · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}"
                : $"Partial: {result.StopReason} · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}";
            qualityLabel.Text = result.IsComplete
                ? "Browser Agent: true page end confirmed; verified-overlap stitch completed."
                : "Browser Agent: Partial capture saved; verified frames were stitched without claiming a complete page.";
'@ @'
            bool requestedRangeComplete = BrowserAgentStopPolicyV018.IsRequestedEnd(result.StopReason);
            statusLabel.Text = result.IsComplete && !requestedRangeComplete
                ? $"Complete: confirmed page end · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}"
                : requestedRangeComplete
                    ? $"Requested range complete: {result.StopReason} · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}"
                    : $"Partial: {result.StopReason} · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}";
            qualityLabel.Text = result.IsComplete && !requestedRangeComplete
                ? "Browser Agent: true page end confirmed; verified-overlap stitch completed."
                : requestedRangeComplete
                    ? "Browser Agent: requested range completed and verified; this is intentionally not a full-page claim."
                    : "Browser Agent: Partial capture saved; verified frames were stitched without claiming a complete page.";
'@ 'Browser Agent: requested range completed and verified; this is intentionally not a full-page claim.'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.8 result-semantics r2 compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.8 result-semantics r2 applied." -ForegroundColor Green
}
