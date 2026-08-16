[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$resolver = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTransitionResolverV019.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
foreach ($path in @($resolver, $automation)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "RC4 target missing: $path" }
}

$resolverText = [IO.File]::ReadAllText($resolver)
$oldDecision = '                bool fullDisagreesWithPrior = hasFull && !AreIndependentCandidatesConsistent(fullDelta, prior);'
$newDecision = '                bool fullDisagreesWithPrior = hasFull && ShouldFullRangeVetoValidatedPrior(fullDelta, prior);'
if ($resolverText.Contains($newDecision)) {
    Write-Host "[v0.1.9-rc4] already present: asymmetric full-range veto" -ForegroundColor DarkYellow
}
else {
    if (-not $resolverText.Contains($oldDecision)) { throw "RC4 resolver decision anchor missing." }
    $resolverText = $resolverText.Replace($oldDecision, $newDecision)
}

$helperMarker = '    internal static bool ShouldFullRangeVetoValidatedPrior(int fullDelta, int priorDelta)'
if (-not $resolverText.Contains($helperMarker)) {
    $helperAnchor = @'
    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)
'@
    $helper = @'
    // RC4 real evidence: with no direct anchor, the full-range search can prefer a much larger
    // repeated-content alias simply because the larger displacement has less overlap to score.
    // Such an upward candidate is already forbidden from being accepted without corroboration;
    // therefore it is not independent negative evidence against a strongly validated temporal prior.
    // Same-size/shorter conflicting candidates remain able to veto the prior, preserving the
    // ambiguous-short fail-closed regression introduced in RC3.
    internal static bool ShouldFullRangeVetoValidatedPrior(int fullDelta, int priorDelta)
    {
        if (!IsSafeUncorroboratedPriorCandidate(fullDelta, priorDelta)) return false;
        return !AreIndependentCandidatesConsistent(fullDelta, priorDelta);
    }

    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)
'@
    if (-not $resolverText.Contains($helperAnchor.TrimStart("`r", "`n"))) { throw "RC4 resolver helper anchor missing." }
    $resolverText = $resolverText.Replace($helperAnchor.TrimStart("`r", "`n"), $helper.TrimStart("`r", "`n"))
}

# Tighten the explanatory comment so the production invariant is reviewable from source.
$oldComment = @'
                // A robust prior score can itself be a repeated-pattern alias. If an independent
                // full-range search points to a materially different displacement and did not earn
                // the strict short-override rule above, the evidence is ambiguous. Do not silently
                // choose the temporal prior merely because both scores happen to be below threshold.
'@
$newComment = @'
                // A robust prior score can itself be a repeated-pattern alias, so a materially
                // different same-size/shorter full-range candidate may veto it. RC4 makes this
                // asymmetric: a larger uncorroborated full-range candidate is already unsafe to
                // accept and, because its reduced overlap can produce deceptively low scores, it
                // cannot by itself veto a strongly validated temporal prior.
'@
if ($resolverText.Contains($oldComment.TrimStart("`r", "`n"))) {
    $resolverText = $resolverText.Replace($oldComment.TrimStart("`r", "`n"), $newComment.TrimStart("`r", "`n"))
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
    Write-Host "LongCapture v0.1.9 RC4 upward-alias veto compatibility passed." -ForegroundColor Green
    exit 0
}

[IO.File]::WriteAllText($resolver, $resolverText, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText($automation, $automationText, [Text.UTF8Encoding]::new($true))
Write-Host "LongCapture v0.1.9 RC4 upward-alias veto applied." -ForegroundColor Green
