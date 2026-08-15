[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.7 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) { Write-Host "[v0.1.7] already present: $Marker"; return }
    if (-not $text.Contains($Old)) { throw "v0.1.7 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old,$New)
        [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.7] applied/compatible: $Marker"
}

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
$qualitySummary = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModFinalQualitySummary.cs"

Replace-Literal -Path $manager `
  -Old @'
                bestIgnoreBottomOffset = 0;
                Reset();
'@ `
  -New @'
                bestIgnoreBottomOffset = 0;
                ShareXModAnchorContinuityV017.ResetLive();
                ShareXModDelayedCompositorV016.ResetLive();
                Reset();
'@ `
  -Marker 'ShareXModAnchorContinuityV017.ResetLive();'

# Real v0.1.6 Linux.do evidence contained seven strict-anchor rejections. Every one bypassed the
# delayed fixed-overlay compositor through the old CombineImagesAsync fallback. v0.1.7 removes that
# split path: direct anchor, validated fallback and validated temporal prior all feed the same
# raw-frame delayed compositor. If no transition can be validated, stop instead of corrupting.
Replace-Literal -Path $manager `
  -Old @'
                            ShareXModReplayDiagnostics.RecordAnchor(modHasAnchor, modAnchor);

                            int modRepairedOverlayTiles = 0;
                            bool modV016DelayedCompositorUsed = false;
                            Bitmap newResult = null;
                            if (modHasAnchor && Result != null && previousScreenshot != null)
                            {
                                ShareXModV016Telemetry modV016Before = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
                                newResult = ShareXModDelayedCompositorV016.TryAppendLive(
                                    Result,
                                    previousScreenshot,
                                    lastScreenshot,
                                    modAnchor.ScrollDelta);
                                modV016DelayedCompositorUsed = newResult != null;
                                if (modV016DelayedCompositorUsed)
                                {
                                    ShareXModV016Telemetry modV016After = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
                                    int repairedPixels = Math.Max(0, modV016After.RepairedPixelsApprox - modV016Before.RepairedPixelsApprox);
                                    modRepairedOverlayTiles = repairedPixels <= 0 ? 0 : Math.Max(1, repairedPixels / (32 * 24));
                                }
                            }
                            if (newResult == null)
                            {
                                newResult = await CombineImagesAsync(Result, lastScreenshot);
                            }
'@ `
  -New @'
                            ShareXModReplayDiagnostics.RecordAnchor(modHasAnchor, modAnchor);

                            int modRepairedOverlayTiles = 0;
                            bool modV016DelayedCompositorUsed = false;
                            Bitmap newResult = null;

                            if (Result == null)
                            {
                                newResult = (Bitmap)lastScreenshot.Clone();
                                status = ScrollingCaptureStatus.Successful;
                            }
                            else if (previousScreenshot != null)
                            {
                                bool modV017Resolved = ShareXModAnchorContinuityV017.TryResolve(
                                    previousScreenshot,
                                    lastScreenshot,
                                    modHasAnchor,
                                    modAnchor,
                                    out int modV017Delta,
                                    out string modV017Source,
                                    out double modV017Score);
                                ShareXModReplayDiagnostics.RecordResolution(
                                    modV017Resolved,
                                    modV017Delta,
                                    modV017Source,
                                    modV017Score);

                                if (modV017Resolved)
                                {
                                    ShareXModV016Telemetry modV016Before = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
                                    newResult = ShareXModDelayedCompositorV016.TryAppendLive(
                                        Result,
                                        previousScreenshot,
                                        lastScreenshot,
                                        modV017Delta);
                                    modV016DelayedCompositorUsed = newResult != null;
                                    if (modV016DelayedCompositorUsed)
                                    {
                                        ShareXModV016Telemetry modV016After = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
                                        int repairedPixels = Math.Max(0, modV016After.RepairedPixelsApprox - modV016Before.RepairedPixelsApprox);
                                        modRepairedOverlayTiles = repairedPixels <= 0 ? 0 : Math.Max(1, repairedPixels / (32 * 24));
                                    }
                                }

                                if (newResult == null)
                                {
                                    status = ScrollingCaptureStatus.PartiallySuccessful;
                                }
                            }
'@ `
  -Marker 'bool modV017Resolved = ShareXModAnchorContinuityV017.TryResolve('

Replace-Literal -Path $qualitySummary `
  -Old @'
            int semanticRangeCount = 0;
'@ `
  -New @'
            ShareXModV017AnchorTelemetry anchorContinuity = ShareXModAnchorContinuityV017.SnapshotTelemetry();
            if (anchorContinuity.UnresolvedTransitions > 0)
            {
                status = "unresolved";
                confidence = "low";
            }
            else if ((anchorContinuity.FallbackAnchors > 0 || anchorContinuity.PriorValidatedAnchors > 0) &&
                     string.Equals(status, "clean", StringComparison.OrdinalIgnoreCase))
            {
                status = "partially-repaired";
                confidence = "medium";
            }

            int semanticRangeCount = 0;
'@ `
  -Marker 'ShareXModV017AnchorTelemetry anchorContinuity = ShareXModAnchorContinuityV017.SnapshotTelemetry();'

Replace-Literal -Path $qualitySummary `
  -Old @'
                delayedCompositor = new
                {
                    delayedCompositor.AppendCount,
'@ `
  -New @'
                anchorContinuity = new
                {
                    anchorContinuity.DirectAnchors,
                    anchorContinuity.FallbackAnchors,
                    anchorContinuity.PriorValidatedAnchors,
                    anchorContinuity.UnresolvedTransitions,
                    anchorContinuity.LatestDelta,
                    anchorContinuity.LatestSource,
                    anchorContinuity.LatestScore
                },
                delayedCompositor = new
                {
                    delayedCompositor.AppendCount,
'@ `
  -Marker 'anchorContinuity.UnresolvedTransitions,'

Replace-Literal -Path $automation `
  -Old '        "ShareX.ScreenCaptureLib.ShareXModV016ReplaySelfTests",' `
  -New @'
        "ShareX.ScreenCaptureLib.ShareXModV016ReplaySelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV017AnchorContinuitySelfTests",
'@ `
  -Marker '"ShareX.ScreenCaptureLib.ShareXModV017AnchorContinuitySelfTests",'

# The replay button is now part of the recovery workflow, not an optional developer file.
Replace-Literal -Path $automation `
  -Old @'
            "1-RUN-AUTOMATED-TESTS.cmd",
            "Run-LongCapture-AutomatedTests.ps1"
'@ `
  -New @'
            "1-RUN-AUTOMATED-TESTS.cmd",
            "2-REPLAY-LAST-CAPTURE.cmd",
            "Run-LongCapture-AutomatedTests.ps1"
'@ `
  -Marker '"2-REPLAY-LAST-CAPTURE.cmd",'

# Remove stale RC2 wording from the report that the user sees after QUICK.
Replace-Literal -Path $automation `
  -Old 'Normal Long Capture can target an existing Chrome HWND. Reusing that same daily Chrome authenticated DOM/CDP session for Smart Web is not declared complete in v0.1.3 RC2.' `
  -New 'Normal Long Capture can target an existing Chrome HWND. Existing daily-Chrome authenticated DOM/CDP reuse remains an Advanced provider item and is not required for the v0.1.7 core fixed/stitch validation.' `
  -Marker 'v0.1.7 core fixed/stitch validation.'

if ($CheckOnly) { Write-Host "LongCapture v0.1.7 anchor-continuity compatibility passed." -ForegroundColor Green }
else { Write-Host "LongCapture v0.1.7 anchor-continuity hooks applied." -ForegroundColor Green }
