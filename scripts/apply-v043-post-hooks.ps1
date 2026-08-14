[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
if (-not (Test-Path -LiteralPath $manager)) {
    throw "ScrollingCaptureManager.cs not found."
}

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $text = [IO.File]::ReadAllText($manager)

    if ($text.Contains($Marker)) {
        Write-Host "[v0.4.3-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "v0.4.3 compatibility check failed: '$Marker' anchor not found."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.4.3-hook] applied: $Marker" -ForegroundColor Cyan
}

# Capture Boundary observes the same session without changing manual Start/Stop defaults.
Replace-Literal `
    -Old @'
                ShareXModCaptureQualitySession modQualityGuard = ShareXModCaptureQualityGuard.TryCreate(selectedRectangle);
                ShareXModSegmentStore modSegmentStore = ShareXModSegmentStore.TryCreate(modV04);
'@ `
    -New @'
                ShareXModCaptureQualitySession modQualityGuard = ShareXModCaptureQualityGuard.TryCreate(selectedRectangle);
                ShareXModCaptureBoundarySession modBoundary = ShareXModCaptureBoundary.TryCreate(modV04, selectedRectangle);
                ShareXModSegmentStore modSegmentStore = ShareXModSegmentStore.TryCreate(modV04);
'@ `
    -Marker "ShareXModCaptureBoundarySession modBoundary"

# An unchanged frame is useful evidence for an optional infinite-scroll/no-new-content boundary.
Replace-Literal `
    -Old @'
                        if (CompareLastTwoImages())
                        {
                            if (modRobustSession?.ShouldStopOnUnchangedFrame() ?? true)
                            {
                                break;
                            }
                        }
                        else
                        {
                            modRobustSession?.OnChangedFrame();
                        }
'@ `
    -New @'
                        if (CompareLastTwoImages())
                        {
                            modBoundary?.OnUnchangedFrame();

                            if (modBoundary?.StopRequested == true)
                            {
                                stopRequested = true;
                                break;
                            }

                            if (modRobustSession?.ShouldStopOnUnchangedFrame() ?? true)
                            {
                                break;
                            }
                        }
                        else
                        {
                            modBoundary?.OnChangedFrame();
                            modRobustSession?.OnChangedFrame();
                        }
'@ `
    -Marker "modBoundary?.OnUnchangedFrame();"

# Count only accepted appended content as a capture step. This keeps the output length deterministic
# even though ShareX's legacy loop sends the next scroll before the current frame is committed.
Replace-Literal `
    -Old @'
                                    modQualityGuard?.OnAppend(
                                        modEstimatedDelta,
                                        modActualGrowth,
                                        modHasAnchor ? modAnchor.Score : -1,
                                        modHasAnchor ? modAnchor.AgreementCount : 0,
                                        fallbackCombined,
                                        modRepairedOverlayTiles,
                                        lastScreenshot);
'@ `
    -New @'
                                    modQualityGuard?.OnAppend(
                                        modEstimatedDelta,
                                        modActualGrowth,
                                        modHasAnchor ? modAnchor.Score : -1,
                                        modHasAnchor ? modAnchor.AgreementCount : 0,
                                        fallbackCombined,
                                        modRepairedOverlayTiles,
                                        lastScreenshot);

                                    modBoundary?.OnAppend(modActualGrowth);
                                    if (modBoundary?.StopRequested == true)
                                    {
                                        stopRequested = true;
                                    }
'@ `
    -Marker "modBoundary?.OnAppend(modActualGrowth);"

# Automatic boundary reasons must not be mislabeled as a manual hotkey stop.
Replace-Literal `
    -Old '                    string modEndReason = stopRequested ? "manual-stop" : "capture-ended";' `
    -New @'
                    string modEndReason =
                        modBoundary?.StopReason ??
                        (stopRequested ? "manual-stop" : "capture-ended");
'@ `
    -Marker "modBoundary?.StopReason ??"

# Finalize the passive boundary report and turn Quality Guard suspect ranges into a repair plan.
Replace-Literal `
    -Old @'
                    modRobustSession?.Complete(modEndReason, status, Result);
                    modQualityGuard?.Complete(status, Result);
'@ `
    -New @'
                    modRobustSession?.Complete(modEndReason, status, Result);
                    modQualityGuard?.Complete(status, Result);
                    modBoundary?.Complete(modEndReason, status, Result);
                    ShareXModRepairPlanner.TryWriteLatestPlan(modV04);
'@ `
    -Marker "ShareXModRepairPlanner.TryWriteLatestPlan(modV04);"

Replace-Literal `
    -Old @'
                        modQualityGuard?.Dispose();
'@ `
    -New @'
                        modQualityGuard?.Dispose();
                        modBoundary?.Dispose();
'@ `
    -Marker "modBoundary?.Dispose();"

Write-Host "ShareX-Mod v0.4.3 post hooks applied." -ForegroundColor Green
