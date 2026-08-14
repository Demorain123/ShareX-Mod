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
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $text = [IO.File]::ReadAllText($Path)

    if ($text.Contains($Marker)) {
        Write-Host "[v0.4.2-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "v0.4.2 compatibility check failed: '$Marker' anchor not found."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.4.2-hook] applied: $Marker" -ForegroundColor Cyan
}

# Capture Map / Quality Guard session.
Replace-Literal -Path $manager `
    -Old @'
                modRobustSession = ShareXModRobustScrollingSession.TryCreate(selectedRectangle, Options);
                ShareXModSegmentStore modSegmentStore = ShareXModSegmentStore.TryCreate(modV04);
'@ `
    -New @'
                modRobustSession = ShareXModRobustScrollingSession.TryCreate(selectedRectangle, Options);
                ShareXModCaptureQualitySession modQualityGuard = ShareXModCaptureQualityGuard.TryCreate(selectedRectangle);
                ShareXModSegmentStore modSegmentStore = ShareXModSegmentStore.TryCreate(modV04);
'@ `
    -Marker "ShareXModCaptureQualitySession modQualityGuard"

# Give the ledger a frame count and initial logical range.
Replace-Literal -Path $manager `
    -Old @'
                        modRobustSession?.OnFrameCaptured(lastScreenshot);
'@ `
    -New @'
                        modRobustSession?.OnFrameCaptured(lastScreenshot);
                        modQualityGuard?.OnFrameCaptured(lastScreenshot);
'@ `
    -Marker "modQualityGuard?.OnFrameCaptured(lastScreenshot);"

# Estimate the page movement before combining. Use the same movement evidence to remove repeated
# fixed/sticky edge chrome from the frame that is actually written into the long image.
Replace-Literal -Path $manager `
    -Old @'
                            Bitmap newResult = await CombineImagesAsync(Result, lastScreenshot);
'@ `
    -New @'
                            Bitmap modCombineImage = lastScreenshot;
                            Bitmap modCleanedImage = null;
                            ShareXModAnchorMatch modAnchor = default;
                            bool modHasAnchor =
                                modRobustSession != null &&
                                previousScreenshot != null &&
                                ShareXModAnchorMatcher.TryEstimateScrollDelta(
                                    previousScreenshot,
                                    lastScreenshot,
                                    out modAnchor);

                            int modRepairedOverlayTiles = 0;

                            if (modHasAnchor)
                            {
                                ShareXModOverlayCleanResult? modOverlayClean =
                                    ShareXModStaticOverlayCleaner.TryClean(
                                        previousScreenshot,
                                        lastScreenshot,
                                        modAnchor.ScrollDelta);

                                if (modOverlayClean is ShareXModOverlayCleanResult cleaned)
                                {
                                    modCleanedImage = cleaned.Image;
                                    modCombineImage = cleaned.Image;
                                    modRepairedOverlayTiles = cleaned.RepairedTiles;
                                }
                            }

                            Bitmap newResult = await CombineImagesAsync(Result, modCombineImage);
'@ `
    -Marker "ShareXModStaticOverlayCleaner.TryClean"

# v0.4.1 already performs an independent growth cross-check. Reuse the precomputed anchor rather
# than running the anchor matcher a second time.
Replace-Literal -Path $manager `
    -Old @'
                            if (newResult != null && modRobustSession != null && previousScreenshot != null &&
                                ShareXModAnchorMatcher.TryEstimateScrollDelta(previousScreenshot, lastScreenshot, out ShareXModAnchorMatch modAnchor))
'@ `
    -New @'
                            if (newResult != null && modHasAnchor)
'@ `
    -Marker "if (newResult != null && modHasAnchor)"

# The fallback compositor should also consume the cleaned frame when a safe repair was produced.
Replace-Literal -Path $manager `
    -Old @'
                                newResult = modRobustSession.TryFallbackCombine(Result, previousScreenshot, lastScreenshot);
'@ `
    -New @'
                                newResult = modRobustSession.TryFallbackCombine(Result, previousScreenshot, modCombineImage);
'@ `
    -Marker "TryFallbackCombine(Result, previousScreenshot, modCombineImage)"

# Record the accepted document range before replacing Result.
Replace-Literal -Path $manager `
    -Old @'
                            if (newResult != null)
                            {
                                if (!fallbackCombined)
'@ `
    -New @'
                            modCleanedImage?.Dispose();
                            modCleanedImage = null;

                            if (newResult != null)
                            {
                                if (Result != null)
                                {
                                    int modActualGrowth = newResult.Height - Result.Height;
                                    int modEstimatedDelta = modHasAnchor ? modAnchor.ScrollDelta : modActualGrowth;

                                    modQualityGuard?.OnAppend(
                                        modEstimatedDelta,
                                        modActualGrowth,
                                        modHasAnchor ? modAnchor.Score : -1,
                                        modHasAnchor ? modAnchor.AgreementCount : 0,
                                        fallbackCombined,
                                        modRepairedOverlayTiles,
                                        lastScreenshot);
                                }

                                if (!fallbackCombined)
'@ `
    -Marker "modQualityGuard?.OnAppend("

# Replace the v0.4 settle call with the lazy-load-aware v0.4.2 probe. It compares the new viewport
# against the pre-scroll frame only at low resolution and extends waiting only for suspicious blanks.
Replace-Literal -Path $manager `
    -Old @'
                            await ShareXModAdaptiveSettle.WaitAsync(
                                () => screenshot.CaptureRectangle(selectedRectangle),
                                modV04,
                                Options.ScrollDelay);
'@ `
    -New @'
                            ShareXModSettleResult modSettleResult =
                                await ShareXModAdaptiveSettleV042.WaitAsync(
                                    () => screenshot.CaptureRectangle(selectedRectangle),
                                    previousScreenshot,
                                    modV04,
                                    Options.ScrollDelay);

                            modQualityGuard?.OnSettle(modSettleResult);
'@ `
    -Marker "ShareXModAdaptiveSettleV042.WaitAsync"

# Final report is written before resources are disposed.
Replace-Literal -Path $manager `
    -Old @'
                    modRobustSession?.Complete(modEndReason, status, Result);
'@ `
    -New @'
                    modRobustSession?.Complete(modEndReason, status, Result);
                    modQualityGuard?.Complete(status, Result);
'@ `
    -Marker "modQualityGuard?.Complete(status, Result);"

Replace-Literal -Path $manager `
    -Old @'
                        modRobustSession?.Dispose();
                        modRobustSession = null;
'@ `
    -New @'
                        modRobustSession?.Dispose();
                        modRobustSession = null;
                        modQualityGuard?.Dispose();
'@ `
    -Marker "modQualityGuard?.Dispose();"

# Preserve native appendix pixels by routing the existing tail hook through the v0.4.2 slicer.
Replace-Literal -Path $manager `
    -Old "ShareXModImageAppendixTail.Build(modSegmentStore.DirectoryPath, appendixWidth)" `
    -New "ShareXModImageAppendixTailV042.Build(modSegmentStore.DirectoryPath, appendixWidth)" `
    -Marker "ShareXModImageAppendixTailV042.Build"

Write-Host "ShareX-Mod v0.4.2 post hooks applied." -ForegroundColor Green
