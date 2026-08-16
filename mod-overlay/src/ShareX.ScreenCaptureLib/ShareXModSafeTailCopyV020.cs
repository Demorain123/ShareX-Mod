#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModSafeTailCopyResult(
    int CopiedPixels,
    int RequiredPixels,
    int BlockedPixels,
    int OutOfViewPixels)
{
    public bool Complete => RequiredPixels > 0 && CopiedPixels >= RequiredPixels;
}

/// <summary>
/// Copies a provisional-tail repair from a later raw frame without ever sampling pixels that are
/// themselves inside the persistent stationary/fixed mask. The detailed result tells the caller
/// whether the current frame actually exposes the whole hidden region. If it does not, v0.1.10 keeps
/// that tail provisional and retries it against a later frame with a larger cumulative scroll offset.
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
        int tileHeight) =>
        CopyDetailed(
            result,
            current,
            currentStationaryTiles,
            columns,
            resultViewportTop,
            x0,
            y0,
            x1,
            y1,
            delta,
            tileWidth,
            tileHeight).CopiedPixels;

    internal static ShareXModSafeTailCopyResult CopyDetailed(
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
        if (result is null || current is null || x1 <= x0 || y1 <= y0 || delta <= 0)
            return default;

        int copiedPixels = 0;
        int blockedPixels = 0;
        int outOfViewPixels = 0;
        int requiredPixels = checked((x1 - x0) * (y1 - y0));

        using Graphics graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.None;

        for (int targetY0 = y0; targetY0 < y1; targetY0 += StripHeight)
        {
            int targetY1 = Math.Min(y1, targetY0 + StripHeight);
            int stripPixels = checked((x1 - x0) * (targetY1 - targetY0));
            int sourceY0 = targetY0 - delta;
            int sourceY1 = targetY1 - delta;
            if (sourceY0 < 0 || sourceY1 > current.Height)
            {
                outOfViewPixels += stripPixels;
                continue;
            }
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
                blockedPixels += stripPixels;
                continue;
            }

            int height = targetY1 - targetY0;
            int width = x1 - x0;
            graphics.DrawImage(
                current,
                new Rectangle(x0, resultViewportTop + targetY0, width, height),
                new Rectangle(x0, sourceY0, width, height),
                GraphicsUnit.Pixel);
            copiedPixels += stripPixels;
        }

        return new ShareXModSafeTailCopyResult(copiedPixels, requiredPixels, blockedPixels, outOfViewPixels);
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
