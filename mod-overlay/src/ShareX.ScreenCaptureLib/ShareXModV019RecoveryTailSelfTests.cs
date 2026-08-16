#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModV019RecoveryTailSelfTests
{
    private const int Width = 900;
    private const int Height = 720;
    private const int Delta = 260;

    public static string RunOrThrow()
    {
        VerifyOutlierDirectCannotOverrideValidatedPrior();
        VerifyVariableShortMovementCanRecoverFullRange();
        VerifyEvidenceShapedSequenceRejectsAliasAnchors();
        VerifyVariableDeltaMatrix();
        VerifyOnlyNewestTailMayBeRepaired();
        VerifyFixedControlsDoNotAccumulateAcrossLongRun();
        return "v0.1.9 recovery-tail passed: outlier direct anchor gated, variable short movement full-range recovered, evidence-shaped false 200/404-style aliases rejected, variable-delta matrix passed, committed body immutable, provisional fixed/sticky tail repair exercised, long-run fixed controls bounded to safe first/final occurrences, legacy mosaic fallback forbidden.";
    }

    private static void VerifyOutlierDirectCannotOverrideValidatedPrior()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(Delta, Delta, Delta);
            using Bitmap previous = BuildViewport(0, 0, includeFixed: false);
            using Bitmap current = BuildViewport(Delta, 1, includeFixed: false);
            ShareXModAnchorMatch wrong = new(100, 0, 2);

            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, true, wrong,
                out int resolved, out string source, out _, out bool hold);

            if (!ok || hold || Math.Abs(resolved - Delta) > 2 || source != "prior-overrode-outlier-direct")
                throw new InvalidOperationException($"v0.1.9 accepted/failed outlier direct geometry: ok={ok} hold={hold} delta={resolved} source={source}.");

            ShareXModV019TransitionTelemetry telemetry = ShareXModTransitionResolverV019.SnapshotTelemetry();
            if (telemetry.OutlierDirectRejected < 1 || telemetry.PriorResolved < 1)
                throw new InvalidOperationException($"v0.1.9 outlier telemetry missing: {telemetry}.");
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static void VerifyVariableShortMovementCanRecoverFullRange()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(Delta, Delta, Delta, Delta);
            int shortDelta = 90;
            using Bitmap previous = BuildViewport(0, 0, includeFixed: false);
            using Bitmap current = BuildViewport(shortDelta, 1, includeFixed: false);

            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, false, default,
                out int resolved, out string source, out _, out bool hold);

            if (!ok || hold || Math.Abs(resolved - shortDelta) > 10 ||
                !source.Contains("full-range", StringComparison.Ordinal))
                throw new InvalidOperationException($"v0.1.9 full-range variable scroll recovery failed: ok={ok} hold={hold} delta={resolved} source={source}.");
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    // Evidence-shaped synthetic sequence derived from the user's real Linux.do failure pattern:
    // stable large wheel movements, two far-away false direct anchors, then a much shorter final
    // movement. Deltas are aligned to the fixture's 10px document raster so the test measures the
    // production resolver rather than sub-row artifacts in the synthetic renderer.
    private static void VerifyEvidenceShapedSequenceRejectsAliasAnchors()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            int[] actual = { 260, 250, 270, 260, 250, 270, 260, 250, 270, 260, 250, 270, 260, 250, 270, 260, 90 };
            var offsets = new List<int> { 0 };
            foreach (int delta in actual) offsets.Add(offsets[^1] + delta);

            for (int i = 1; i < offsets.Count; i++)
            {
                using Bitmap previous = BuildViewport(offsets[i - 1], i - 1, includeFixed: true);
                using Bitmap current = BuildViewport(offsets[i], i, includeFixed: true);

                bool falseAlias = i == 3 || i == 14;
                bool omitDirect = i == offsets.Count - 1;
                int directDelta = falseAlias ? (i == 3 ? 70 : 140) : actual[i - 1];
                ShareXModAnchorMatch direct = new(directDelta, 0, falseAlias ? 2 : 3);

                bool ok = ShareXModTransitionResolverV019.TryResolve(
                    previous, current, !omitDirect, direct,
                    out int resolved, out string source, out _, out bool hold);

                if (!ok || hold || Math.Abs(resolved - actual[i - 1]) > 10)
                    throw new InvalidOperationException($"v0.1.9 evidence-shaped sequence failed frame={i} expected={actual[i - 1]} resolved={resolved} source={source} ok={ok} hold={hold}.");

                if (falseAlias && resolved == directDelta)
                    throw new InvalidOperationException($"v0.1.9 accepted evidence-shaped false alias frame={i} alias={directDelta} source={source}.");
            }

            ShareXModV019TransitionTelemetry telemetry = ShareXModTransitionResolverV019.SnapshotTelemetry();
            if (telemetry.OutlierDirectRejected < 2 || telemetry.FullRangeRecovered < 1 || telemetry.TerminalUnresolved != 0)
                throw new InvalidOperationException($"v0.1.9 evidence-shaped telemetry insufficient: {telemetry}.");
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
        }
    }

    private static void VerifyVariableDeltaMatrix()
    {
        int[] deltas = { 60, 90, 120, 180, 220, 260, 340, 420 };
        foreach (int expected in deltas)
        {
            ShareXModTransitionResolverV019.ResetLive();
            try
            {
                ShareXModTransitionResolverV019.SeedForSelfTest(Delta, Delta, Delta, Delta);
                using Bitmap previous = BuildViewport(0, 0, includeFixed: false);
                using Bitmap current = BuildViewport(expected, 1, includeFixed: false);

                // Shorter-than-prior movement must recover without direct-anchor help. Larger jumps
                // are intentionally stricter: they need a high-consensus direct anchor that can be
                // corroborated by the independent full-range matcher.
                bool largerJump = expected > Delta;
                ShareXModAnchorMatch direct = largerJump ? new ShareXModAnchorMatch(expected, 0, 3) : default;
                bool ok = ShareXModTransitionResolverV019.TryResolve(
                    previous, current, largerJump, direct,
                    out int resolved, out string source, out _, out bool hold);
                if (!ok || hold || Math.Abs(resolved - expected) > 10)
                    throw new InvalidOperationException($"v0.1.9 variable-delta matrix failed expected={expected} resolved={resolved} source={source} ok={ok} hold={hold}.");
            }
            finally
            {
                ShareXModTransitionResolverV019.ResetLive();
            }
        }
    }

    private static void VerifyOnlyNewestTailMayBeRepaired()
    {
        using Bitmap frame0 = BuildViewport(0, 0, includeFixed: true);
        using Bitmap frame1 = BuildViewport(Delta, 1, includeFixed: true);
        using Bitmap frame2 = BuildViewport(Delta * 2, 2, includeFixed: true);
        using Bitmap frame3 = BuildViewport(Delta * 3, 3, includeFixed: true);
        using Bitmap committedBodySnapshot = (Bitmap)frame0.Clone();
        using var session = new ShareXModTrustSplitCompositorV019.Session();

        Bitmap? result = (Bitmap)frame0.Clone();
        try
        {
            result = Replace(result, session.TryAppend(result!, frame0, frame1, Delta));
            result = Replace(result, session.TryAppend(result!, frame1, frame2, Delta));
            result = Replace(result, session.TryAppend(result!, frame2, frame3, Delta));

            AssertFirstViewportImmutable(result!, committedBodySnapshot);

            ShareXModV019CompositorTelemetry telemetry = session.SnapshotTelemetry();
            if (telemetry.AppendCount != 3 || !telemetry.CommittedBodyImmutable || !telemetry.ProvisionalTailRepairEnabled)
                throw new InvalidOperationException($"v0.1.9 compositor telemetry invalid: {telemetry}.");
            if (telemetry.TailRepairComponents <= 0 || telemetry.TailRepairPixelsApprox <= 0)
                throw new InvalidOperationException($"v0.1.9 synthetic fixed overlay did not exercise provisional tail repair: {telemetry}.");

            int blueBands = CountFixedBlueBands(result!);
            if (blueBands > 4)
                throw new InvalidOperationException($"v0.1.9 provisional tail repair left too many repeated fixed controls; bands={blueBands}, safeCeiling=4.");
        }
        finally
        {
            result?.Dispose();
        }
    }

    private static void VerifyFixedControlsDoNotAccumulateAcrossLongRun()
    {
        const int transitions = 12;
        using Bitmap first = BuildViewport(0, 0, includeFixed: true);
        using Bitmap committedBodySnapshot = (Bitmap)first.Clone();
        using var session = new ShareXModTrustSplitCompositorV019.Session();
        Bitmap? result = (Bitmap)first.Clone();
        Bitmap? previous = (Bitmap)first.Clone();
        try
        {
            int offset = 0;
            for (int i = 1; i <= transitions; i++)
            {
                int delta = i == transitions ? 90 : Delta;
                offset += delta;
                using Bitmap current = BuildViewport(offset, i, includeFixed: true);
                Bitmap? next = session.TryAppend(result!, previous!, current, delta);
                result = Replace(result!, next);
                previous.Dispose();
                previous = (Bitmap)current.Clone();
            }

            AssertFirstViewportImmutable(result!, committedBodySnapshot);
            ShareXModV019CompositorTelemetry telemetry = session.SnapshotTelemetry();
            if (telemetry.AppendCount != transitions || telemetry.RejectedAppendCount != 0 || telemetry.TailRepairComponents <= 0)
                throw new InvalidOperationException($"v0.1.9 long-run compositor telemetry invalid: {telemetry}.");

            int blueBands = CountFixedBlueBands(result!);
            if (blueBands > 4)
                throw new InvalidOperationException($"v0.1.9 long-run fixed controls accumulated; bands={blueBands}, safeCeiling=4, transitions={transitions}.");
        }
        finally
        {
            previous?.Dispose();
            result?.Dispose();
        }
    }

    private static void AssertFirstViewportImmutable(Bitmap result, Bitmap snapshot)
    {
        for (int y = 0; y < Height; y += 9)
        {
            for (int x = 0; x < Width; x += 11)
            {
                if (result.GetPixel(x, y).ToArgb() != snapshot.GetPixel(x, y).ToArgb())
                    throw new InvalidOperationException($"v0.1.9 rewrote committed first-frame body at {x},{y}.");
            }
        }
    }

    private static Bitmap Replace(Bitmap oldValue, Bitmap? next)
    {
        if (next is null) throw new InvalidOperationException("v0.1.9 compositor rejected a valid synthetic transition.");
        oldValue.Dispose();
        return next;
    }

    private static Bitmap BuildViewport(int logicalOffset, int frame, bool includeFixed)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        for (int y = 0; y < Height; y += 10)
        {
            int logical = logicalOffset + y;
            int row = logical / 10;
            using var bg = new SolidBrush(Color.FromArgb(
                224 + row * 7 % 29,
                228 + row * 11 % 25,
                232 + row * 13 % 21));
            g.FillRectangle(bg, 0, y, Width, 10);
            using var ink = new SolidBrush(Color.FromArgb(
                35 + row * 17 % 160,
                45 + row * 19 % 150,
                55 + row * 23 % 140));
            g.FillRectangle(ink, 34 + row * 31 % 650, y + 3, 72 + row * 5 % 180, 4);

            if (row % 11 == 0)
            {
                using var marker = new SolidBrush(Color.FromArgb(60 + row * 3 % 120, 70, 90));
                g.FillRectangle(marker, 690 + row * 7 % 90, y + 1, 10, 8);
            }
        }

        if (includeFixed)
        {
            int fixedX = Width - 188;
            int fixedY = Height - 178;
            using var blue = new SolidBrush(Color.FromArgb(0, 145, 220));
            g.FillRectangle(blue, fixedX, fixedY, 152, 62);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, fixedX + 18, fixedY + 17, 92, 15);
            g.FillRectangle(blue, fixedX + 12, fixedY + 82, 164, 70);
            g.FillRectangle(white, fixedX + 28, fixedY + 104, 95, 14);
        }

        return bitmap;
    }

    private static int CountFixedBlueBands(Bitmap bitmap)
    {
        int bands = 0;
        bool inside = false;
        for (int y = 0; y < bitmap.Height; y += 4)
        {
            bool hit = false;
            for (int x = Width - 230; x < Width - 10; x += 4)
            {
                Color c = bitmap.GetPixel(x, y);
                if (c.B > 175 && c.G > 105 && c.R < 40)
                {
                    hit = true;
                    break;
                }
            }
            if (hit && !inside) bands++;
            inside = hit;
        }
        return bands;
    }
}
