#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModAdaptiveSettleV042
{
    public static async Task<ShareXModSettleResult> WaitAsync(
        Func<Bitmap> captureProbe,
        Bitmap? beforeScroll,
        ShareXModV04Settings settings,
        int configuredScrollDelay)
    {
        int minimumDelay = Math.Max(0, Math.Max(
            settings.SettleMinimumDelayMs,
            Math.Min(configuredScrollDelay, 250)));

        int interval = Math.Clamp(settings.SettleProbeIntervalMs, 50, 1000);
        int normalMaximum = Math.Max(minimumDelay, settings.SettleMaximumWaitMs);
        int suspiciousExtension = Math.Clamp(
            Math.Max(900, settings.SettleBlankExtraWaitMs * 3),
            900,
            3500);
        int requiredStable = Math.Clamp(settings.SettleRequiredStableProbes, 1, 8);

        double baselineBottomBlank = 0;
        bool hasBaseline = false;

        if (beforeScroll != null)
        {
            _ = BuildFingerprint(
                beforeScroll,
                settings,
                out _,
                out baselineBottomBlank);
            hasBaseline = true;
        }

        if (minimumDelay > 0)
        {
            await Task.Delay(minimumDelay);
        }

        byte[]? previous = null;
        int stable = 0;
        int probes = 0;
        int waited = minimumDelay;
        double lastDifference = double.MaxValue;
        double complexity = 0;
        bool suspiciousEver = false;
        bool finalBlankLike = false;

        while (true)
        {
            int allowedMaximum = normalMaximum + (suspiciousEver ? suspiciousExtension : 0);
            if (waited >= allowedMaximum) break;

            using Bitmap frame = captureProbe();
            byte[] current = BuildFingerprint(
                frame,
                settings,
                out complexity,
                out double bottomBlankRatio);

            probes++;

            bool globallyBlank = complexity < settings.SettleBlankComplexityThreshold;
            bool newlyExposedLooksBlank =
                !globallyBlank &&
                bottomBlankRatio >= 0.82 &&
                (!hasBaseline || bottomBlankRatio >= baselineBottomBlank + 0.18);

            // An almost entirely white/flat lower area is suspicious even when the prior viewport
            // also had some whitespace. This catches a common lazy-load blank before it is accepted.
            if (!globallyBlank && bottomBlankRatio >= 0.94)
            {
                newlyExposedLooksBlank = true;
            }

            finalBlankLike = globallyBlank || newlyExposedLooksBlank;
            suspiciousEver |= newlyExposedLooksBlank;

            if (previous != null)
            {
                lastDifference = MeanAbsoluteDifference(previous, current);

                if (lastDifference <= settings.SettleMeanDifferenceThreshold && !globallyBlank)
                {
                    if (newlyExposedLooksBlank)
                    {
                        // The viewport stopped moving, but the newly exposed lower area still looks
                        // like an unloaded placeholder. Keep the low-cost watch running.
                        stable = 0;
                    }
                    else
                    {
                        stable++;
                        if (stable >= requiredStable)
                        {
                            return new ShareXModSettleResult(
                                waited,
                                probes,
                                false,
                                false,
                                lastDifference,
                                complexity);
                        }
                    }
                }
                else
                {
                    stable = 0;
                }
            }

            previous = current;

            int nextDelay = interval;
            if (newlyExposedLooksBlank)
            {
                nextDelay += Math.Clamp(settings.SettleBlankExtraWaitMs / 2, 100, 600);
            }

            int currentMaximum = normalMaximum + (suspiciousEver ? suspiciousExtension : 0);
            if (waited + nextDelay > currentMaximum)
            {
                nextDelay = currentMaximum - waited;
            }

            if (nextDelay <= 0) break;

            await Task.Delay(nextDelay);
            waited += nextDelay;
        }

        return new ShareXModSettleResult(
            waited,
            probes,
            true,
            finalBlankLike,
            lastDifference,
            complexity);
    }

    private static byte[] BuildFingerprint(
        Bitmap source,
        ShareXModV04Settings settings,
        out double complexity,
        out double bottomBlankRatio)
    {
        int width = Math.Clamp(settings.SettleProbeWidth, 32, 256);
        int height = Math.Clamp(settings.SettleProbeHeight, 18, 160);

        int cropX = Math.Clamp((int)Math.Round(source.Width * 0.16), 0, Math.Max(0, source.Width - 2));
        int cropY = Math.Clamp((int)Math.Round(source.Height * 0.04), 0, Math.Max(0, source.Height - 2));
        int cropRight = Math.Clamp((int)Math.Round(source.Width * 0.84), cropX + 1, source.Width);
        int cropBottom = Math.Clamp((int)Math.Round(source.Height * 0.94), cropY + 1, source.Height);
        Rectangle movingRegion = new(cropX, cropY, cropRight - cropX, cropBottom - cropY);

        using Bitmap probe = new(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(probe))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Low;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                movingRegion,
                GraphicsUnit.Pixel);
        }

        BitmapData data = probe.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            byte[] row = new byte[Math.Abs(data.Stride)];
            byte[] fingerprint = new byte[width * height];

            long edgeSum = 0;
            int edgeCount = 0;
            int index = 0;

            int bottomStart = Math.Clamp((int)Math.Round(height * 0.62), 0, height - 1);
            const int tileWidth = 8;
            const int tileHeight = 6;
            int blankTiles = 0;
            int totalTiles = 0;

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);

                int previousLuma = -1;
                for (int x = 0; x < width; x++)
                {
                    int p = x * 3;
                    int luma = (row[p + 2] * 77 + row[p + 1] * 150 + row[p] * 29) >> 8;
                    fingerprint[index++] = (byte)luma;

                    if (previousLuma >= 0)
                    {
                        edgeSum += Math.Abs(luma - previousLuma);
                        edgeCount++;
                    }

                    previousLuma = luma;
                }
            }

            // Blank/loaded detection used to measure only horizontal neighbours. A page made of
            // horizontal text/card bands can therefore contain substantial real structure while
            // still looking "globally blank" to that one-axis metric. Include vertical edges too;
            // a genuinely blank placeholder remains near zero in both directions, while loaded
            // rows, cards and images gain the complexity they should have.
            for (int y = 1; y < height; y++)
            {
                int rowIndex = y * width;
                int previousRowIndex = (y - 1) * width;
                for (int x = 0; x < width; x++)
                {
                    edgeSum += Math.Abs(fingerprint[rowIndex + x] - fingerprint[previousRowIndex + x]);
                    edgeCount++;
                }
            }

            complexity = edgeCount > 0 ? edgeSum / (double)edgeCount : 0;

            for (int top = bottomStart; top + tileHeight <= height; top += tileHeight)
            {
                for (int left = 0; left + tileWidth <= width; left += tileWidth)
                {
                    long sum = 0;
                    long localEdges = 0;
                    int pixels = 0;
                    int edges = 0;

                    for (int y = 0; y < tileHeight; y++)
                    {
                        int rowIndex = (top + y) * width + left;
                        int prev = -1;

                        for (int x = 0; x < tileWidth; x++)
                        {
                            int value = fingerprint[rowIndex + x];
                            sum += value;
                            pixels++;

                            if (prev >= 0)
                            {
                                localEdges += Math.Abs(value - prev);
                                edges++;
                            }

                            prev = value;
                        }
                    }

                    for (int y = 1; y < tileHeight; y++)
                    {
                        int rowIndex = (top + y) * width + left;
                        int previousRowIndex = (top + y - 1) * width + left;
                        for (int x = 0; x < tileWidth; x++)
                        {
                            localEdges += Math.Abs(fingerprint[rowIndex + x] - fingerprint[previousRowIndex + x]);
                            edges++;
                        }
                    }

                    double mean = pixels > 0 ? sum / (double)pixels : 0;
                    double localComplexity = edges > 0 ? localEdges / (double)edges : 0;
                    bool blankTile = mean >= 242 && localComplexity <= 0.85;

                    if (blankTile) blankTiles++;
                    totalTiles++;
                }
            }

            bottomBlankRatio = totalTiles > 0 ? blankTiles / (double)totalTiles : 0;
            return fingerprint;
        }
        finally
        {
            probe.UnlockBits(data);
        }
    }

    private static double MeanAbsoluteDifference(byte[] a, byte[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        if (length == 0) return double.MaxValue;

        long sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum / (double)length;
    }
}
