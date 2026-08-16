#nullable enable

using System;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// RC4 regressions derived from the real v0.1.9-rc3 Linux.do failure without embedding user pixels.
/// The real sequence established a stable ~752px temporal prior, then lost the direct anchor. The
/// exact prior still validated strongly, but a larger ~1068px full-range alias won a shorter-overlap
/// search and was allowed to veto the prior, causing an immediate terminal unresolved stop.
///
/// The safety rule is intentionally asymmetric: an uncorroborated larger full-range candidate is
/// already forbidden from being accepted, so it also cannot veto a strongly validated temporal prior.
/// A materially different same-size/shorter candidate remains eligible to veto the prior; this keeps
/// the earlier ambiguous-short regression (120px true motion vs 260px prior) fail-closed.
/// </summary>
internal static class ShareXModV019Rc4RealEvidenceSelfTests
{
    public static string RunOrThrow()
    {
        VerifyUncorroboratedUpwardAliasCannotVetoValidatedPrior();
        VerifyShortConflictingCandidateStillVetoesPrior();
        VerifyNearPriorCandidateDoesNotCreateFalseDisagreement();
        return "v0.1.9 rc4 real-evidence passed: upward full-range alias cannot veto validated prior, short conflicting evidence still vetoes, near-prior agreement remains accepted, premature no-direct stop regression blocked.";
    }

    private static void VerifyUncorroboratedUpwardAliasCannotVetoValidatedPrior()
    {
        // Scaled directly from the rc3 evidence: stable prior ~= 752, spurious full-range ~= 1068.
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(1068, 752))
        {
            throw new InvalidOperationException(
                "RC4 regression: an uncorroborated larger full-range alias was allowed to veto a validated prior.");
        }
    }

    private static void VerifyShortConflictingCandidateStillVetoesPrior()
    {
        // This preserves the confidence-loop regression that forced RC3 to stop blindly trusting a
        // plausible prior: a real 120px short movement must be allowed to challenge a 260px prior.
        if (!ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(120, 260))
        {
            throw new InvalidOperationException(
                "RC4 regression: short conflicting evidence no longer vetoes a potentially aliased prior.");
        }
    }

    private static void VerifyNearPriorCandidateDoesNotCreateFalseDisagreement()
    {
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(748, 752))
        {
            throw new InvalidOperationException(
                "RC4 regression: a near-prior candidate was incorrectly treated as conflicting geometry.");
        }
    }
}
