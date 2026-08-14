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
        if (before == null || after == null ||
            before.Width != after.Width || before.Height != after.Height ||
            scrollDelta <= 8 || scrollDelta >= after.Height - 8)
        {
            return null;
        }

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
                // First release is conservative: only repair screen-edge chrome where fixed/sticky
                // controls are common. This avoids changing low-motion document content in the body.
                double centerX = (tx + 0.5) / cols;
                double centerY = (ty + 0.5) / rows;
                bool edgeChrome = centerX <= 0.22 || centerX >= 0.78 ||
                                  centerY <= 0.14 || centerY >= 0.90;
                if (!edgeChrome) continue;

                int currentTop = ty * TileHeight;
                int currentLeft = tx * TileWidth;
                int shiftedPreviousTop = currentTop + probeDelta;
                if (shiftedPreviousTop + TileHeight > ProbeHeight) continue;

                double sameScreenDifference = TileDifference(
                    previousProbe, currentTop, currentLeft,
                    currentProbe, currentTop, currentLeft);

                // Fixed UI should be almost identical at the same screen coordinate.
                if (sameScreenDifference > 2.5) continue;

                double shiftedDifference = TileDifference(
                    currentProbe, currentTop, currentLeft,
                    previousProbe, shiftedPreviousTop, currentLeft);

                // The previous frame at Y + scrollDelta represents the same logical document
                // coordinate. It should be visibly different from the fixed overlay.
                if (shiftedDifference < 4.0) continue;

                candidate[ty, tx] = true;

                // A textured tile (text/icon/border inside the control) seeds the overlay mask.
                // Flat neighboring button background is added in a second pass.
                double overlayComplexity = TileComplexity(currentProbe, currentTop, currentLeft);
                if (overlayComplexity >= 1.0)
                {
                    seed[ty, tx] = true;
                }
            }
        }

        List<Rectangle> repairs = new();

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

                Rectangle rect = Rectangle.FromLTRB(left, top, right, bottom);
                if (rect.Bottom + scrollDelta <= before.Height)
                {
                    repairs.Add(rect);
                }
            }
        }

        if (repairs.Count == 0) return null;

        Bitmap cleaned = (Bitmap)after.Clone();
        using Graphics graphics = Graphics.FromImage(cleaned);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.None;

        foreach (Rectangle rect in repairs)
        {
            Rectangle source = new(rect.X, rect.Y + scrollDelta, rect.Width, rect.Height);
            graphics.DrawImage(before, rect, source, GraphicsUnit.Pixel);
        }

        return new ShareXModOverlayCleanResult(cleaned, repairs.Count);
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
