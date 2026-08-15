[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.8 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) { Write-Host "[v0.1.8] already present: $Marker" -ForegroundColor DarkYellow; return }
    if (-not $text.Contains($Old)) { throw "v0.1.8 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old,$New)
        [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.8] applied/compatible: $Marker" -ForegroundColor Cyan
}

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
$qualitySummary = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModFinalQualitySummary.cs"

# Reset v0.1.8 state together with the previous-generation state. The v0.1.6 compositor is no
# longer used for pixels but is reset so its legacy telemetry cannot leak between test sessions.
Replace-Literal -Path $manager `
  -Old @'
                ShareXModAnchorContinuityV017.ResetLive();
                ShareXModDelayedCompositorV016.ResetLive();
                Reset();
'@ `
  -New @'
                ShareXModAnchorContinuityV017.ResetLive();
                ShareXModTransitionResolverV018.ResetLive();
                ShareXModTrustSplitCompositorV018.ResetLive();
                ShareXModDelayedCompositorV016.ResetLive();
                Reset();
'@ `
  -Marker 'ShareXModTrustSplitCompositorV018.ResetLive();'

# A rejected frame may be held without changing the reliable reference; this local controls the
# later previous-frame handoff after the normal settle delay has run.
Replace-Literal -Path $manager `
  -Old '                        Stopwatch timer = Stopwatch.StartNew();' `
  -New @'
                        Stopwatch timer = Stopwatch.StartNew();
                        bool modV018HoldReliableReference = false;
'@ `
  -Marker 'bool modV018HoldReliableReference = false;'

# Replace v0.1.7's destructive fixed/sticky repair compositor with v0.1.8 trust split:
# validated motion chooses geometry; the raster compositor is append-only and cannot rewrite body.
Replace-Literal -Path $manager `
  -Old @'
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
'@ `
  -New @'
                                bool modV018Resolved = ShareXModTransitionResolverV018.TryResolve(
                                    previousScreenshot,
                                    lastScreenshot,
                                    modHasAnchor,
                                    modAnchor,
                                    out int modV018Delta,
                                    out string modV018Source,
                                    out double modV018Score,
                                    out modV018HoldReliableReference);
                                ShareXModReplayDiagnostics.RecordResolution(
                                    modV018Resolved,
                                    modV018Delta,
                                    modV018Source,
                                    modV018Score);

                                if (modV018Resolved)
                                {
                                    newResult = ShareXModTrustSplitCompositorV018.TryAppendLive(
                                        Result,
                                        previousScreenshot,
                                        lastScreenshot,
                                        modV018Delta);
                                    modV016DelayedCompositorUsed = false;
                                    modRepairedOverlayTiles = 0;
                                }

                                if (newResult == null)
                                {
                                    status = ScrollingCaptureStatus.PartiallySuccessful;
                                }
'@ `
  -Marker 'bool modV018Resolved = ShareXModTransitionResolverV018.TryResolve('

# v0.4 used a second mosaic matcher when the primary returned null. That made "unresolved" a soft
# warning rather than an invariant. Remove it completely; there is only one geometry authority now.
Replace-Literal -Path $manager `
  -Old @'
                            bool fallbackCombined = false;

                            if (newResult == null && modRobustSession != null)
                            {
                                newResult = modRobustSession.TryFallbackCombine(Result, previousScreenshot, lastScreenshot);
                                fallbackCombined = newResult != null;
                            }
'@ `
  -New @'
                            bool fallbackCombined = false; // v0.1.8: legacy fallback is structurally disabled.
'@ `
  -Marker 'legacy fallback is structurally disabled.'

Replace-Literal -Path $manager `
  -Old @'
                            else if (!(modRobustSession?.ShouldContinueAfterCombineFailure() ?? false))
                            {
                                break;
                            }
'@ `
  -New @'
                            else if (!modV018HoldReliableReference)
                            {
                                break;
                            }
'@ `
  -Marker 'else if (!modV018HoldReliableReference)'

# ScrollSnap's useful failure behavior is adopted here: a single rejected frame does not become the
# comparison reference. The next captured frame may validate a two-step catch-up against the last
# reliable frame. If no hold is requested, normal one-step reference advancement is unchanged.
Replace-Literal -Path $manager `
  -Old @'
                        if (lastScreenshot != null)
                        {
                            if (previousScreenshot != null)
                            {
                                previousScreenshot.Dispose();
                            }

                            previousScreenshot = lastScreenshot;
                            lastScreenshot = null;
                        }
'@ `
  -New @'
                        if (lastScreenshot != null)
                        {
                            if (modV018HoldReliableReference)
                            {
                                lastScreenshot.Dispose();
                                lastScreenshot = null;
                            }
                            else
                            {
                                if (previousScreenshot != null)
                                {
                                    previousScreenshot.Dispose();
                                }

                                previousScreenshot = lastScreenshot;
                                lastScreenshot = null;
                            }
                        }
