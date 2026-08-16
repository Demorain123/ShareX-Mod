#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// v0.1.10 goal-loop regression suite. The old fixed-overlay fixtures were too clean and could pass
/// while Linux.do still failed. These tests deliberately combine repeated card structure, large white
/// areas, code-block-like regions, a fixed right/bottom control, a short wheel movement and a wrong
/// repeated-content direct anchor. The central document body is compared against an independently
/// generated ground-truth document so a compositor cannot earn a pass merely by removing blue UI.
/// </summary>
internal static class ShareXModV020GoalLoopSelfTests
{
    private const int Width = 1200;
    private const int Height = 800;
    private const int NormalDelta = 340;

    public static string RunOrThrow()
    {
        VerifyRepeatedContentAliasCannotBeatStableGeometry();
        VerifyShortMovementRecovery();
        VerifyLinuxLikeFixedControlGroundTruth();
        VerifyLargeFixedSurfaceFailsSafe();
        VerifyDeterministicAdversarialMatrix();
        return "v0.1.10 goal-loop passed: repeated-content alias rejected, variable short movement recovered, Linux-like fixed control reduced to first/final occurrences, central document ground truth preserved, large fixed surface fails safe, 12 deterministic adversarial sequences preserved exact geometry/body.";
    }

    private static void VerifyRepeatedContentAliasCannotBeatStableGeometry()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(NormalDelta, NormalDelta, NormalDelta, NormalDelta);
            using Bitmap document = BuildDocument(Height + NormalDelta + 32, seed: 101);
            using Bitmap previous = Slice(document, 0);
            using Bitmap current = Slice(document, NormalDelta);

