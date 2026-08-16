#nullable enable

using System;
using System.Collections.Generic;
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
            result.Width != currentFrame.Width) return false;

        if (ShareXModAnchorMatcher.TryEstimateScrollDelta(previousFrame, currentFrame, out ShareXModAnchorMatch anchorMatch))
        { scrollDelta = anchorMatch.ScrollDelta; score = anchorMatch.Score; }
        else if (!TryEstimateScrollDelta(previousFrame, currentFrame, settings, out scrollDelta, out score)) return false;

        if (scrollDelta <= 0 || scrollDelta >= currentFrame.Height) return false;
        Bitmap newResult = new(result.Width, result.Height + scrollDelta, PixelFormat.Format32bppArgb);
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

    internal static bool TryEstimateScrollDeltaOnly(Bitmap previousFrame, Bitmap currentFrame,
        ShareXModRobustScrollingSettings settings, out int scrollDelta, out double score)
    {
        scrollDelta = 0; score = double.MaxValue;
        if (!Compatible(previousFrame, currentFrame)) return false;
        return TryEstimateScrollDelta(previousFrame, currentFrame, settings, out scrollDelta, out score);
    }

    internal static bool TryValidateSpecificDelta(Bitmap previousFrame, Bitmap currentFrame,
        ShareXModRobustScrollingSettings settings, int delta, out double score)
    {
        score = double.MaxValue;
        if (!Compatible(previousFrame, currentFrame) || delta <= 0 || delta >= currentFrame.Height) return false;
        PixelBuffer previous = PixelBuffer.FromBitmap(previousFrame);
        PixelBuffer current = PixelBuffer.FromBitmap(currentFrame);
        try
        {
            score = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 16, 16);
            return score <= settings.FallbackMaxMeanDifference;
        }
        finally { previous.Dispose(); current.Dispose(); }
    }

    internal static bool TryEstimateNearDelta(Bitmap previousFrame, Bitmap currentFrame,
        ShareXModRobustScrollingSettings settings, int priorDelta, out int bestDelta, out double bestScore)
    {
        bestDelta = 0; bestScore = double.MaxValue;
        if (!Compatible(previousFrame, currentFrame) || priorDelta <= 0 || priorDelta >= currentFrame.Height) return false;

        int tolerance = Math.Clamp(Math.Max(48, priorDelta / 8), 48, Math.Max(48, currentFrame.Height / 5));
        int minDelta = Math.Max(1, priorDelta - tolerance);
        int maxDelta = Math.Min(currentFrame.Height - 1, priorDelta + tolerance);
        PixelBuffer previous = PixelBuffer.FromBitmap(previousFrame);
        PixelBuffer current = PixelBuffer.FromBitmap(currentFrame);
        try
        {
            const int coarse = 8;
            for (int delta = minDelta; delta <= maxDelta; delta += coarse)
            {
                double candidate = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 20, 18);
                if (candidate < bestScore) { bestScore = candidate; bestDelta = delta; }
            }
            if (bestDelta == 0) return false;
            int refineStart = Math.Max(minDelta, bestDelta - coarse);
            int refineEnd = Math.Min(maxDelta, bestDelta + coarse);
            for (int delta = refineStart; delta <= refineEnd; delta++)
            {
                double candidate = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 16, 16);
                if (candidate < bestScore) { bestScore = candidate; bestDelta = delta; }
            }
            return bestScore <= settings.FallbackMaxMeanDifference;
        }
        finally { previous.Dispose(); current.Dispose(); }
    }

    private static bool TryEstimateScrollDelta(Bitmap previousFrame, Bitmap currentFrame,
        ShareXModRobustScrollingSettings settings, out int bestDelta, out double bestScore)
    {
        bestDelta = 0; bestScore = double.MaxValue;
        int width = previousFrame.Width, height = previousFrame.Height;
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
                if (candidate < bestScore) { bestScore = candidate; bestDelta = delta; }
            }
            if (bestDelta == 0) return false;
            int refineStart = Math.Max(minDelta, bestDelta - coarseStep);
            int refineEnd = Math.Min(maxDelta, bestDelta + coarseStep);
            for (int delta = refineStart; delta <= refineEnd; delta++)
            {
                double candidate = CalculateScore(previous, current, width, height, delta, 16, 16);
                if (candidate < bestScore) { bestScore = candidate; bestDelta = delta; }
            }
            return bestScore <= settings.FallbackMaxMeanDifference;
        }
        finally { previous.Dispose(); current.Dispose(); }
    }

    private static bool Compatible(Bitmap a, Bitmap b) => a != null && b != null && a.Width == b.Width && a.Height == b.Height;

    private static double CalculateScore(PixelBuffer previous, PixelBuffer current, int width, int height,
        int delta, int xStep, int yStep)
    {
        int overlap = height - delta;
        if (overlap < Math.Max(64, height / 8)) return double.MaxValue;
        int marginX = Math.Min(width / 3, Math.Max(40, width / 6));
        int usableWidth = width - marginX * 2;
        if (usableWidth < 96) return double.MaxValue;

        // The original fallback score averaged each horizontal tile over the entire overlap height.
        // A full-width sticky header therefore contaminated every tile at the same time and could
        // make the true scroll delta score worse than a repeated-content short alias. The real
        // v0.1.9-rc2 Linux.do evidence exhibited exactly that failure: a stable ~750px movement was
        // replaced by 253px and the next transition then failed closed. Score independent vertical
        // bands first and use their median so a minority of fixed/dynamic rows cannot dominate the
        // geometry decision. Horizontal side margins remain in place to avoid fixed edge controls.
        const int horizontalTileCount = 8;
        const int verticalBandCount = 8;
        List<double> verticalBandScores = new(verticalBandCount);

        for (int band = 0; band < verticalBandCount; band++)
        {
            int bandTop = overlap * band / verticalBandCount;
            int bandBottom = overlap * (band + 1) / verticalBandCount;
            if (bandBottom <= bandTop) continue;

            List<double> movingTileScores = new(horizontalTileCount);
            for (int tile = 0; tile < horizontalTileCount; tile++)
            {
                int tileLeft = marginX + usableWidth * tile / horizontalTileCount;
                int tileRight = marginX + usableWidth * (tile + 1) / horizontalTileCount;
                long shiftedDiff = 0, samePositionDiff = 0;
                int samples = 0;

                for (int y = bandTop; y < bandBottom; y += yStep)
                {
                    int previousY = y + delta;
                    for (int x = tileLeft; x < tileRight; x += xStep)
                    {
                        int shiftedPrevious = previous.RowOffset(previousY) + x * 4;
                        int samePrevious = previous.RowOffset(y) + x * 4;
                        int currentOffset = current.RowOffset(y) + x * 4;
                        shiftedDiff += ColorDifference(previous.Bytes, shiftedPrevious, current.Bytes, currentOffset);
                        samePositionDiff += ColorDifference(previous.Bytes, samePrevious, current.Bytes, currentOffset);
                        samples += 3;
                    }
                }

                if (samples == 0) continue;
                double movement = samePositionDiff / (double)samples;
                double shifted = shiftedDiff / (double)samples;
                if (movement >= 1.25) movingTileScores.Add(shifted);
            }

            if (movingTileScores.Count < 3) continue;
            movingTileScores.Sort();
            int take = Math.Min(5, movingTileScores.Count);
            double bandTotal = 0;
            for (int i = 0; i < take; i++) bandTotal += movingTileScores[i];
            verticalBandScores.Add(bandTotal / take);
        }

        if (verticalBandScores.Count < 3) return double.MaxValue;
        verticalBandScores.Sort();
        int middle = verticalBandScores.Count / 2;
        if ((verticalBandScores.Count & 1) == 1) return verticalBandScores[middle];
        return (verticalBandScores[middle - 1] + verticalBandScores[middle]) / 2.0;
    }

    private static int ColorDifference(byte[] a, int ai, byte[] b, int bi) =>
        Math.Abs(a[ai] - b[bi]) + Math.Abs(a[ai + 1] - b[bi + 1]) + Math.Abs(a[ai + 2] - b[bi + 2]);

    private sealed class PixelBuffer : IDisposable
    {
        public byte[] Bytes { get; }
        private Bitmap Normalized { get; }
        private int Stride { get; }
        private PixelBuffer(Bitmap normalized, byte[] bytes, int stride) { Normalized = normalized; Bytes = bytes; Stride = stride; }
        public int RowOffset(int y) => y * Stride;
        public static PixelBuffer FromBitmap(Bitmap source)
        {
            Bitmap normalized = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(normalized)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImageUnscaled(source, 0, 0); }
            Rectangle rect = new(0, 0, normalized.Width, normalized.Height);
            BitmapData data = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * normalized.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return new PixelBuffer(normalized, bytes, stride);
            }
            finally { normalized.UnlockBits(data); }
        }
        public void Dispose() => Normalized.Dispose();
    }
}
