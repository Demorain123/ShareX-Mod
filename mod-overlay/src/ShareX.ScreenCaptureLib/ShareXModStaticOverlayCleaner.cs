#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModOverlayCleanResult(Bitmap Image, int RepairedTiles);

internal static class ShareXModStaticOverlayCleaner
{
    private const int ProbeWidth = 96;
    private const int ProbeHeight = 54;
    private const int TileWidth = 4;
    private const int TileHeight = 3;

    public static ShareXModOverlayCleanResult? TryClean(Bitmap before, Bitmap after, int scrollDelta)
    {
        if (!CanAnalyze(before, after, scrollDelta)) return null;

        List<Rectangle> stableEdgeRegions = FindStableEdgeRegions(before, after, scrollDelta);
        List<Rectangle> repairs = new();
        foreach (Rectangle rect in stableEdgeRegions)
        {
            // Repair the current frame when the same logical document pixels were already
            // visible lower down in the previous frame. This is ideal for sticky headers and
            // floating controls whose underlying content exists at Y + scrollDelta.
            if (rect.Bottom + scrollDelta <= before.Height)
            {
                repairs.Add(rect);
            }
        }

        if (repairs.Count == 0) return null;

        Bitmap cleaned = (Bitmap)after.Clone();
        using Graphics graphics = Graphics.FromImage(cleaned);
        ConfigureCopyGraphics(graphics);

        foreach (Rectangle rect in repairs)
        {
            Rectangle source = new(rect.X, rect.Y + scrollDelta, rect.Width, rect.Height);
            graphics.DrawImage(before, rect, source, GraphicsUnit.Pixel);
        }

        return new ShareXModOverlayCleanResult(cleaned, repairs.Count);
    }

    /// <summary>
    /// Repairs fixed/sticky pixels that could not be reconstructed in the current frame because
    /// they sit too close to the viewport bottom. One frame later those logical pixels have moved
    /// upward and become visible, so they can be written back into the previous viewport already
    /// present at the tail of the mosaic. This complements TryClean rather than replacing it.
    /// </summary>
    public static int TryRepairPreviousResultTail(Bitmap result, Bitmap before, Bitmap after, int scrollDelta)
    {
        if (result == null || !CanAnalyze(before, after, scrollDelta) ||
            result.Width != before.Width || result.Height < before.Height)
        {
            return 0;
        }

        List<Rectangle> stableEdgeRegions = FindStableEdgeRegions(before, after, scrollDelta);
        int previousViewportTop = result.Height - before.Height;
        int repaired = 0;

        using Graphics graphics = Graphics.FromImage(result);
        ConfigureCopyGraphics(graphics);

        foreach (Rectangle rect in stableEdgeRegions)
        {
            // A previous-frame pixel at screen Y maps to screen Y-scrollDelta in the new frame.
            // This reverse source is especially important for fixed footers/right-bottom widgets,
            // where TryClean cannot use before[Y+scrollDelta] because that coordinate is off-screen.
            int sourceY = rect.Y - scrollDelta;
            if (sourceY < 0 || sourceY + rect.Height > after.Height) continue;

            Rectangle destination = new(rect.X, previousViewportTop + rect.Y, rect.Width, rect.Height);
            if (destination.Top < 0 || destination.Bottom > result.Height) continue;

            Rectangle source = new(rect.X, sourceY, rect.Width, rect.Height);
            graphics.DrawImage(after, destination, source, GraphicsUnit.Pixel);
            repaired++;
        }

        return repaired;
    }

    private static bool CanAnalyze(Bitmap before, Bitmap after, int scrollDelta) =>
        before != null && after != null &&
        before.Width == after.Width && before.Height == after.Height &&
        scrollDelta > 8 && scrollDelta < after.Height - 8;

