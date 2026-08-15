#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModV018TrustSplitSelfTests
{
    private const int Width = 840;
    private const int Height = 620;
    private const int Delta = 240;

    public static string RunOrThrow()
    {
        VerifyAcceptedMosaicIsImmutable();
        VerifyOneGapCatchUp();
        return "v0.1.8 trust-split passed: accepted-mosaic immutable, pixelOverlayRepair=off, one-gap two-step catch-up validated, legacy fallback forbidden.";
    }

    private static void VerifyAcceptedMosaicIsImmutable()
    {
        using Bitmap first = BuildViewport(0, 0);
        using Bitmap second = BuildViewport(Delta, 1);
        using Bitmap acceptedBefore = (Bitmap)first.Clone();
        using var session = new ShareXModTrustSplitCompositorV018.Session();
        using Bitmap combined = session.TryAppend(first, first, second, Delta)
            ?? throw new InvalidOperationException("v0.1.8 trust-split compositor rejected a valid transition.");

        if (combined.Height != Height + Delta)
            throw new InvalidOperationException($"v0.1.8 append height mismatch {combined.Height} != {Height + Delta}.");

        // The defining v0.1.8 invariant: no later frame may rewrite already accepted mosaic pixels.
        for (int y = 0; y < Height; y += 7)
        {
            for (int x = 0; x < Width; x += 9)
            {
                if (combined.GetPixel(x, y).ToArgb() != acceptedBefore.GetPixel(x, y).ToArgb())
                    throw new InvalidOperationException($"v0.1.8 mutated accepted mosaic at {x},{y}.");
            }
        }

        ShareXModV018CompositorTelemetry telemetry = session.SnapshotTelemetry();
        if (telemetry.AppendCount != 1 || telemetry.PixelOverlayRepairEnabled)
            throw new InvalidOperationException($"v0.1.8 compositor telemetry invalid: {telemetry}.");
    }

    private static void VerifyOneGapCatchUp()
    {
        ShareXModTransitionResolverV018.ResetLive();
        ShareXModAnchorContinuityV017.ResetLive();
        try
        {
            using Bitmap reliable = BuildViewport(0, 0);
            using Bitmap twoStepsLater = BuildViewport(Delta * 2, 2);
            ShareXModTransitionResolverV018.ForcePendingGapForSelfTest(Delta);

            bool resolved = ShareXModTransitionResolverV018.TryResolve(
                reliable,
                twoStepsLater,
                false,
                default,
                out int resolvedDelta,
                out string source,
                out double score,
                out bool hold);

            if (!resolved || hold || Math.Abs(resolvedDelta - Delta * 2) > 1)
                throw new InvalidOperationException($"v0.1.8 catch-up failed resolved={resolved} hold={hold} delta={resolvedDelta} source={source} score={score:F2}.");

            ShareXModV018TransitionTelemetry telemetry = ShareXModTransitionResolverV018.SnapshotTelemetry();
            if (telemetry.CatchUpResolved != 1 || telemetry.TerminalUnresolved != 0 || telemetry.PendingGap)
                throw new InvalidOperationException($"v0.1.8 catch-up telemetry invalid: {telemetry}.");
        }
        finally
        {
            ShareXModTransitionResolverV018.ResetLive();
            ShareXModAnchorContinuityV017.ResetLive();
        }
    }

    private static Bitmap BuildViewport(int logicalOffset, int frame)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        for (int y = 0; y < Height; y += 10)
        {
            int logical = logicalOffset + y;
            int row = logical / 10;
            using var bg = new SolidBrush(Color.FromArgb(
                232 + row * 7 % 21,
                234 + row * 11 % 19,
                236 + row * 13 % 17));
            g.FillRectangle(bg, 0, y, Width, 10);
            using var ink = new SolidBrush(Color.FromArgb(
                35 + row * 17 % 160,
                45 + row * 19 % 150,
                55 + row * 23 % 140));
            g.FillRectangle(ink, 30 + row * 29 % 620, y + 3, 80 + row * 3 % 150, 4);
        }

        // Deliberately large changing same-screen overlay. v0.1.6-style pixel repair could mutate
        // the accepted body around it; v0.1.8 may diagnose it but must never rewrite prior pixels.
        int fixedX = Width - 220;
        int fixedY = Height - 190;
        using var overlay = new SolidBrush(Color.FromArgb(24 + frame * 31 % 80, 42, 58));
        g.FillRectangle(overlay, fixedX, fixedY, 190, 160);
        using var accent = new SolidBrush(Color.FromArgb(0, 150 + frame * 17 % 90, 220));
        g.FillRectangle(accent, fixedX + 20, fixedY + 28, 120, 22);
        return bitmap;
    }
}
