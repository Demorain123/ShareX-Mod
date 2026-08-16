[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$resolver = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTransitionResolverV019.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
foreach ($path in @($resolver, $automation)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "RC5 target missing: $path" }
}

$resolverText = [IO.File]::ReadAllText($resolver)
$oldHelper = @'
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
'@
$newHelper = @'
    internal static bool ShouldFullRangeVetoValidatedPrior(
        int fullDelta, int priorDelta, bool hasNear, int nearDelta)
    {
        if (fullDelta <= 0 || priorDelta <= 0) return false;
        if (AreIndependentCandidatesConsistent(fullDelta, priorDelta)) return false;

        bool nearCorroboratesPrior = hasNear &&
            AreIndependentCandidatesConsistent(nearDelta, priorDelta);

        // RC5 real evidence: after 24 stable ~748-752px transitions the exact 750px prior
        // validated essentially perfectly and the restricted near-prior search independently
        // returned 750px, while the full-range search preferred a much worse 396px repeated-
        // content alias. RC4 made corroboration authoritative only for upward aliases and still
        // let any shorter full-range conflict veto the validated prior, causing an unnecessary
        // terminal stop at frame 25. A genuinely shorter movement already has an earlier escape
        // hatch in ShouldShortGlobalOverrideValidatedPrior, which requires the short candidate to
        // be materially better than the prior. Therefore, once that strict short-override test has
        // failed, a conflicting full-range candidate in either direction cannot veto a validated
        // prior when the independent near-prior search corroborates the prior.
        return !nearCorroboratesPrior;
    }
'@

if ($resolverText.Contains('RC5 real evidence: after 24 stable ~748-752px transitions')) {
    Write-Host "[v0.1.9-rc5] already present: bidirectional corroborated-prior veto guard" -ForegroundColor DarkYellow
}
else {
    if (-not $resolverText.Contains($oldHelper.TrimStart("`r", "`n"))) {
        throw "RC5 resolver helper anchor missing; RC4 must be applied first."
    }
    if (-not $CheckOnly) {
        $resolverText = $resolverText.Replace(
            $oldHelper.TrimStart("`r", "`n"),
            $newHelper.TrimStart("`r", "`n"))
    }
}

$oldComment = @'
                // A robust prior score can itself be a repeated-pattern alias, so disagreement still
                // matters. RC4 makes the upward case evidence-aware rather than absolute: a larger
                // full-range candidate cannot veto a validated prior when a restricted near-prior
                // search independently corroborates that prior. Without that corroboration we still
                // fail closed, preserving the ambiguous-short regression from the confidence loop.
'@
$newComment = @'
                // A robust prior score can itself be a repeated-pattern alias, so disagreement still
                // matters. RC5 makes the rule symmetric and evidence-aware: after the strict short-
                // movement override above has failed, a conflicting full-range candidate in either
                // direction cannot veto a validated prior when a restricted near-prior search also
                // corroborates that prior. Without near-prior corroboration we still fail closed.
'@
if (-not $CheckOnly -and $resolverText.Contains($oldComment.TrimStart("`r", "`n"))) {
    $resolverText = $resolverText.Replace(
        $oldComment.TrimStart("`r", "`n"),
        $newComment.TrimStart("`r", "`n"))
}

$automationText = [IO.File]::ReadAllText($automation)
$suiteMarker = '"ShareX.ScreenCaptureLib.ShareXModV019Rc5RealEvidenceSelfTests",'
if ($automationText.Contains($suiteMarker)) {
    Write-Host "[v0.1.9-rc5] already present: RC5 real-evidence suite" -ForegroundColor DarkYellow
}
else {
    $suiteAnchor = '        "ShareX.ScreenCaptureLib.ShareXModV019Rc4RealEvidenceSelfTests",'
    $suiteReplacement = @'
        "ShareX.ScreenCaptureLib.ShareXModV019Rc4RealEvidenceSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV019Rc5RealEvidenceSelfTests",
'@
    if (-not $automationText.Contains($suiteAnchor)) {
        throw "RC5 automation anchor missing; RC4 suite must be installed first."
    }
    if (-not $CheckOnly) {
        $automationText = $automationText.Replace($suiteAnchor, $suiteReplacement.TrimEnd("`r", "`n"))
    }
}

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.9 RC5 downward-alias consensus compatibility passed." -ForegroundColor Green
    exit 0
}

[IO.File]::WriteAllText($resolver, $resolverText, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText($automation, $automationText, [Text.UTF8Encoding]::new($true))
Write-Host "LongCapture v0.1.9 RC5 bidirectional corroborated-prior alias guard applied." -ForegroundColor Green
