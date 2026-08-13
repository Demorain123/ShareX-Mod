#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModSettleResult(int WaitedMs, int ProbeCount, bool TimedOut, bool BlankLike, double LastDifference, double Complexity);

internal static class ShareXModAdaptiveSettle
{
    public static async Task<ShareXModSettleResult> WaitAsync(Func<Bitmap> captureProbe, ShareXModV04Settings settings, int configuredScrollDelay)
    {
        int minimumDelay = Math.Max(0, Math.Max(settings.SettleMinimumDelayMs, Math.Min(configuredScrollDelay, 250)));
        int interval = Math.Clamp(settings.SettleProbeIntervalMs, 50, 1000);
        int maximum = Math.Max(minimumDelay, settings.SettleMaximumWaitMs);
        int requiredStable = Math.Clamp(settings.SettleRequiredStableProbes, 1, 8);

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
        bool blankLike = false;
        bool blankExtraApplied = false;

        while (waited < maximum)
        {
            using Bitmap frame = captureProbe();
            byte[] current = BuildFingerprint(frame, settings, out complexity);
            probes++;
            blankLike = complexity < settings.SettleBlankComplexityThreshold;

            if (previous != null)
            {
                lastDifference = MeanAbsoluteDifference(previous, current);
                if (lastDifference <= settings.SettleMeanDifferenceThreshold && !blankLike)
                {
                    stable++;
                    if (stable >= requiredStable)
                    {
                        return new ShareXModSettleResult(waited, probes, false, false, lastDifference, complexity);
                    }
                }
                else
                {
                    stable = 0;
                }
            }

            previous = current;

            int nextDelay = interval;
            if (blankLike && !blankExtraApplied)
            {
                nextDelay += Math.Max(0, settings.SettleBlankExtraWaitMs);
                blankExtraApplied = true;
            }

            if (waited + nextDelay > maximum)
            {
                nextDelay = maximum - waited;
            }

            if (nextDelay <= 0)
            {
                break;
            }

            await Task.Delay(nextDelay);
            waited += nextDelay;
        }

        return new ShareXModSettleResult(waited, probes, true, blankLike, lastDifference, complexity);
    }

    private static byte[] BuildFingerprint(Bitmap source, ShareXModV04Settings settings, out double complexity)
    {
        int width = Math.Clamp(settings.SettleProbeWidth, 32, 256);
        int height = Math.Clamp(settings.SettleProbeHeight, 18, 160);

        using Bitmap probe = new(width, height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(probe))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.Low;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        }

        BitmapData data = probe.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[Math.Abs(data.Stride)];
            byte[] fingerprint = new byte[width * height];
            long edgeSum = 0;
            int edgeCount = 0;
            int index = 0;

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                int previousLum = -1;
                for (int x = 0; x < width; x++)
                {
                    int p = x * 3;
                    int lum = (row[p + 2] * 77 + row[p + 1] * 150 + row[p] * 29) >> 8;
                    fingerprint[index++] = (byte)lum;
                    if (previousLum >= 0)
                    {
                        edgeSum += Math.Abs(lum - previousLum);
                        edgeCount++;
                    }
                    previousLum = lum;
                }
            }

            complexity = edgeCount > 0 ? edgeSum / (double)edgeCount : 0;
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
        if (length == 0)
        {
            return double.MaxValue;
        }

        long sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }
        return sum / (double)length;
    }
}
