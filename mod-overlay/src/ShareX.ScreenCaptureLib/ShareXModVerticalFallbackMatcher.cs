#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModVerticalFallbackMatcher
{
    public static bool TryAppend(Bitmap result, Bitmap previousFrame, Bitmap currentFrame,
        ShareXModRobustScrollingSettings settings, out Bitmap? combined, out int scrollDelta, out double score)
    {
        combined = null;
        scrollDelta = 0;
        score = double.MaxValue;

        if (result == null || previousFrame == null || currentFrame == null ||
            previousFrame.Width != currentFrame.Width || previousFrame.Height != currentFrame.Height ||
            result.Width != currentFrame.Width)
        {
            return false;
        }

        if (!TryEstimateScrollDelta(previousFrame, currentFrame, settings, out scrollDelta, out score) ||
            scrollDelta <= 0 || scrollDelta >= currentFrame.Height)
        {
            return false;
        }

        Bitmap newResult = new Bitmap(result.Width, result.Height + scrollDelta, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(newResult))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(result, 0, 0);
            g.DrawImage(currentFrame,
                new Rectangle(0, result.Height, currentFrame.Width, scrollDelta),
                new Rectangle(0, currentFrame.Height - scrollDelta, currentFrame.Width, scrollDelta),
                GraphicsUnit.Pixel);
        }

        combined = newResult;
        return true;
    }

    private static bool TryEstimateScrollDelta(Bitmap previousFrame, Bitmap currentFrame,
        ShareXModRobustScrollingSettings settings, out int bestDelta, out double bestScore)
    {
        bestDelta = 0;
        bestScore = double.MaxValue;

        int width = previousFrame.Width;
        int height = previousFrame.Height;
        int minDelta = Math.Clamp(settings.FallbackMinScrollDelta, 1, Math.Max(1, height - 1));
        int maxDelta = Math.Clamp((int)(height * settings.FallbackMaxScrollDeltaRatio), minDelta, height - 1);
        int coarseStep = Math.Max(2, settings.FallbackCoarseStep);

        PixelBuffer previous = PixelBuffer.FromBitmap(previousFrame);
        PixelBuffer current = PixelBuffer.FromBitmap(currentFrame);

        try
        {
            for (int delta = minDelta; delta <= maxDelta; delta += coarseStep)
            {
                double candidate = CalculateScore(previous, current, width, height, delta, 28, 24);
                if (candidate < bestScore)
                {
                    bestScore = candidate;
                    bestDelta = delta;
                }
            }

            if (bestDelta == 0)
            {
                return false;
            }

            int refineStart = Math.Max(minDelta, bestDelta - coarseStep);
            int refineEnd = Math.Min(maxDelta, bestDelta + coarseStep);

            for (int delta = refineStart; delta <= refineEnd; delta++)
            {
                double candidate = CalculateScore(previous, current, width, height, delta, 16, 16);
                if (candidate < bestScore)
                {
                    bestScore = candidate;
                    bestDelta = delta;
                }
            }

            return bestScore <= settings.FallbackMaxMeanDifference;
        }
        finally
        {
            previous.Dispose();
            current.Dispose();
        }
    }

    private static double CalculateScore(PixelBuffer previous, PixelBuffer current, int width, int height,
        int delta, int xStep, int yStep)
    {
        int overlap = height - delta;
        if (overlap < Math.Max(64, height / 8)) return double.MaxValue;

        int marginX = Math.Min(width / 3, Math.Max(24, width / 20));
        int usableWidth = width - marginX * 2;
        if (usableWidth < 64) return double.MaxValue;

        const int tileCount = 8;
        double[] tileScores = new double[tileCount];

        for (int tile = 0; tile < tileCount; tile++)
        {
            int tileLeft = marginX + usableWidth * tile / tileCount;
            int tileRight = marginX + usableWidth * (tile + 1) / tileCount;
            long diffTotal = 0;
            int samples = 0;

            for (int y = 0; y < overlap; y += yStep)
            {
                int previousY = y + delta;
                for (int x = tileLeft; x < tileRight; x += xStep)
                {
                    int po = previous.RowOffset(previousY) + x * 4;
                    int co = current.RowOffset(y) + x * 4;
                    diffTotal += Math.Abs(previous.Bytes[po] - current.Bytes[co]);
                    diffTotal += Math.Abs(previous.Bytes[po + 1] - current.Bytes[co + 1]);
                    diffTotal += Math.Abs(previous.Bytes[po + 2] - current.Bytes[co + 2]);
                    samples += 3;
                }
            }

            tileScores[tile] = samples > 0 ? (double)diffTotal / samples : double.MaxValue;
        }

        Array.Sort(tileScores);
        double total = 0;
        for (int i = 0; i < 5; i++) total += tileScores[i];
        return total / 5;
    }

    private sealed class PixelBuffer : IDisposable
    {
        public byte[] Bytes { get; }
        private Bitmap Normalized { get; }
        private int Stride { get; }

        private PixelBuffer(Bitmap normalized, byte[] bytes, int stride)
        {
            Normalized = normalized;
            Bytes = bytes;
            Stride = stride;
        }

        public int RowOffset(int y) => y * Stride;

        public static PixelBuffer FromBitmap(Bitmap source)
        {
            Bitmap normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(normalized))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(source, 0, 0);
            }

            Rectangle rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
            BitmapData data = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * normalized.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return new PixelBuffer(normalized, bytes, stride);
            }
            finally
            {
                normalized.UnlockBits(data);
            }
        }

        public void Dispose() => Normalized.Dispose();
    }
}
