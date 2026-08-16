#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// RC6 regressions derived from the user's real RC5 Linux.do run without embedding user pixels.
/// RC5 captured 45 raw viewports and successfully committed 43 transitions, but stopped on the
/// final 43->44 transition even though the page visibly moved. Sparse white content made the
/// fixed-phase matcher produce multiple deceptively perfect/near-perfect aliases. The same run also
/// accepted a 712px near-prior direct anchor inside an otherwise ~750px sequence, losing document
/// rows before the final stop.
///
/// RC6 adds two protections: restricted near-prior search is seeded with the prior so equal sparse
/// scores cannot drift to the first coarse phase, and a content-bearing edge-support metric can
/// prefer a validated prior when text/edge alignment decisively beats a competing candidate.
/// </summary>
internal static class ShareXModV019Rc6RealEvidenceSelfTests
{
    private const int Width = 1000;
    private const int Height = 720;
    private const int TrueDelta = 300;

    public static string RunOrThrow()
    {
        VerifyRealEvidenceDecisionThresholds();
        VerifySparseFixturePrefersTrueMotion();
        VerifyNearSearchCannotDriftOnSparseTie();
        VerifyNoisyNearDirectCannotDisplaceInformativePrior();
        return "v0.1.9 rc6 real-evidence passed: sparse-content informative edges distinguish true motion from blank aliases, near-prior equal-score drift is blocked, noisy 712-style near-direct geometry cannot displace a stronger validated prior, genuine short-motion evidence remains admissible.";
    }

    private static void VerifyRealEvidenceDecisionThresholds()
    {
        if (!ShareXModTransitionResolverV019.ShouldPreferInformativePrior(
                priorSupport: 0.63, priorSamples: 1693,
                candidateSupport: 0.17, candidateSamples: 2489))
        {
            throw new InvalidOperationException("RC6 regression: real-evidence-like 750 prior did not defeat a sparse alias.");
        }

        if (ShareXModTransitionResolverV019.ShouldPreferInformativePrior(
                priorSupport: 0.14, priorSamples: 1700,
                candidateSupport: 0.62, candidateSamples: 2200))
        {
            throw new InvalidOperationException("RC6 regression: informative genuine shorter motion was incorrectly forced back to the prior.");
        }

        if (ShareXModTransitionResolverV019.ShouldPreferInformativePrior(
                priorSupport: 0.90, priorSamples: 20,
                candidateSupport: 0.10, candidateSamples: 20))
        {
            throw new InvalidOperationException("RC6 regression: insufficient informative samples were treated as authoritative.");
        }
    }

    private static void VerifySparseFixturePrefersTrueMotion()
    {
        using Bitmap previous = BuildSparseViewport(scrollTop: 0);
        using Bitmap current = BuildSparseViewport(scrollTop: TrueDelta);

        if (!ShareXModVerticalFallbackMatcher.TryMeasureInformativeSupport(
                previous, current, TrueDelta, out double trueSupport, out int trueSamples))
            throw new InvalidOperationException("RC6 sparse fixture produced insufficient true-motion evidence.");

        const int falseAlias = 180;
        if (!ShareXModVerticalFallbackMatcher.TryMeasureInformativeSupport(
                previous, current, falseAlias, out double aliasSupport, out int aliasSamples))
            throw new InvalidOperationException("RC6 sparse fixture produced insufficient alias evidence for comparison.");

        if (!ShareXModTransitionResolverV019.ShouldPreferInformativePrior(
                trueSupport, trueSamples, aliasSupport, aliasSamples))
        {
            throw new InvalidOperationException(
                $"RC6 sparse fixture did not prefer true motion: true={trueSupport:F3}/{trueSamples}, alias={aliasSupport:F3}/{aliasSamples}.");
        }
    }

    private static void VerifyNearSearchCannotDriftOnSparseTie()
    {
        using Bitmap previous = BuildSparseViewport(scrollTop: 0);
        using Bitmap current = BuildSparseViewport(scrollTop: TrueDelta);
        ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
        if (!ShareXModVerticalFallbackMatcher.TryEstimateNearDelta(
                previous, current, settings, TrueDelta, out int nearDelta, out double nearScore))
        {
            throw new InvalidOperationException("RC6 sparse fixture near-prior search failed.");
        }

        if (Math.Abs(nearDelta - TrueDelta) > 2)
        {
            throw new InvalidOperationException(
                $"RC6 sparse tie drifted away from prior: expected={TrueDelta}, actual={nearDelta}, score={nearScore:F3}.");
        }
    }

    private static void VerifyNoisyNearDirectCannotDisplaceInformativePrior()
    {
        using Bitmap previous = BuildSparseViewport(scrollTop: 0);
        using Bitmap current = BuildSparseViewport(scrollTop: TrueDelta);
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(TrueDelta, TrueDelta, TrueDelta, TrueDelta);
            var noisyDirect = new ShareXModAnchorMatch(TrueDelta - 40, 0.0, 2);
            bool resolved = ShareXModTransitionResolverV019.TryResolve(
                previous, current, true, noisyDirect,
                out int delta, out string source, out _, out _);
            if (!resolved || delta != TrueDelta ||
                !string.Equals(source, "informative-prior-overrode-near-direct-v019", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"RC6 noisy near-direct fixture was not corrected: resolved={resolved}, delta={delta}, source={source}.");
            }
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static Bitmap BuildSparseViewport(int scrollTop)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.White);

        using var dark = new SolidBrush(Color.FromArgb(42, 47, 54));
        using var mid = new SolidBrush(Color.FromArgb(112, 118, 126));
        using var pale = new SolidBrush(Color.FromArgb(235, 238, 241));
        using var blue = new SolidBrush(Color.FromArgb(0, 145, 220));

        // Sparse document marks with deliberately large white gaps. Width/indent varies so a nearby
        // repeated row is plausible to a sparse fixed-phase sampler but not to content-bearing edges.
        for (int row = 0; row < 24; row++)
        {
            int documentY = 48 + row * 79;
            int y = documentY - scrollTop;
            if (y < -24 || y >= Height) continue;
            int indent = 190 + (row % 4) * 31;
            int lineWidth = 330 + (row % 5) * 57;
            g.FillRectangle((row % 3) == 0 ? mid : dark, indent, y, lineWidth, 5 + (row % 2));
            if ((row % 4) == 1)
                g.FillRectangle(pale, indent + 28, y + 14, Math.Min(420, lineWidth + 80), 18);
        }

        // A full-width sticky header and a bottom-right fixed control model Linux.do contamination.
        g.FillRectangle(pale, 0, 0, Width, 52);
        g.FillRectangle(mid, 150, 18, 280, 6);
        g.FillRectangle(blue, Width - 175, Height - 96, 145, 48);
        g.FillRectangle(Brushes.White, Width - 135, Height - 80, 75, 12);
        return bitmap;
    }
}
