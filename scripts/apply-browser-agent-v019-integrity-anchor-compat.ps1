[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$text = [IO.File]::ReadAllText($session)
$marker = 'BrowserAgentIntegrityProofV019.ObserveFastPath(manifest.Frames, record);'

if ($text.Contains($marker)) {
    Write-Host "[BrowserAgent-v0.1.9-integrity-compat] already: $marker" -ForegroundColor DarkYellow
    exit 0
}

# v0.1.8 adds calibration/adaptive bookkeeping between SaveNewFrameAsync and
# ValidateProgress, so the original v0.1.9 placement anchor was too broad. The
# integrity policy evaluation is a stable semantic point: all first-pass evidence
# has been collected, while the frame has not yet been classified clean/suspect.
$old = '                BrowserAgentIntegrityAssessmentV018 integrity = BrowserAgentIntegrityPolicyV018.Evaluate(manifest.Frames, record);'
$new = @'
                BrowserAgentIntegrityProofV019.ObserveFastPath(manifest.Frames, record);
                BrowserAgentIntegrityAssessmentV018 integrity = BrowserAgentIntegrityPolicyV018.Evaluate(manifest.Frames, record);
'@
if (-not $text.Contains($old)) {
    throw "Browser Agent v0.1.9 fast-path compatibility anchor missing: v0.1.8 integrity evaluation"
}

if (-not $CheckOnly) {
    [IO.File]::WriteAllText($session, $text.Replace($old, $new.TrimEnd("`r", "`n")), [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.9-integrity-compat] applied stable fast-path placement." -ForegroundColor Cyan
} else {
    Write-Host "[BrowserAgent-v0.1.9-integrity-compat] stable fast-path placement compatible." -ForegroundColor Green
}
