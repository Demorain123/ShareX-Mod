#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Evidence-shaped RC3 regressions derived from the real v0.1.9-rc2 Linux.do failure without
/// embedding or publishing any user pixels. The failing real sequence had a stable ~750px motion
/// in a 1380px viewport, a high-consensus 748px direct anchor, a false 253px short alias, then an
/// automatic stop. This fixture preserves those ratios and the fixed-header contamination pattern.
/// </summary>
internal static class ShareXModV019Rc3RealEvidenceSelfTests
{
    private const int Width = 960;
    private const int Height = 720;
    private const int StableDelta = 390; // ~54% of viewport, matching the real ~750/1380 motion.

    public static string RunOrThrow()
    {
        VerifyStickyRowsDoNotDefeatTrueGeometry();
        VerifyStrongDirectPriorConsensusCannotBeShortOverridden();
        VerifyNextNoDirectTransitionDoesNotPrematurelyStop();
        return "v0.1.9 rc3 real-evidence passed: vertical-band geometry ignores sticky-row pollution, strong direct/prior consensus cannot be short-overridden, next no-direct stable transition remains resolvable, premature automatic stop regression blocked.";
    }

    private static void VerifyStickyRowsDoNotDefeatTrueGeometry()
    {
        using Bitmap previous = BuildViewport(0, frame: 0);
        using Bitmap current = BuildViewport(StableDelta, frame: 1);
        ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();

        if (!ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                previous, current, settings, StableDelta, out double validatedScore))
        {
            throw new InvalidOperationException(
                $"RC3 robust geometry rejected the true sticky-header delta={StableDelta}, score={validatedScore:F2}.");
        }

        if (!ShareXModVerticalFallbackMatcher.TryEstimateScrollDeltaOnly(
                previous, current, settings, out int estimated, out double estimatedScore) ||
            Math.Abs(estimated - StableDelta) > 10)
        {
            throw new InvalidOperationException(
                $"RC3 full-range geometry missed true sticky-header motion expected={StableDelta} estimated={estimated} score={estimatedScore:F2}.");
        }
    }

    private static void VerifyStrongDirectPriorConsensusCannotBeShortOverridden()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(StableDelta, StableDelta, StableDelta, StableDelta);
            using Bitmap previous = BuildViewport(0, frame: 0);
            using Bitmap current = BuildViewport(StableDelta, frame: 1);

            // Mirrors the real RC2 terminal precursor: direct anchor agreement=3 and a very low
            // anchor score agree with the stable prior. A short full-range alias must never replace it.
            ShareXModAnchorMatch direct = new(StableDelta - 2, 4.38, 3);
            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, true, direct,
                out int resolved, out string source, out _, out bool hold);

            if (!ok || hold || Math.Abs(resolved - direct.ScrollDelta) > 2 ||
                source != "strong-direct-prior-consensus-v019")
            {
                throw new InvalidOperationException(
                    $"RC3 strong direct/prior consensus regression: ok={ok} hold={hold} resolved={resolved} source={source}.");
            }
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static void VerifyNextNoDirectTransitionDoesNotPrematurelyStop()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(StableDelta, StableDelta, StableDelta, StableDelta);
            using Bitmap previous = BuildViewport(StableDelta, frame: 1);
            using Bitmap current = BuildViewport(StableDelta * 2, frame: 2);

            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, false, default,
                out int resolved, out string source, out _, out bool hold);

            ShareXModV019TransitionTelemetry telemetry = ShareXModTransitionResolverV019.SnapshotTelemetry();
            if (!ok || hold || Math.Abs(resolved - StableDelta) > 10 || telemetry.TerminalUnresolved != 0)
            {
                throw new InvalidOperationException(
                    $"RC3 no-direct continuation prematurely failed: ok={ok} hold={hold} resolved={resolved} source={source} telemetry={telemetry}.");
            }
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static Bitmap BuildViewport(int logicalOffset, int frame)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        // Stable document content: deliberately repetitive at short ranges, with sparse unique marks.
        for (int y = 0; y < Height; y += 10)
        {
            int logical = logicalOffset + y;
            int row = logical / 10;
            using var background = new SolidBrush(Color.FromArgb(
                232 + row * 7 % 19,
                234 + row * 11 % 17,
                236 + row * 13 % 15));
            g.FillRectangle(background, 0, y, Width, 10);

            using var ink = new SolidBrush(Color.FromArgb(
                38 + row * 17 % 145,
                48 + row * 19 % 135,
                58 + row * 23 % 125));
            g.FillRectangle(ink, 40 + row * 31 % 680, y + 3, 80 + row * 7 % 170, 4);

            if (row % 17 == 0)
            {
                using var marker = new SolidBrush(Color.FromArgb(40 + row * 5 % 150, 75, 105));
                g.FillRectangle(marker, 720 + row * 9 % 110, y + 1, 14, 8);
            }
        }

        // Full-width sticky header: this does not move with the document. A small frame-dependent
        // element simulates live page/UI churn. The legacy whole-height tile average lets these rows
        // pollute every horizontal tile; the RC3 vertical-band median must isolate them.
        const int headerHeight = 96;
        using (var header = new SolidBrush(Color.FromArgb(248, 248, 248)))
            g.FillRectangle(header, 0, 0, Width, headerHeight);
        using (var line = new SolidBrush(Color.FromArgb(210, 210, 210)))
            g.FillRectangle(line, 0, headerHeight - 3, Width, 3);
        using (var title = new SolidBrush(Color.FromArgb(34, 34, 34)))
            g.FillRectangle(title, 120, 24, 430, 18);
        using (var churn = new SolidBrush(frame % 2 == 0 ? Color.FromArgb(0, 145, 220) : Color.FromArgb(80, 95, 210)))
            g.FillRectangle(churn, 650 + frame * 9 % 90, 54, 150, 24);

        // Right-side stationary controls are outside the central matching margins but exercise the
        // same visual topology as the real Back/counter controls.
        using (var blue = new SolidBrush(Color.FromArgb(0, 145, 220)))
        {
            g.FillRectangle(blue, Width - 155, Height - 150, 125, 52);
            g.FillRectangle(blue, Width - 165, Height - 82, 135, 48);
        }

        return bitmap;
    }
}
