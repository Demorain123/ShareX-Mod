[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$resolver = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTransitionResolverV019.cs"
$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
foreach ($path in @($resolver, $compositor, $automation)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "RC4 target missing: $path" }
}

$resolverText = [IO.File]::ReadAllText($resolver)
$oldDecision = '                bool fullDisagreesWithPrior = hasFull && !AreIndependentCandidatesConsistent(fullDelta, prior);'
$newDecision = @'
                bool hasNearForVeto = ShareXModVerticalFallbackMatcher.TryEstimateNearDelta(
                    previousReliable, current, settings, prior, out int nearDeltaForVeto, out _);
                bool fullDisagreesWithPrior = hasFull && ShouldFullRangeVetoValidatedPrior(
                    fullDelta, prior, hasNearForVeto, nearDeltaForVeto);
'@
if ($resolverText.Contains('bool hasNearForVeto = ShareXModVerticalFallbackMatcher.TryEstimateNearDelta(')) {
    Write-Host "[v0.1.9-rc4] already present: corroborated asymmetric full-range veto" -ForegroundColor DarkYellow
}
else {
    if (-not $resolverText.Contains($oldDecision)) { throw "RC4 resolver decision anchor missing." }
    $resolverText = $resolverText.Replace($oldDecision, $newDecision.TrimEnd("`r", "`n"))
}

$helperMarker = '    internal static bool ShouldFullRangeVetoValidatedPrior(int fullDelta, int priorDelta, bool hasNear, int nearDelta)'
if (-not $resolverText.Contains($helperMarker)) {
    $helperAnchor = '    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)'
    $helper = @'
    // RC4 real evidence: a full-range search can prefer a much larger repeated-content alias because
    // the larger displacement scores fewer overlap rows. Do not blindly ignore that disagreement:
    // only suppress an upward full-range veto when the restricted near-prior search independently
    // corroborates the validated prior. Same-size/shorter conflicts continue to veto, and an upward
    // conflict with no near-prior corroboration also remains fail-closed. This preserves the older
    // ambiguous-short regression while fixing the real RC3 752-prior / 737-near / 1068-full failure.
    internal static bool ShouldFullRangeVetoValidatedPrior(
        int fullDelta, int priorDelta, bool hasNear, int nearDelta)
    {
        if (fullDelta <= 0 || priorDelta <= 0) return false;
        if (AreIndependentCandidatesConsistent(fullDelta, priorDelta)) return false;

        if (fullDelta > priorDelta)
        {
            bool nearCorroboratesPrior = hasNear &&
                AreIndependentCandidatesConsistent(nearDelta, priorDelta);
            return !nearCorroboratesPrior;
        }

        return true;
    }

    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)
'@
    if (-not $resolverText.Contains($helperAnchor)) { throw "RC4 resolver helper anchor missing." }
    $resolverText = $resolverText.Replace($helperAnchor, $helper.TrimEnd("`r", "`n"))
}

$oldComment = @'
                // A robust prior score can itself be a repeated-pattern alias. If an independent
                // full-range search points to a materially different displacement and did not earn
                // the strict short-override rule above, the evidence is ambiguous. Do not silently
                // choose the temporal prior merely because both scores happen to be below threshold.
'@
$newComment = @'
                // A robust prior score can itself be a repeated-pattern alias, so disagreement still
                // matters. RC4 makes the upward case evidence-aware rather than absolute: a larger
                // full-range candidate cannot veto a validated prior when a restricted near-prior
                // search independently corroborates that prior. Without that corroboration we still
                // fail closed, preserving the ambiguous-short regression from the confidence loop.
'@
if ($resolverText.Contains($oldComment.TrimStart("`r", "`n"))) {
    $resolverText = $resolverText.Replace($oldComment.TrimStart("`r", "`n"), $newComment.TrimStart("`r", "`n"))
}

$compositorText = [IO.File]::ReadAllText($compositor)
$oldSourceGuard = @'
                double sourceStationary = MeanAbsoluteError(
                    previous, x0, sourceY0,
                    current, x0, sourceY0,
                    repairWidth, repairHeight);
                if (sourceStationary <= 10.0) continue;
'@
$newSourceGuard = @'
                double sourceStationary = MeanAbsoluteError(
                    previous, x0, sourceY0,
                    current, x0, sourceY0,
                    repairWidth, repairHeight);

                // RC4 real evidence: a bottom-right fixed control can cover a visually quiet/blank
                // document region. In that case same-coordinate source MAE can be low even though the
                // source pixels are exactly the newly revealed document content we need. Requiring
                // sourceStationary > 10 forever stamps the control into every committed tail. Bypass
                // that heuristic only for repeatedly confirmed components that are simultaneously at
                // the right AND bottom edges; right-edge-only sidebars retain the conservative guard.
                bool bottomRightConfirmed = rightEdge && bottomEdge;
                if (!bottomRightConfirmed && sourceStationary <= 10.0) continue;
'@
if ($compositorText.Contains('bool bottomRightConfirmed = rightEdge && bottomEdge;')) {
    Write-Host "[v0.1.9-rc4] already present: quiet-source bottom-right repair" -ForegroundColor DarkYellow
}
else {
    if (-not $compositorText.Contains($oldSourceGuard.TrimStart("`r", "`n"))) { throw "RC4 compositor source-guard anchor missing." }
    $compositorText = $compositorText.Replace($oldSourceGuard.TrimStart("`r", "`n"), $newSourceGuard.TrimStart("`r", "`n"))
}

$automationText = [IO.File]::ReadAllText($automation)
$suiteMarker = '"ShareX.ScreenCaptureLib.ShareXModV019Rc4RealEvidenceSelfTests",'
if ($automationText.Contains($suiteMarker)) {
    Write-Host "[v0.1.9-rc4] already present: RC4 real-evidence suite" -ForegroundColor DarkYellow
}
else {
    $suiteAnchor = '        "ShareX.ScreenCaptureLib.ShareXModV019Rc3RealEvidenceSelfTests",'
    $suiteReplacement = @'
        "ShareX.ScreenCaptureLib.ShareXModV019Rc3RealEvidenceSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV019Rc4RealEvidenceSelfTests",
'@
    if (-not $automationText.Contains($suiteAnchor)) { throw "RC4 automation anchor missing; RC3 suite must be installed first." }
    $automationText = $automationText.Replace($suiteAnchor, $suiteReplacement.TrimEnd("`r", "`n"))
}

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.9 RC4 corroborated-upward-alias/fixed-control compatibility passed." -ForegroundColor Green
    exit 0
}

[IO.File]::WriteAllText($resolver, $resolverText, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText($compositor, $compositorText, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText($automation, $automationText, [Text.UTF8Encoding]::new($true))
Write-Host "LongCapture v0.1.9 RC4 corroborated upward-alias veto + bottom-right fixed-control repair applied." -ForegroundColor Green
