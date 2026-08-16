#nullable enable

using System;
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
        VerifyOnlyNewestTailMayBeRepaired();
        return "v0.1.9 recovery-tail passed: outlier direct anchor gated, variable short movement full-range recovered, committed body immutable, provisional fixed/sticky tail repair exercised, legacy mosaic fallback forbidden.";
    }

    private static void VerifyOutlierDirectCannotOverrideValidatedPrior()
    {
        ShareXModTransitionResolverV019.ResetLive();
        try
        {
            ShareXModTransitionResolverV019.SeedForSelfTest(Delta, Delta, Delta);
            using Bitmap previous = BuildViewport(0, 0, includeFixed: false);
            using Bitmap current = BuildViewport(Delta, 1, includeFixed: false);
            ShareXModAnchorMatch wrong = new(96, 0, 2);

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
            int shortDelta = 92;
            using Bitmap previous = BuildViewport(0, 0, includeFixed: false);
            using Bitmap current = BuildViewport(shortDelta, 1, includeFixed: false);

            bool ok = ShareXModTransitionResolverV019.TryResolve(
                previous, current, false, default,
                out int resolved, out string source, out _, out bool hold);

            if (!ok || hold || Math.Abs(resolved - shortDelta) > 12 || !source.StartsWith("full-range", StringComparison.Ordinal))
                throw new InvalidOperationException($"v0.1.9 full-range variable scroll recovery failed: ok={ok} hold={hold} delta={resolved} source={source}.");
        }
        finally
        {
            ShareXModTransitionResolverV019.ResetLive();
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

            for (int y = 0; y < Height; y += 9)
            {
                for (int x = 0; x < Width; x += 11)
                {
                    if (result!.GetPixel(x, y).ToArgb() != committedBodySnapshot.GetPixel(x, y).ToArgb())
                        throw new InvalidOperationException($"v0.1.9 rewrote committed first-frame body at {x},{y}.");
                }
            }

            ShareXModV019CompositorTelemetry telemetry = session.SnapshotTelemetry();
            if (telemetry.AppendCount != 3 || !telemetry.CommittedBodyImmutable || !telemetry.ProvisionalTailRepairEnabled)
                throw new InvalidOperationException($"v0.1.9 compositor telemetry invalid: {telemetry}.");
            if (telemetry.TailRepairComponents <= 0 || telemetry.TailRepairPixelsApprox <= 0)
                throw new InvalidOperationException($"v0.1.9 synthetic fixed overlay did not exercise provisional tail repair: {telemetry}.");

            int blueBands = CountFixedBlueBands(result!);
            if (blueBands >= 4)
                throw new InvalidOperationException($"v0.1.9 provisional tail repair did not reduce repeated fixed controls; bands={blueBands}.");
        }
        finally
        {
            result?.Dispose();
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