            ShareXModAnchorMatch wrong = new(92, 0, 3);
            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, true, wrong,
                out int resolved, out string source, out _, out bool hold);

            if (!ok || hold || Math.Abs(resolved - NormalDelta) > 2 || source != "prior-overrode-outlier-direct")
                throw new InvalidOperationException($"Goal-loop alias gate failed: ok={ok} hold={hold} delta={resolved} source={source}.");
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static void VerifyShortMovementRecovery()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(NormalDelta, NormalDelta, NormalDelta, NormalDelta);
            const int shortDelta = 118;
            using Bitmap document = BuildDocument(Height + shortDelta + 32, seed: 203);
            using Bitmap previous = Slice(document, 0);
            using Bitmap current = Slice(document, shortDelta);

            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, false, default,
                out int resolved, out string source, out _, out bool hold);

            if (!ok || hold || Math.Abs(resolved - shortDelta) > 12 || !source.StartsWith("full-range", StringComparison.Ordinal))
                throw new InvalidOperationException($"Goal-loop short movement recovery failed: ok={ok} hold={hold} delta={resolved} source={source}.");
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static void VerifyLinuxLikeFixedControlGroundTruth()
    {
        int[] deltas = { 340, 340, 338, 342, 340, 120, 340, 339, 341, 340, 338, 342 };
        int totalHeight = Height;
        foreach (int value in deltas) totalHeight += value;

        using Bitmap document = BuildDocument(totalHeight + 16, seed: 307);
        var frames = new List<Bitmap>();
        int offset = 0;
        frames.Add(BuildViewport(document, offset, fixedOverlay: true, giantOverlay: false));
        foreach (int delta in deltas)
        {
            offset += delta;
            frames.Add(BuildViewport(document, offset, fixedOverlay: true, giantOverlay: false));
        }

        using var session = new ShareXModTrustSplitCompositorV019.Session();
        Bitmap? result = (Bitmap)frames[0].Clone();
        try
        {
            for (int i = 1; i < frames.Count; i++)
                result = Replace(result, session.TryAppend(result!, frames[i - 1], frames[i], deltas[i - 1]));

            if (result!.Height != totalHeight)
                throw new InvalidOperationException($"Goal-loop geometry height mismatch: actual={result.Height} expected={totalHeight}.");

            AssertCentralBodyMatchesDocument(result, document);

            int blueBands = CountFixedBlueBands(result);
            if (blueBands > 2)
                throw new InvalidOperationException($"Goal-loop Linux-like fixed control repeated: bands={blueBands}, expected<=2 (first/final only); compositor={session.SnapshotTelemetry()}; deferred={session.SnapshotDeferredTailTelemetry()}.");

            ShareXModV019CompositorTelemetry telemetry = session.SnapshotTelemetry();
            if (!telemetry.CommittedBodyImmutable || telemetry.TailRepairComponents < deltas.Length / 2)
                throw new InvalidOperationException($"Goal-loop fixed repair evidence too weak: {telemetry}; deferred={session.SnapshotDeferredTailTelemetry()}.");
            if (telemetry.RingValidatedRepairs + telemetry.PersistentEdgeRepairs <= 0)
                throw new InvalidOperationException($"Goal-loop did not exercise the hardened fixed evidence paths: {telemetry}.");
        }
        finally
        {
            result?.Dispose();
            foreach (Bitmap frame in frames) frame.Dispose();
        }
    }

    private static void VerifyLargeFixedSurfaceFailsSafe()
    {
        const int delta = 320;
        using Bitmap document = BuildDocument(Height + delta * 3 + 16, seed: 409);
        using Bitmap frame0 = BuildViewport(document, 0, fixedOverlay: false, giantOverlay: true);
        using Bitmap frame1 = BuildViewport(document, delta, fixedOverlay: false, giantOverlay: true);
        using Bitmap frame2 = BuildViewport(document, delta * 2, fixedOverlay: false, giantOverlay: true);
        using Bitmap frame3 = BuildViewport(document, delta * 3, fixedOverlay: false, giantOverlay: true);
        using var session = new ShareXModTrustSplitCompositorV019.Session();
        Bitmap? result = (Bitmap)frame0.Clone();
        try
        {
            result = Replace(result, session.TryAppend(result!, frame0, frame1, delta));
            result = Replace(result, session.TryAppend(result!, frame1, frame2, delta));
            result = Replace(result, session.TryAppend(result!, frame2, frame3, delta));

            ShareXModV019CompositorTelemetry telemetry = session.SnapshotTelemetry();
            if (telemetry.RejectedLargeComponents <= 0)
                throw new InvalidOperationException($"Goal-loop large-overlay area cap was not exercised: {telemetry}; deferred={session.SnapshotDeferredTailTelemetry()}.");

            AssertCommittedFirstFrameUnchanged(result!, frame0);
        }
        finally
        {
            result?.Dispose();
        }
    }

    private static void VerifyDeterministicAdversarialMatrix()
    {
        var rng = new Random(0x51020);
        for (int scenario = 0; scenario < 12; scenario++)
        {
            int frameCount = 7 + scenario % 4;
            int[] deltas = new int[frameCount - 1];
            int totalHeight = Height;
            for (int i = 0; i < deltas.Length; i++)
            {
                int delta = (i == 3 && scenario % 3 == 0) ? 105 + scenario : 300 + rng.Next(-26, 27);
                deltas[i] = delta;
                totalHeight += delta;
            }

            using Bitmap document = BuildDocument(totalHeight + 16, 600 + scenario * 17);
            var frames = new List<Bitmap>();
            int offset = 0;
            frames.Add(BuildViewport(document, offset, fixedOverlay: true, giantOverlay: false));
            foreach (int delta in deltas)
            {
                offset += delta;
                frames.Add(BuildViewport(document, offset, fixedOverlay: true, giantOverlay: false));
            }

            using var session = new ShareXModTrustSplitCompositorV019.Session();
            Bitmap? result = (Bitmap)frames[0].Clone();
            try
            {
                for (int i = 1; i < frames.Count; i++)
                    result = Replace(result, session.TryAppend(result!, frames[i - 1], frames[i], deltas[i - 1]));

                if (result!.Height != totalHeight)
                    throw new InvalidOperationException($"Goal-loop adversarial scenario {scenario} height mismatch: {result.Height}!={totalHeight}.");
                AssertCentralBodyMatchesDocument(result, document);
                int bands = CountFixedBlueBands(result);
                if (bands > 2)
                    throw new InvalidOperationException($"Goal-loop adversarial scenario {scenario} retained repeated fixed controls: bands={bands}; deltas=[{string.Join(',', deltas)}]; compositor={session.SnapshotTelemetry()}; deferred={session.SnapshotDeferredTailTelemetry()}.");
            }
            finally
            {
                result?.Dispose();
                foreach (Bitmap frame in frames) frame.Dispose();
            }
        }
    }

    private static Bitmap BuildDocument(int height, int seed)
    {
        Bitmap bitmap = new(Width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        var rng = new Random(seed);

        for (int y = 0; y < height; y += 96)
        {
            bool alternate = (y / 96) % 5 == 0;
            using var card = new SolidBrush(alternate ? Color.FromArgb(246, 247, 248) : Color.FromArgb(252, 252, 252));
            g.FillRectangle(card, 28, y + 8, Width - 84, 72);
            using var avatar = new SolidBrush(Color.FromArgb(35 + rng.Next(80), 45 + rng.Next(80), 55 + rng.Next(80)));
            g.FillEllipse(avatar, 42, y + 20, 28, 28);
            using var ink = new SolidBrush(Color.FromArgb(45, 50, 55));
            int lineWidth = 420 + (y / 96 * 137) % 520;
            g.FillRectangle(ink, 92, y + 23, lineWidth, 5);
            g.FillRectangle(Brushes.Gray, 92, y + 39, Math.Max(140, lineWidth - 160), 3);

            if ((y / 96) % 7 == 4)
            {
                using var dark = new SolidBrush(Color.FromArgb(26, 27, 29));
                g.FillRectangle(dark, 92, y + 52, 620, Math.Min(40, height - y - 52));
            }
        }
        return bitmap;
    }

    private static Bitmap Slice(Bitmap document, int offset) => BuildViewport(document, offset, false, false);

    private static Bitmap BuildViewport(Bitmap document, int offset, bool fixedOverlay, bool giantOverlay)
    {
        Bitmap viewport = new(Width, Height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(viewport);
        g.DrawImage(document,
            new Rectangle(0, 0, Width, Height),
            new Rectangle(0, offset, Width, Height),
            GraphicsUnit.Pixel);

        if (fixedOverlay)
        {
            int x = Width - 198;
            int y = Height - 178;
            using var blue = new SolidBrush(Color.FromArgb(0, 145, 220));
            using var pale = new SolidBrush(Color.FromArgb(243, 249, 252));
            g.FillRectangle(blue, x, y, 164, 68);
            g.FillRectangle(pale, x - 6, y + 78, 176, 74);
            g.FillRectangle(blue, x + 16, y + 94, 140, 46);
            g.FillRectangle(Brushes.White, x + 42, y + 20, 76, 12);
            g.FillRectangle(Brushes.White, x + 52, y + 108, 62, 10);
        }

        if (giantOverlay)
        {
            using var fixedPanel = new SolidBrush(Color.FromArgb(238, 242, 246));
            g.FillRectangle(fixedPanel, Width - 470, Height - 360, 450, 340);
            using var accent = new SolidBrush(Color.FromArgb(30, 120, 190));
            g.FillRectangle(accent, Width - 430, Height - 320, 300, 90);
        }

        return viewport;
    }

    private static Bitmap Replace(Bitmap oldValue, Bitmap? next)
    {
        if (next is null) throw new InvalidOperationException("Goal-loop compositor rejected a valid transition.");
        oldValue.Dispose();
        return next;
    }

    private static void AssertCentralBodyMatchesDocument(Bitmap result, Bitmap document)
    {
        int maxX = (int)(Width * 0.68);
        for (int y = 0; y < result.Height; y += 11)
        {
            for (int x = 8; x < maxX; x += 13)
            {
                if (result.GetPixel(x, y).ToArgb() != document.GetPixel(x, y).ToArgb())
                    throw new InvalidOperationException($"Goal-loop document-body corruption at {x},{y}.");
            }
        }
    }

    private static void AssertCommittedFirstFrameUnchanged(Bitmap result, Bitmap firstFrame)
    {
        for (int y = 0; y < Height; y += 9)
        {
            for (int x = 0; x < Width; x += 11)
            {
                if (result.GetPixel(x, y).ToArgb() != firstFrame.GetPixel(x, y).ToArgb())
                    throw new InvalidOperationException($"Goal-loop rewrote committed body at {x},{y}.");
            }
        }
    }

    private static int CountFixedBlueBands(Bitmap bitmap)
    {
        int bands = 0;
        bool inside = false;
        int quietRows = 0;
        int xStart = (int)(Width * 0.78);
        for (int y = 0; y < bitmap.Height; y += 4)
        {
            int hits = 0;
            for (int x = xStart; x < Width - 8; x += 4)
            {
                Color c = bitmap.GetPixel(x, y);
                if (c.B > 175 && c.G > 105 && c.R < 45) hits++;
            }
            bool hit = hits >= 8;
            if (hit)
            {
                if (!inside) bands++;
                inside = true;
                quietRows = 0;
            }
            else if (inside)
            {
                quietRows++;
                if (quietRows > 4) inside = false;
            }
        }
        return bands;
    }
}
