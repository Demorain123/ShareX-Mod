#nullable enable

using System;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModTemplateRouterEvidenceSelfTests
{
    public static string RunOrThrow()
    {
        ShareXModRouterEvidenceScore familyA = ShareXModTemplateRouterEvidenceScorer.Score(new[]
        {
            new ShareXModRouterProbeObservation("next", true, true, true, 4, 120, 80, "resolved"),
            new ShareXModRouterProbeObservation("range-start", true, true, false, 1, 100, 40, "resolved"),
            new ShareXModRouterProbeObservation("range-end", true, true, false, 1, 96, 36, "resolved")
        });

        ShareXModRouterEvidenceScore familyB = ShareXModTemplateRouterEvidenceScorer.Score(new[]
        {
            new ShareXModRouterProbeObservation("next", true, true, true, 4, 120, 80, "resolved"),
            new ShareXModRouterProbeObservation("range-start", false, false, false, 1, 0, 0, "missing"),
            new ShareXModRouterProbeObservation("range-end", false, false, false, 1, 0, 0, "missing")
        });

        Assert(familyA.Valid && familyB.Valid,
            "missing optional range anchors must not invalidate an otherwise valid family");
        Assert(familyA.Score > familyB.Score + 0.20,
            $"matching range placeholders should create useful family separation: A={familyA.Score:0.000}, B={familyB.Score:0.000}");

        ShareXModRouterEvidenceScore unsafeFamily = ShareXModTemplateRouterEvidenceScorer.Score(new[]
        {
            new ShareXModRouterProbeObservation("next", false, false, true, 4, 0, 0, "missing"),
            new ShareXModRouterProbeObservation("range-start", true, true, false, 1, 100, 40, "resolved")
        });
        Assert(!unsafeFamily.Valid,
            "range evidence must never rescue a family whose required Next/action locator is unresolved");

        return "ShareX-Mod range-aware router evidence self-tests passed: 2";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ShareX-Mod router-evidence self-test failed: " + message);
    }
}
