#nullable enable

using System;
using System.Collections.Generic;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Safety guard for persistent stationary edge masks.
///
/// A large fixed panel can contain moving text/icons that fragment its stationary mask into several
/// individually-small connected components. Per-component area caps therefore are not sufficient.
/// This guard computes dense right-edge and bottom-edge envelopes over the full persistent mask and
/// refuses destructive repair when the envelope itself is large enough to plausibly be application or
/// document content. Small fixed controls (for example Linux.do's Back + counter) remain below the
/// envelope area threshold and continue through the component-level repair path.
///
/// Conceptually this mirrors a morphological-close + connected-component statistics safety pass, but
/// keeps LongCapture dependency-free and operates directly on the existing coarse tile mask.
/// </summary>
internal static class ShareXModEdgeEnvelopeGuardV020
{
    private const double MaxDenseEnvelopeAreaRatio = 0.05;
    private const double MinDenseEnvelopeTileDensity = 0.18;
    private const double RightEdgeStartRatio = 0.68;
    private const double BottomEdgeStartRatio = 0.82;

    internal static bool IsUnsafe(
        HashSet<int> tiles,
        int columns,
        int rows,
        int tileWidth,
        int tileHeight,
        int width,
        int height,
        out double worstAreaRatio,
        out double worstDensity)
    {
        worstAreaRatio = 0;
        worstDensity = 0;
        if (tiles.Count < 4 || columns <= 0 || rows <= 0 || width <= 0 || height <= 0) return false;

        int rightStartTile = Math.Clamp((int)Math.Floor(width * RightEdgeStartRatio / tileWidth), 0, columns - 1);
        int bottomStartTile = Math.Clamp((int)Math.Floor(height * BottomEdgeStartRatio / tileHeight), 0, rows - 1);

        bool unsafeRight = EvaluateEnvelope(
            tiles,
            columns,
            rows,
            tileWidth,
            tileHeight,
            width,
            height,
            key => key % columns >= rightStartTile,
            ref worstAreaRatio,
            ref worstDensity);

        bool unsafeBottom = EvaluateEnvelope(
            tiles,
            columns,
            rows,
            tileWidth,
            tileHeight,
            width,
            height,
            key => key / columns >= bottomStartTile,
            ref worstAreaRatio,
            ref worstDensity);

        return unsafeRight || unsafeBottom;
    }

    private static bool EvaluateEnvelope(
        HashSet<int> allTiles,
        int columns,
        int rows,
        int tileWidth,
        int tileHeight,
        int width,
        int height,
        Func<int, bool> include,
        ref double worstAreaRatio,
        ref double worstDensity)
    {
        int count = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (int key in allTiles)
        {
            if (!include(key)) continue;
            int ty = key / columns;
            int tx = key % columns;
            if (tx < 0 || tx >= columns || ty < 0 || ty >= rows) continue;
            count++;
            minX = Math.Min(minX, tx);
            minY = Math.Min(minY, ty);
            maxX = Math.Max(maxX, tx);
            maxY = Math.Max(maxY, ty);
        }

        if (count < 4 || maxX < minX || maxY < minY) return false;

        int x0 = minX * tileWidth;
        int y0 = minY * tileHeight;
        int x1 = Math.Min(width, (maxX + 1) * tileWidth);
        int y1 = Math.Min(height, (maxY + 1) * tileHeight);
        long envelopeArea = Math.Max(0, x1 - x0) * (long)Math.Max(0, y1 - y0);
        long viewportArea = (long)width * height;
        if (envelopeArea <= 0 || viewportArea <= 0) return false;

        long nominalTileArea = (long)tileWidth * tileHeight;
        long representedArea = Math.Min(envelopeArea, count * nominalTileArea);
        double areaRatio = envelopeArea / (double)viewportArea;
        double density = representedArea / (double)envelopeArea;
        worstAreaRatio = Math.Max(worstAreaRatio, areaRatio);
        worstDensity = Math.Max(worstDensity, density);

        return areaRatio > MaxDenseEnvelopeAreaRatio && density >= MinDenseEnvelopeTileDensity;
    }
}
