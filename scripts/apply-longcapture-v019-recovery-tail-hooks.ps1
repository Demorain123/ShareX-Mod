[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.9 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.1.9] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.9 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old,$New)
        [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.9] applied/compatible: $Marker" -ForegroundColor Cyan
}

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
$qualitySummary = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModFinalQualitySummary.cs"
$replayDiagnostics = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModReplayDiagnostics.cs"

# Reset v0.1.9 state together with the previous diagnostic generations.
Replace-Literal -Path $manager `
  -Old @'
                ShareXModTransitionResolverV018.ResetLive();
                ShareXModTrustSplitCompositorV018.ResetLive();
'@ `
  -New @'
                ShareXModTransitionResolverV018.ResetLive();
                ShareXModTrustSplitCompositorV018.ResetLive();
                ShareXModTransitionResolverV019.ResetLive();
                ShareXModTrustSplitCompositorV019.ResetLive();
'@ `
  -Marker 'ShareXModTransitionResolverV019.ResetLive();'

# v0.1.9 RC1 keeps the proven loop timing from v0.1.8 but replaces the geometry authority. The
# resolver can now recover the exact short final movement seen in the user's evidence, so the unsafe
# v0.1.8 two-step catch-up is not needed in this RC.
Replace-Literal -Path $manager `
  -Old '                        bool modV018HoldReliableReference = false;' `
  -New '                        bool modV019HoldReliableReference = false; // v0.1.9 RC1 remains fail-closed; full-range recovery happens before this can stop.' `
  -Marker 'bool modV019HoldReliableReference = false;'

# Promote v0.1.9 resolver/compositor and retain the resolver-selected delta as the sole growth truth.
Replace-Literal -Path $manager `
  -Old @'
                            int modRepairedOverlayTiles = 0;
                            bool modV016DelayedCompositorUsed = false;
                            Bitmap newResult = null;
'@ `
  -New @'
                            int modRepairedOverlayTiles = 0;
                            bool modV016DelayedCompositorUsed = false;
                            int modResolvedDelta = 0;
                            Bitmap newResult = null;
'@ `
  -Marker 'int modResolvedDelta = 0;'

Replace-Literal -Path $manager `
  -Old @'
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
  -New @'
                                bool modV019Resolved = ShareXModTransitionResolverV019.TryResolve(
                                    previousScreenshot,
                                    lastScreenshot,
                                    modHasAnchor,
                                    modAnchor,
                                    out int modV019Delta,
                                    out string modV019Source,
                                    out double modV019Score,
                                    out modV019HoldReliableReference);
                                ShareXModReplayDiagnostics.RecordResolution(
                                    modV019Resolved,
                                    modV019Delta,
                                    modV019Source,
                                    modV019Score);

                                if (modV019Resolved)
                                {
                                    modResolvedDelta = modV019Delta;
                                    ShareXModV019CompositorTelemetry modV019Before = ShareXModTrustSplitCompositorV019.SnapshotLiveTelemetry();
                                    newResult = ShareXModTrustSplitCompositorV019.TryAppendLive(
                                        Result,
                                        previousScreenshot,
                                        lastScreenshot,
                                        modV019Delta);
                                    ShareXModV019CompositorTelemetry modV019After = ShareXModTrustSplitCompositorV019.SnapshotLiveTelemetry();
                                    int repairedPixels = Math.Max(0, modV019After.TailRepairPixelsApprox - modV019Before.TailRepairPixelsApprox);
                                    modRepairedOverlayTiles = repairedPixels <= 0 ? 0 : Math.Max(1, repairedPixels / (48 * 36));
                                    modV016DelayedCompositorUsed = false;
                                }

                                if (newResult == null)
                                {
                                    status = ScrollingCaptureStatus.PartiallySuccessful;
                                }
'@ `
  -Marker 'bool modV019Resolved = ShareXModTransitionResolverV019.TryResolve('

