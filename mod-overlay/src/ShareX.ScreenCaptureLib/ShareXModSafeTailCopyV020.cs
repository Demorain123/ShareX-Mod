#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Copies a provisional-tail repair from the next raw frame without ever sampling pixels that are
/// themselves inside the current stationary/fixed mask. This matters for short wheel movements: a
/// large repair rectangle can otherwise map back into the same fixed control and stamp the control at
/// a shifted Y position. Safe strips keep valid document pixels and skip only source rows whose tile
/// range intersects stationary evidence.
/// </summary>
internal static class ShareXModSafeTailCopyV020
{
    private const int StripHeight = 12;

    internal static int Copy(
        Bitmap result,
        Bitmap current,
        HashSet<int> currentStationaryTiles,
        int columns,
        int resultViewportTop,
        int x0,
        int y0,
        int x1,
        int y1,
        int delta,
        int tileWidth,
        int tileHeight)
    {
        if (result is null || current is null || x1 <= x0 || y1 <= y0 || delta <= 0) return 0;
        int copiedPixels = 0;

        using Graphics graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.None;

        for (int targetY0 = y0; targetY0 < y1; targetY0 += StripHeight)
        {
            int targetY1 = Math.Min(y1, targetY0 + StripHeight);
            int sourceY0 = targetY0 - delta;
            int sourceY1 = targetY1 - delta;
            if (sourceY0 < 0 || sourceY1 > current.Height) continue;
            if (SourceIntersectsStationaryTiles(
                currentStationaryTiles,
                columns,
                x0,
                x1,
                sourceY0,
                sourceY1,
                tileWidth,
                tileHeight,
                current.Width,
                current.Height))
            {
                continue;
            }

            int height = targetY1 - targetY0;
            int width = x1 - x0;
            graphics.DrawImage(
                current,
                new Rectangle(x0, resultViewportTop + targetY0, width, height),
                new Rectangle(x0, sourceY0, width, height),
                GraphicsUnit.Pixel);
            copiedPixels += width * height;
        }

        return copiedPixels;
    }

    private static bool SourceIntersectsStationaryTiles(
        HashSet<int> tiles,
        int columns,
        int x0,
        int x1,
        int y0,
        int y1,
        int tileWidth,
        int tileHeight,
        int width,
        int height)
    {
        if (tiles.Count == 0) return false;
        int rows = (height + tileHeight - 1) / tileHeight;
        int tx0 = Math.Clamp(x0 / tileWidth, 0, Math.Max(0, columns - 1));
        int tx1 = Math.Clamp((Math.Max(x0, x1 - 1)) / tileWidth, 0, Math.Max(0, columns - 1));
        int ty0 = Math.Clamp(y0 / tileHeight, 0, Math.Max(0, rows - 1));
        int ty1 = Math.Clamp((Math.Max(y0, y1 - 1)) / tileHeight, 0, Math.Max(0, rows - 1));

        for (int ty = ty0; ty <= ty1; ty++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                if (tiles.Contains(ty * columns + tx)) return true;
            }
        }
        return false;
    }
}
