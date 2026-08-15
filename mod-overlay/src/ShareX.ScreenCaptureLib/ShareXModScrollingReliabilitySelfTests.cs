#nullable enable

using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Deterministic, offline fixtures for the long-capture reliability primitives that are
/// easy to regress without a real website: scroll-delta consensus, repeated-content
/// ambiguity rejection, fixed/sticky edge cleanup, false-positive resistance, and the
/// lazy-load-aware settle guard. These tests intentionally use generated pixels so CI
/// does not depend on network timing or a third-party page.
/// </summary>
internal static class ShareXModScrollingReliabilitySelfTests
{
    private const int Width = 720;
    private const int Height = 540;
    private const int Delta = 120;

    public static string RunOrThrow()
    {
        TestUniqueScrollDelta();
        TestRepeatedPatternIsRejected();
        TestStaticOverlayCleanup();
        TestPlainContentIsNotOverCleaned();
        TestLazyLoadSettle().GetAwaiter().GetResult();

        return "Scrolling reliability self-tests passed: anchor consensus, repeated-pattern rejection, static-overlay cleanup, false-positive guard, lazy-load settle.";
    }

    private static void TestUniqueScrollDelta()
    {
        using Bitmap before = BuildDocumentViewport(0, includeOverlay: false);
        using Bitmap after = BuildDocumentViewport(Delta, includeOverlay: false);

        if (!ShareXModAnchorMatcher.TryEstimateScrollDelta(before, after, out ShareXModAnchorMatch match))
        {
            throw new InvalidOperationException("Anchor matcher failed a deterministic unique-content scroll fixture.");
        }

        if (Math.Abs(match.ScrollDelta - Delta) > 24)
        {
            throw new InvalidOperationException($"Anchor matcher estimated {match.ScrollDelta}px for a {Delta}px fixture.");
        }

        if (match.AgreementCount < 2)
        {
            throw new InvalidOperationException("Anchor matcher accepted a scroll without multi-anchor agreement.");
        }
    }

    private static void TestRepeatedPatternIsRejected()
    {
        using Bitmap before = BuildRepeatingViewport(0);
        using Bitmap after = BuildRepeatingViewport(80); // exact two-period shift => visually ambiguous

        if (ShareXModAnchorMatcher.TryEstimateScrollDelta(before, after, out ShareXModAnchorMatch match))
        {
            throw new InvalidOperationException(
                $"Anchor matcher accepted a deliberately ambiguous repeated-pattern fixture: delta={match.ScrollDelta}, score={match.Score:F2}, agreement={match.AgreementCount}.");
        }
    }

    private static void TestStaticOverlayCleanup()
    {
        using Bitmap before = BuildDocumentViewport(0, includeOverlay: true);
        using Bitmap after = BuildDocumentViewport(Delta, includeOverlay: true);
        using Bitmap expected = BuildDocumentViewport(Delta, includeOverlay: false);

        double originalError = OverlayRegionError(after, expected);
        ShareXModOverlayCleanResult? clean = ShareXModStaticOverlayCleaner.TryClean(before, after, Delta);

        if (clean is not ShareXModOverlayCleanResult result)
        {
            throw new InvalidOperationException("Static overlay cleaner did not detect deterministic fixed header/side controls.");
        }

        using Bitmap cleaned = result.Image;
        double cleanedError = OverlayRegionError(cleaned, expected);

        if (result.RepairedTiles <= 0)
        {
            throw new InvalidOperationException("Static overlay cleaner reported no repaired tiles.");
        }

        // The visual cleaner is deliberately conservative, so require a material improvement
        // rather than pixel perfection. The compositor/Chrome-aware path handles regions for
        // which a previous-frame source pixel does not exist (notably the viewport bottom).
        if (!(cleanedError < originalError * 0.82))
        {
            throw new InvalidOperationException(
                $"Static overlay cleanup did not materially improve the fixture: before={originalError:F2}, after={cleanedError:F2}, tiles={result.RepairedTiles}.");
        }
    }

    private static void TestPlainContentIsNotOverCleaned()
    {
        using Bitmap before = BuildDocumentViewport(0, includeOverlay: false);
        using Bitmap after = BuildDocumentViewport(Delta, includeOverlay: false);

        ShareXModOverlayCleanResult? clean = ShareXModStaticOverlayCleaner.TryClean(before, after, Delta);
        if (clean is ShareXModOverlayCleanResult result)
        {
            try
            {
                if (result.RepairedTiles > 3)
                {
                    throw new InvalidOperationException(
                        $"Static overlay cleaner modified ordinary moving document content ({result.RepairedTiles} tiles).");
                }
            }
            finally
            {
                result.Image.Dispose();
            }
        }
    }

    private static async Task TestLazyLoadSettle()
    {
        var settings = new ShareXModV04Settings
        {
            SettleMinimumDelayMs = 0,
            SettleProbeIntervalMs = 50,
            SettleRequiredStableProbes = 2,
            SettleMaximumWaitMs = 650,
            SettleProbeWidth = 96,
            SettleProbeHeight = 54,
            SettleMeanDifferenceThreshold = 3.2,
            SettleBlankComplexityThreshold = 1.2,
            SettleBlankExtraWaitMs = 100
        };

        using Bitmap beforeScroll = BuildLazyFixture(blankBottom: false, phase: 0);
        int probe = 0;

        Bitmap CaptureProbe()
        {
            int current = probe++;
            if (current < 3)
            {
                // The viewport appears motion-stable but newly exposed content is still blank.
                return BuildLazyFixture(blankBottom: true, phase: current);
            }

            // Resource arrives and then remains stable for the required probes.
            return BuildLazyFixture(blankBottom: false, phase: 3);
        }

        ShareXModSettleResult result = await ShareXModAdaptiveSettleV042.WaitAsync(
            CaptureProbe,
            beforeScroll,
            settings,
            configuredScrollDelay: 0);

        if (result.TimedOut || result.BlankLike)
        {
            throw new InvalidOperationException(
                $"Lazy-load settle fixture was accepted as timed-out/blank: waited={result.WaitedMs}ms probes={result.ProbeCount} blank={result.BlankLike}.");
        }

        if (result.ProbeCount < 5)
        {
            throw new InvalidOperationException(
                $"Lazy-load settle returned before observing the delayed content transition (probes={result.ProbeCount}).");
        }
    }

