#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModAnchorMatch(int ScrollDelta, double Score, int AgreementCount);

internal static class ShareXModAnchorMatcher
{
    private const int Width = 64;
    private const int AnchorHeight = 8;
    private static readonly double[] AnchorPositions = { 0.55, 0.65, 0.75, 0.84 };

    public static bool TryEstimateScrollDelta(Bitmap before, Bitmap after, out ShareXModAnchorMatch match)
    {
        match = default;
        if (before.Width != after.Width || before.Height != after.Height || before.Height < 180) return false;

        using Bitmap a = CreateStrip(before);
        using Bitmap b = CreateStrip(after);
        List<(int delta, double score)> matches = new();

        foreach (double ratio in AnchorPositions)
        {
            int anchorY = Math.Clamp((int)(a.Height * ratio) - AnchorHeight / 2, 0, a.Height - AnchorHeight);
            byte[] anchor = ReadAnchor(a, anchorY);
            if (Complexity(anchor) < 3.0) continue;

            double best = double.MaxValue;
            double second = double.MaxValue;
            int bestY = -1;
            for (int y = 0; y <= b.Height - AnchorHeight; y++)
            {
                double score = Difference(anchor, b, y);
                if (score < best) { second = best; best = score; bestY = y; }
                else if (score < second) second = score;
            }

            if (bestY < 0 || best > 14.0 || second - best < 1.25) continue;
            double scale = before.Height / (double)a.Height;
            int delta = (int)Math.Round((anchorY - bestY) * scale);
            if (Math.Abs(delta) >= 2 && Math.Abs(delta) < before.Height)
                matches.Add((delta, best));
        }

        if (matches.Count == 0) return false;
        matches.Sort((x, y) => x.delta.CompareTo(y.delta));
        int median = matches[matches.Count / 2].delta;
        int tolerance = Math.Max(8, before.Height / 250);
        var agreeing = matches.Where(x => Math.Abs(x.delta - median) <= tolerance).ToArray();
        if (agreeing.Length < 2) return false;

        match = new ShareXModAnchorMatch(
            (int)Math.Round(agreeing.Average(x => x.delta)),
            agreeing.Average(x => x.score),
            agreeing.Length);
        return true;
    }

    private static Bitmap CreateStrip(Bitmap source)
    {
        int height = Math.Clamp(source.Height / 4, 180, 480);
        Bitmap result = new(Width, height, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.Low;
        int margin = Math.Clamp(source.Width / 8, 8, source.Width / 3);
        g.DrawImage(source, new Rectangle(0, 0, Width, height),
            new Rectangle(margin, 0, source.Width - margin * 2, source.Height), GraphicsUnit.Pixel);
        return result;
    }

    private static byte[] ReadAnchor(Bitmap bitmap, int top)
    {
        byte[] values = new byte[Width * AnchorHeight];
        int i = 0;
        for (int y = top; y < top + AnchorHeight; y++)
            for (int x = 0; x < Width; x++)
                values[i++] = Luma(bitmap.GetPixel(x, y));
        return values;
    }

    private static double Difference(byte[] anchor, Bitmap bitmap, int top)
    {
        long total = 0;
        int i = 0;
        for (int y = top; y < top + AnchorHeight; y++)
            for (int x = 0; x < Width; x++)
                total += Math.Abs(anchor[i++] - Luma(bitmap.GetPixel(x, y)));
        return total / (double)anchor.Length;
    }

    private static double Complexity(byte[] values)
    {
        long total = 0;
        int count = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (i % Width == 0) continue;
            total += Math.Abs(values[i] - values[i - 1]);
            count++;
        }
        return count > 0 ? total / (double)count : 0;
    }

    private static byte Luma(Color c) => (byte)((c.R * 77 + c.G * 150 + c.B * 29) >> 8);
}