    private static List<Rectangle> FindStableEdgeRegions(Bitmap before, Bitmap after, int scrollDelta)
    {
        byte[] previousProbe = CreateLumaProbe(before);
        byte[] currentProbe = CreateLumaProbe(after);
        int probeDelta = Math.Max(1, (int)Math.Round(scrollDelta * (ProbeHeight / (double)after.Height)));

        int cols = ProbeWidth / TileWidth;
        int rows = ProbeHeight / TileHeight;
        bool[,] candidate = new bool[rows, cols];
        bool[,] seed = new bool[rows, cols];

        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                // Be deliberately conservative: auto-repair only screen-edge chrome where
                // sticky/fixed controls are common. Body motion is left to the stitch matcher.
                double centerX = (tx + 0.5) / cols;
                double centerY = (ty + 0.5) / rows;
                bool edgeChrome = centerX <= 0.22 || centerX >= 0.78 ||
                                  centerY <= 0.14 || centerY >= 0.90;
                if (!edgeChrome) continue;

                int currentTop = ty * TileHeight;
                int currentLeft = tx * TileWidth;

                double sameScreenDifference = TileDifference(
                    previousProbe, currentTop, currentLeft,
                    currentProbe, currentTop, currentLeft);

                // Fixed UI should be almost identical at the same screen coordinate.
                if (sameScreenDifference > 2.5) continue;

                bool hasShiftEvidence = false;

                int shiftedPreviousTop = currentTop + probeDelta;
                if (shiftedPreviousTop + TileHeight <= ProbeHeight)
                {
                    double forwardDifference = TileDifference(
                        currentProbe, currentTop, currentLeft,
                        previousProbe, shiftedPreviousTop, currentLeft);
                    hasShiftEvidence |= forwardDifference >= 4.0;
                }

                int shiftedCurrentTop = currentTop - probeDelta;
                if (shiftedCurrentTop >= 0)
                {
                    double reverseDifference = TileDifference(
                        previousProbe, currentTop, currentLeft,
                        currentProbe, shiftedCurrentTop, currentLeft);
                    hasShiftEvidence |= reverseDifference >= 4.0;
                }

                // Requiring at least one valid translated comparison lets us identify bottom-edge
                // overlays too, instead of silently skipping them because Y+delta is off-screen.
                if (!hasShiftEvidence) continue;

                candidate[ty, tx] = true;

                // Text/icon/border texture seeds the mask; flat neighboring button/background
                // tiles are admitted only when adjacent to a seed in the second pass.
                double overlayComplexity = TileComplexity(currentProbe, currentTop, currentLeft);
                if (overlayComplexity >= 1.0)
                {
                    seed[ty, tx] = true;
                }
            }
        }

        List<Rectangle> regions = new();
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                if (!candidate[ty, tx]) continue;

                bool nearSeed = false;
                for (int yy = Math.Max(0, ty - 1); yy <= Math.Min(rows - 1, ty + 1) && !nearSeed; yy++)
                {
                    for (int xx = Math.Max(0, tx - 1); xx <= Math.Min(cols - 1, tx + 1); xx++)
                    {
                        if (seed[yy, xx])
                        {
                            nearSeed = true;
                            break;
                        }
                    }
                }

                if (!nearSeed) continue;

                int left = (int)Math.Floor(tx * TileWidth * (after.Width / (double)ProbeWidth));
                int top = (int)Math.Floor(ty * TileHeight * (after.Height / (double)ProbeHeight));
                int right = (int)Math.Ceiling((tx + 1) * TileWidth * (after.Width / (double)ProbeWidth));
                int bottom = (int)Math.Ceiling((ty + 1) * TileHeight * (after.Height / (double)ProbeHeight));

                left = Math.Clamp(left, 0, after.Width - 1);
                top = Math.Clamp(top, 0, after.Height - 1);
                right = Math.Clamp(right, left + 1, after.Width);
                bottom = Math.Clamp(bottom, top + 1, after.Height);
                regions.Add(Rectangle.FromLTRB(left, top, right, bottom));
            }
        }

        return regions;
    }

    private static void ConfigureCopyGraphics(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
    }

    private static byte[] CreateLumaProbe(Bitmap source)
    {
        using Bitmap probe = new(ProbeWidth, ProbeHeight, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(probe))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Low;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.DrawImage(source,
                new Rectangle(0, 0, ProbeWidth, ProbeHeight),
                new Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
        }

        BitmapData data = probe.LockBits(
            new Rectangle(0, 0, ProbeWidth, ProbeHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            byte[] result = new byte[ProbeWidth * ProbeHeight];
            byte[] row = new byte[Math.Abs(data.Stride)];

            for (int y = 0; y < ProbeHeight; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < ProbeWidth; x++)
                {
                    int p = x * 3;
                    int luma = (row[p + 2] * 77 + row[p + 1] * 150 + row[p] * 29) >> 8;
                    result[y * ProbeWidth + x] = (byte)luma;
                }
            }

            return result;
        }
        finally
        {
            probe.UnlockBits(data);
        }
    }

    private static double TileDifference(
        byte[] a, int ay, int ax,
        byte[] b, int by, int bx)
    {
        long sum = 0;
        int count = 0;

        for (int y = 0; y < TileHeight; y++)
        {
            for (int x = 0; x < TileWidth; x++)
            {
                int av = a[(ay + y) * ProbeWidth + ax + x];
                int bv = b[(by + y) * ProbeWidth + bx + x];
                sum += Math.Abs(av - bv);
                count++;
            }
        }

        return count > 0 ? sum / (double)count : double.MaxValue;
    }

    private static double TileComplexity(byte[] values, int top, int left)
    {
        long sum = 0;
        int count = 0;

        for (int y = 0; y < TileHeight; y++)
        {
            int row = (top + y) * ProbeWidth + left;
            for (int x = 1; x < TileWidth; x++)
            {
                sum += Math.Abs(values[row + x] - values[row + x - 1]);
                count++;
            }
        }

        return count > 0 ? sum / (double)count : 0;
    }
}