    private static Bitmap BuildDocumentViewport(int logicalOffset, bool includeOverlay)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        int firstLogicalBand = (logicalOffset / 24) * 24;
        if (firstLogicalBand > logicalOffset) firstLogicalBand -= 24;

        for (int logicalY = firstLogicalBand; logicalY < logicalOffset + Height + 24; logicalY += 24)
        {
            int row = Math.DivRem(Math.Abs(logicalY), 24, out _);
            int y = logicalY - logicalOffset;
            Color fill = Color.FromArgb(
                255,
                220 - (row * 17 % 85),
                225 - (row * 29 % 90),
                230 - (row * 11 % 75));
            using var brush = new SolidBrush(fill);
            graphics.FillRectangle(brush, 0, y, Width, 24);

            int x1 = 24 + (row * 37 % 310);
            int x2 = 360 + (row * 53 % 280);
            using var dark = new SolidBrush(Color.FromArgb(255, 35 + row * 13 % 90, 45 + row * 7 % 80, 55 + row * 19 % 90));
            graphics.FillRectangle(dark, x1, y + 4, 95 + row % 70, 5);
            graphics.FillRectangle(dark, x2, y + 13, 55 + row % 110, 4);
        }

        // Non-periodic vertical landmarks make a true scroll shift unique across bands.
        for (int i = 0; i < 11; i++)
        {
            int logicalY = 31 + i * 79;
            int y = logicalY - logicalOffset;
            if (y < -20 || y >= Height) continue;
            using var brush = new SolidBrush(Color.FromArgb(255, 30 + i * 15, 80 + i * 9, 150 + i * 7));
            graphics.FillRectangle(brush, 110 + i * 41 % 430, y, 42 + i * 3, 12);
        }

        if (includeOverlay)
        {
            DrawOverlay(graphics);
        }

        return bitmap;
    }

    private static Bitmap BuildRepeatingViewport(int logicalOffset)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        const int period = 40;
        for (int y = 0; y < Height; y++)
        {
            int logical = Mod(logicalOffset + y, period);
            Color color = logical < 20 ? Color.FromArgb(60, 85, 120) : Color.FromArgb(215, 225, 235);
            using var pen = new Pen(color);
            graphics.DrawLine(pen, 0, y, Width - 1, y);
        }

        for (int x = 0; x < Width; x += 32)
        {
            using var pen = new Pen((x / 32) % 2 == 0 ? Color.Black : Color.DarkGray, 2);
            graphics.DrawLine(pen, x, 0, x, Height);
        }

        return bitmap;
    }

    private static Bitmap BuildLazyFixture(bool blankBottom, int phase)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        int contentBottom = blankBottom ? 335 : Height;
        for (int y = 0; y < contentBottom; y += 18)
        {
            int row = y / 18;
            Color fill = Color.FromArgb(255, 232 - row % 45, 238 - (row * 3) % 50, 244 - (row * 5) % 55);
            using var brush = new SolidBrush(fill);
            graphics.FillRectangle(brush, 0, y, Width, 18);
            using var mark = new SolidBrush(Color.FromArgb(40 + (row * 9 + phase * 3) % 100, 70, 110));
            graphics.FillRectangle(mark, 40 + (row * 47 % 520), y + 5, 100, 5);
        }

        return bitmap;
    }

    private static void DrawOverlay(Graphics graphics)
    {
        using var header = new SolidBrush(Color.FromArgb(32, 42, 58));
        graphics.FillRectangle(header, 0, 0, Width, 64);
        for (int x = 12; x < Width; x += 34)
        {
            using var tick = new SolidBrush((x / 34) % 2 == 0 ? Color.White : Color.CornflowerBlue);
            graphics.FillRectangle(tick, x, 12, 18, 9);
            graphics.FillRectangle(tick, x + 5, 38, 12, 7);
        }

        // Floating control intentionally sits in the right-edge band but high enough that
        // the previous frame contains the underlying logical pixels at y + Delta.
        using var button = new SolidBrush(Color.FromArgb(20, 20, 20));
        graphics.FillRectangle(button, Width - 112, 250, 86, 92);
        using var glyph = new SolidBrush(Color.White);
        for (int y = 264; y < 330; y += 14)
        {
            graphics.FillRectangle(glyph, Width - 96, y, 54, 5);
        }
    }

    private static double OverlayRegionError(Bitmap actual, Bitmap expected)
    {
        Rectangle header = new(0, 0, Width, 68);
        Rectangle button = new(Width - 118, 244, 98, 104);
        return (RegionError(actual, expected, header) * 0.65) +
               (RegionError(actual, expected, button) * 0.35);
    }

    private static double RegionError(Bitmap actual, Bitmap expected, Rectangle region)
    {
        long total = 0;
        long samples = 0;
        for (int y = region.Top + 2; y < region.Bottom; y += 4)
        {
            for (int x = region.Left + 2; x < region.Right; x += 4)
            {
                Color a = actual.GetPixel(x, y);
                Color b = expected.GetPixel(x, y);
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                samples += 3;
            }
        }

        return samples == 0 ? 0 : total / (double)samples;
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
