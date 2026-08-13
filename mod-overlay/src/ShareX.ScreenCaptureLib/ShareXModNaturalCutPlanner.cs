#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModNaturalCutPlanner
{
    public static int FindCut(Bitmap source, ShareXModV04Settings settings)
    {
        int target = Math.Clamp(settings.SegmentTargetHeight, settings.SegmentMinimumCutDistance, source.Height - 1024);
        int radius = Math.Max(256, settings.SegmentSearchRadius);
        int start = Math.Max(settings.SegmentMinimumCutDistance, target - radius);
        int end = Math.Min(source.Height - 1024, target + radius);
        int bestY = target;
        double bestScore = double.MaxValue;

        for (int y = start; y <= end; y += 4)
        {
            double score = 0;
            Color previous = source.GetPixel(0, y);
            for (int x = 0; x < source.Width; x += 32)
            {
                Color c = source.GetPixel(x, y);
                double darkness = 255.0 - (c.R + c.G + c.B) / 3.0;
                double edge = Math.Abs(c.R - previous.R) + Math.Abs(c.G - previous.G) + Math.Abs(c.B - previous.B);
                score += darkness + edge * 0.08;
                previous = c;
            }
            score += Math.Abs(y - target) * 0.02;
            if (score < bestScore) { bestScore = score; bestY = y; }
        }
        return bestY;
    }
}
