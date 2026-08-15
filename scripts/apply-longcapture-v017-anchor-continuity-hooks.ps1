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

# Reset temporal state for each live capture.
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

# v0.1.6 used the delayed compositor only when the strict anchor matcher succeeded, then silently
# fell through to legacy CombineImagesAsync on every rejected transition. Real Linux.do evidence
# showed 7 anchor failures and 7 legacy-sized additions. v0.1.7 resolves each transition first and
# sends direct/fallback/prior-validated deltas through the SAME delayed compositor. If none can be
# validated, stop with PartiallySuccessful instead of manufacturing a corrupted mosaic.
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
                                // Initial viewport is not a stitch transition and needs no matcher.
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
                                    ShareXModCaptureSessionContext.AppendEvent("v017-unresolved-transition-stop");
                                }
                            }
'@ `
  -Marker 'bool modV017Resolved = ShareXModAnchorContinuityV017.TryResolve('

# A fallback is valid but less certain than multi-anchor agreement; unresolved is blocking.
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

if ($CheckOnly) { Write-Host "LongCapture v0.1.7 anchor-continuity compatibility passed." -ForegroundColor Green }
else { Write-Host "LongCapture v0.1.7 anchor-continuity hooks applied." -ForegroundColor Green }