Replace-Literal -Path $manager `
  -Old @'
                            if (newResult != null && modHasAnchor)
                            {
                                int actualGrowth = newResult.Height - Result.Height;
                                int expectedGrowth = modAnchor.ScrollDelta;
'@ `
  -New @'
                            if (newResult != null && modResolvedDelta > 0)
                            {
                                int actualGrowth = newResult.Height - Result.Height;
                                int expectedGrowth = modResolvedDelta;
'@ `
  -Marker 'if (newResult != null && modResolvedDelta > 0)'

Replace-Literal -Path $manager `
  -Old '                                    int modEstimatedDelta = modHasAnchor ? modAnchor.ScrollDelta : modActualGrowth;' `
  -New '                                    int modEstimatedDelta = modResolvedDelta > 0 ? modResolvedDelta : modActualGrowth;' `
  -Marker 'int modEstimatedDelta = modResolvedDelta > 0 ? modResolvedDelta : modActualGrowth;'

Replace-Literal -Path $manager `
  -Old '                            else if (!modV018HoldReliableReference)' `
  -New '                            else if (!modV019HoldReliableReference)' `
  -Marker 'else if (!modV019HoldReliableReference)'

Replace-Literal -Path $manager `
  -Old '                            if (modV018HoldReliableReference)' `
  -New '                            if (modV019HoldReliableReference)' `
  -Marker 'if (modV019HoldReliableReference)'

# QUICK must exercise the evidence-derived v0.1.9 geometry/tail path.
Replace-Literal -Path $automation `
  -Old '        "ShareX.ScreenCaptureLib.ShareXModV018TrustSplitSelfTests",' `
  -New @'
        "ShareX.ScreenCaptureLib.ShareXModV018TrustSplitSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",
'@ `
  -Marker '"ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",'

# v0.1.9 quality grading is owned by v0.1.9 geometry + provisional-tail telemetry. v0.1.8 remains
# present only as diagnostic context so older evidence can still be inspected.
Replace-Literal -Path $qualitySummary `
  -Old @'
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
  -New @'
            ShareXModV017AnchorTelemetry anchorContinuity = ShareXModAnchorContinuityV017.SnapshotTelemetry();
            ShareXModV018TransitionTelemetry transitionV018 = ShareXModTransitionResolverV018.SnapshotTelemetry(); // legacy diagnostic context
            ShareXModV018CompositorTelemetry trustSplitV018 = ShareXModTrustSplitCompositorV018.SnapshotLiveTelemetry(); // legacy diagnostic context
            ShareXModV019TransitionTelemetry transitionV019 = ShareXModTransitionResolverV019.SnapshotTelemetry();
            ShareXModV019CompositorTelemetry trustSplitV019 = ShareXModTrustSplitCompositorV019.SnapshotLiveTelemetry();
            if (transitionV019.TerminalUnresolved > 0 || trustSplitV019.RejectedAppendCount > 0)
            {
                status = "unresolved";
                confidence = "low";
            }
            else if ((transitionV019.FullRangeRecovered > 0 || transitionV019.OutlierDirectRejected > 0 ||
                      trustSplitV019.TailRepairComponents > 0 || trustSplitV019.StationaryRiskFrames > 0) &&
                     string.Equals(status, "clean", StringComparison.OrdinalIgnoreCase))
            {
                status = "partially-repaired";
                confidence = "medium";
            }
'@ `
  -Marker 'ShareXModV019TransitionTelemetry transitionV019 ='

Replace-Literal -Path $qualitySummary `
  -Old @'
                transitionV018 = new
                {
'@ `
  -New @'
                transitionV019 = new
                {
                    transitionV019.DirectAccepted,
                    transitionV019.PriorResolved,
                    transitionV019.OutlierDirectRejected,
                    transitionV019.FullRangeRecovered,
                    transitionV019.RetryHeld,
                    transitionV019.RetryResolved,
                    transitionV019.TerminalUnresolved,
                    transitionV019.LatestDelta,
                    transitionV019.LatestSource,
                    transitionV019.PendingRetry
                },
                trustSplitV019 = new
                {
                    trustSplitV019.AppendCount,
                    trustSplitV019.RejectedAppendCount,
                    trustSplitV019.StationaryRiskFrames,
                    trustSplitV019.TailRepairComponents,
                    trustSplitV019.TailRepairPixelsApprox,
                    trustSplitV019.LatestScrollDelta,
                    trustSplitV019.LatestStationaryRiskRatio,
                    trustSplitV019.MaxStationaryRiskRatio,
                    trustSplitV019.CommittedBodyImmutable,
                    trustSplitV019.ProvisionalTailRepairEnabled
                },
                transitionV018 = new
                {
'@ `
  -Marker 'trustSplitV019.ProvisionalTailRepairEnabled'

# Offline replay must revalidate old recorded deltas instead of blindly trusting v0.1.7/v0.1.8
# resolution logs. This lets v0.1.9 reject the real 200/404px aliases found in the Linux.do bundle.
Replace-Literal -Path $replayDiagnostics `
  -Old '        using var replay = new ShareXModTrustSplitCompositorV018.Session();' `
  -New '        using var replay = new ShareXModTrustSplitCompositorV019.Session();' `
  -Marker 'using var replay = new ShareXModTrustSplitCompositorV019.Session();'

Replace-Literal -Path $replayDiagnostics `
  -Old @'
        ShareXModAnchorContinuityV017.ResetLive();
        ShareXModTransitionResolverV018.ResetLive();
'@ `
  -New @'
        ShareXModAnchorContinuityV017.ResetLive();
        ShareXModTransitionResolverV018.ResetLive();
        ShareXModTransitionResolverV019.ResetLive();
'@ `
  -Marker '        ShareXModTransitionResolverV019.ResetLive();'

Replace-Literal -Path $replayDiagnostics `
  -Old @'
                int delta = 0;
                bool holdReliableReference = false;
                if (recordedResolutions.TryGetValue(i, out int savedResolution) && savedResolution > 0)
                {
                    delta = savedResolution;
                }
                else
                {
                    bool hasSavedAnchor = recordedAnchors.TryGetValue(i, out int savedAnchor) && savedAnchor > 0;
                    ShareXModAnchorMatch replayAnchor = hasSavedAnchor
                        ? new ShareXModAnchorMatch(savedAnchor, 0, 3)
                        : default;
                    bool resolved = ShareXModTransitionResolverV018.TryResolve(
                        previous, current, hasSavedAnchor, replayAnchor,
                        out delta, out string replaySource, out double replayScore, out holdReliableReference);
                    if (!resolved)
                    {
                        if (holdReliableReference) continue;
                        throw new InvalidOperationException($"Replay could not safely resolve frame {i}; source={replaySource}, score={replayScore:F2}. Legacy mosaic fallback is disabled.");
                    }
                }
'@ `
  -New @'
                int delta = 0;
                bool holdReliableReference = false;
                bool hasSavedResolution = recordedResolutions.TryGetValue(i, out int savedResolution) && savedResolution > 0;
                bool hasSavedAnchor = recordedAnchors.TryGetValue(i, out int savedAnchor) && savedAnchor > 0;
                int recordedDelta = hasSavedResolution ? savedResolution : (hasSavedAnchor ? savedAnchor : 0);
                ShareXModAnchorMatch replayAnchor = recordedDelta > 0
                    ? new ShareXModAnchorMatch(recordedDelta, 0, hasSavedAnchor ? 3 : 2)
                    : default;
                bool resolved = ShareXModTransitionResolverV019.TryResolve(
                    previous, current, recordedDelta > 0, replayAnchor,
                    out delta, out string replaySource, out double replayScore, out holdReliableReference);
                if (!resolved)
                {
                    throw new InvalidOperationException($"Replay could not safely resolve frame {i}; source={replaySource}, score={replayScore:F2}. Legacy mosaic fallback is disabled.");
                }
'@ `
  -Marker 'bool hasSavedResolution = recordedResolutions.TryGetValue(i, out int savedResolution)'

Replace-Literal -Path $replayDiagnostics `
  -Old '            outputPath ??= Path.Combine(root, $"LongCapture-Replay-v018-{DateTime.Now:yyyyMMdd-HHmmss}.png");' `
  -New '            outputPath ??= Path.Combine(root, $"LongCapture-Replay-v019-{DateTime.Now:yyyyMMdd-HHmmss}.png");' `
  -Marker 'LongCapture-Replay-v019-'

Replace-Literal -Path $replayDiagnostics `
  -Old '                version = "0.1.8",' `
  -New '                version = "0.1.9",' `
  -Marker 'version = "0.1.9",'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.9 recovery-tail hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.9 recovery-tail hooks applied." -ForegroundColor Green
}