'@ `
  -Marker 'if (modV018HoldReliableReference)'

# v0.1.6 fixed-overlay telemetry must not grade v0.1.8; it no longer owns output pixels.
Replace-Literal -Path $qualitySummary `
  -Old @'
            ShareXModV016Telemetry delayedCompositor = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
            if (delayedCompositor.LowOverlapRisk)
            {
                status = "unresolved";
                confidence = "low";
            }
            else if (delayedCompositor.PendingTailComponents > 0 &&
                     string.Equals(status, "clean", StringComparison.OrdinalIgnoreCase))
            {
                status = "partially-repaired";
                confidence = "medium";
            }
'@ `
  -New @'
            ShareXModV016Telemetry delayedCompositor = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry(); // legacy telemetry only
'@ `
  -Marker 'legacy telemetry only'

# A v0.1.7 unresolved event can now be recovered by the one-gap catch-up path, so final quality is
# based on v0.1.8 terminal state rather than the raw count of lower-level rejected transitions.
Replace-Literal -Path $qualitySummary `
  -Old @'
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
'@ `
  -New @'
            ShareXModV017AnchorTelemetry anchorContinuity = ShareXModAnchorContinuityV017.SnapshotTelemetry();
            ShareXModV018TransitionTelemetry transitionV018 = ShareXModTransitionResolverV018.SnapshotTelemetry();
            ShareXModV018CompositorTelemetry trustSplitV018 = ShareXModTrustSplitCompositorV018.SnapshotLiveTelemetry();
            if (transitionV018.TerminalUnresolved > 0 || transitionV018.PendingGap || trustSplitV018.RejectedAppendCount > 0)
            {
                status = "unresolved";
                confidence = "low";
            }
            else if ((transitionV018.GapHeld > 0 || transitionV018.CatchUpResolved > 0 ||
                      anchorContinuity.FallbackAnchors > 0 || anchorContinuity.PriorValidatedAnchors > 0 ||
                      trustSplitV018.StationaryRiskFrames > 0) &&
                     string.Equals(status, "clean", StringComparison.OrdinalIgnoreCase))
            {
                status = "partially-repaired";
                confidence = "medium";
            }
'@ `
  -Marker 'ShareXModV018TransitionTelemetry transitionV018 ='

Replace-Literal -Path $qualitySummary `
  -Old @'
                },
                delayedCompositor = new
                {
'@ `
  -New @'
                },
                transitionV018 = new
                {
                    transitionV018.NormalResolved,
                    transitionV018.GapHeld,
                    transitionV018.CatchUpResolved,
                    transitionV018.TerminalUnresolved,
                    transitionV018.LatestDelta,
                    transitionV018.LatestSource,
                    transitionV018.PendingGap
                },
                trustSplitV018 = new
                {
                    trustSplitV018.AppendCount,
                    trustSplitV018.RejectedAppendCount,
                    trustSplitV018.StationaryRiskFrames,
                    trustSplitV018.LatestScrollDelta,
                    trustSplitV018.LatestStationaryRiskRatio,
                    trustSplitV018.MaxStationaryRiskRatio,
                    trustSplitV018.PixelOverlayRepairEnabled
                },
                delayedCompositor = new
                {
'@ `
  -Marker 'trustSplitV018.PixelOverlayRepairEnabled'

Replace-Literal -Path $automation `
  -Old '        "ShareX.ScreenCaptureLib.ShareXModV017AnchorContinuitySelfTests",' `
  -New @'
        "ShareX.ScreenCaptureLib.ShareXModV017AnchorContinuitySelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV018TrustSplitSelfTests",
'@ `
  -Marker '"ShareX.ScreenCaptureLib.ShareXModV018TrustSplitSelfTests",'

# Structural invariants matter more than comments/tests that merely claim fail-closed behavior.
# Apply-chain CI and local CheckOnly both reject any generated manager that still exposes the old
# fallback or destructive v0.1.6 compositor as a live output path.
if (-not $CheckOnly) {
    $managerText = [IO.File]::ReadAllText($manager)
    foreach ($forbidden in @(
        'modRobustSession.TryFallbackCombine(',
        'modRobustSession?.ShouldContinueAfterCombineFailure()',
        'ShareXModDelayedCompositorV016.TryAppendLive('
    )) {
        if ($managerText.Contains($forbidden)) { throw "v0.1.8 structural invariant failed; generated manager still contains: $forbidden" }
    }
}

if ($CheckOnly) { Write-Host "LongCapture v0.1.8 trust-split compatibility passed." -ForegroundColor Green }
else { Write-Host "LongCapture v0.1.8 trust-split hooks applied." -ForegroundColor Green }
