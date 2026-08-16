#nullable enable

using System;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// RC5 regressions derived from the user's real v0.1.9-rc4 Linux.do capture without embedding user
/// pixels. RC4 successfully resolved 24 consecutive transitions clustered at ~748-752px. On the
/// next no-direct transition the exact temporal prior and the restricted near-prior search both
/// supported 750px, while full-range preferred a 396px repeated-content alias. The 396px score was
/// not good enough to pass the strict short-movement override, yet RC4 still allowed any shorter
/// conflict to veto the validated prior and terminated the capture.
///
/// RC5 closes only that logical gap. If the strict short-movement override did not already win and
/// near-prior independently corroborates the validated prior, a conflicting full-range candidate in
/// either direction is treated as an alias. Without near-prior corroboration the resolver remains
/// fail-closed, preserving the old ambiguous-short and upward-alias protections.
/// </summary>
internal static class ShareXModV019Rc5RealEvidenceSelfTests
{
    public static string RunOrThrow()
    {
        VerifyCorroboratedDownwardAliasCannotVetoValidatedPrior();
        VerifyCorroboratedUpwardAliasStillCannotVetoValidatedPrior();
        VerifyUncorroboratedDownwardConflictStillFailsClosed();
        VerifyUncorroboratedUpwardConflictStillFailsClosed();
        VerifyNearPriorAgreementRemainsNonConflict();
        return "v0.1.9 rc5 real-evidence passed: 750-prior plus 750-near defeats false 396 downward alias, corroborated upward alias remains blocked, uncorroborated conflicts still fail closed, premature frame-25 stop regression blocked.";
    }

    private static void VerifyCorroboratedDownwardAliasCannotVetoValidatedPrior()
    {
        // Exact RC4 failure signature: the stable prior and restricted search independently agree
        // at 750px while a repeated-content full-range search reports a spurious 396px movement.
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 396, priorDelta: 750, hasNear: true, nearDelta: 750))
        {
            throw new InvalidOperationException(
                "RC5 regression: the real 396px downward alias vetoed the corroborated 750px prior.");
        }
    }

    private static void VerifyCorroboratedUpwardAliasStillCannotVetoValidatedPrior()
    {
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 1068, priorDelta: 752, hasNear: true, nearDelta: 737))
        {
            throw new InvalidOperationException(
                "RC5 regression: RC4's corroborated upward-alias protection was lost.");
        }
    }

    private static void VerifyUncorroboratedDownwardConflictStillFailsClosed()
    {
        // A truly ambiguous shorter candidate must not silently choose the prior. A genuine short
        // terminal move is handled earlier by ShouldShortGlobalOverrideValidatedPrior when its score
        // is materially better; reaching this helper means that strict escape did not win.
        if (!ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 120, priorDelta: 750, hasNear: true, nearDelta: 610))
        {
            throw new InvalidOperationException(
                "RC5 regression: an uncorroborated downward conflict stopped failing closed.");
        }
    }

    private static void VerifyUncorroboratedUpwardConflictStillFailsClosed()
    {
        if (!ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 620, priorDelta: 260, hasNear: true, nearDelta: 300))
        {
            throw new InvalidOperationException(
                "RC5 regression: RC4's uncorroborated upward conflict protection was lost.");
        }
    }

    private static void VerifyNearPriorAgreementRemainsNonConflict()
    {
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 748, priorDelta: 750, hasNear: true, nearDelta: 750))
        {
            throw new InvalidOperationException(
                "RC5 regression: a normal near-prior full-range result was treated as conflict.");
        }
    }
}
