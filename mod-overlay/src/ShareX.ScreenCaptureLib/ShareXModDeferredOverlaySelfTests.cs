#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModDeferredOverlaySelfTests
{
    private const int Width = 720;
    private const int Height = 540;
    private const int Delta = 120;

    public static string RunOrThrow()
    {
        using Bitmap before = BuildViewport(0);
        using Bitmap after = BuildViewport(Delta);
        using Bitmap expectedPrevious = BuildDocumentOnly(0);
        using Bitmap mosaic = (Bitmap)before.Clone();

        Rectangle fixedRegion = new(Width - 150, Height - 118, 136, 104);
        double errorBefore = RegionError(mosaic, expectedPrevious, fixedRegion);

        int repaired = ShareXModStaticOverlayCleaner.TryRepairPreviousResultTail(
            mosaic,
            before,
            after,
            Delta);

        double errorAfter = RegionError(mosaic, expectedPrevious, fixedRegion);
        if (repaired <= 0)
        {
            throw new InvalidOperationException(
                "Deferred overlay repair did not detect a deterministic right-bottom fixed control.");
        }

        if (!(errorAfter < errorBefore * 0.72))
        {
            throw new InvalidOperationException(
                $"Deferred overlay repair did not materially recover the previous mosaic tail: before={errorBefore:F2}, after={errorAfter:F2}, tiles={repaired}.");
        }

        // The deferred pass must not change geometry; it repairs evidence already represented in
        // the mosaic rather than deleting a band and risking missing document pixels.
        if (mosaic.Width != Width || mosaic.Height != Height)
        {
            throw new InvalidOperationException("Deferred overlay repair unexpectedly changed mosaic geometry.");
        }

        return $"Deferred fixed-overlay self-test passed: repairedTiles={repaired}, error={errorBefore:F2}->{errorAfter:F2}.";
    }

    private static Bitmap BuildViewport(int logicalOffset)
    {
        Bitmap bitmap = BuildDocumentOnly(logicalOffset);
        using Graphics graphics = Graphics.FromImage(bitmap);

        using var panel = new SolidBrush(Color.FromArgb(22, 25, 32));
        graphics.FillRectangle(panel, Width - 144, Height - 112, 122, 94);
        using var accent = new SolidBrush(Color.DeepSkyBlue);
        graphics.FillRectangle(accent, Width - 132, Height - 98, 98, 9);
        using var glyph = new SolidBrush(Color.White);
        for (int y = Height - 78; y < Height - 28; y += 13)
        {
            graphics.FillRectangle(glyph, Width - 124, y, 70, 5);
        }

        return bitmap;
    }

    private static Bitmap BuildDocumentOnly(int logicalOffset)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        for (int screenY = 0; screenY < Height; screenY += 18)
        {
            int logicalY = logicalOffset + screenY;
            int row = logicalY / 18;
            Color fill = Color.FromArgb(
                255,
                214 - (row * 13 % 72),
                226 - (row * 19 % 84),
                238 - (row * 7 % 66));
            using var brush = new SolidBrush(fill);
            graphics.FillRectangle(brush, 0, screenY, Width, 18);

            using var ink = new SolidBrush(Color.FromArgb(
                255,
                35 + row * 17 % 100,
                45 + row * 23 % 95,
                60 + row * 11 % 105));
            graphics.FillRectangle(ink, 28 + row * 41 % 520, screenY + 5, 70 + row % 95, 5);
        }

        for (int i = 0; i < 9; i++)
        {
            int logicalY = 47 + i * 97;
            int y = logicalY - logicalOffset;
            if (y < -16 || y >= Height) continue;
            using var marker = new SolidBrush(Color.FromArgb(35 + i * 18, 80 + i * 9, 150 + i * 6));
            graphics.FillRectangle(marker, 140 + i * 53 % 390, y, 56, 13);
        }

        return bitmap;
    }

    private static double RegionError(Bitmap actual, Bitmap expected, Rectangle region)
    {
        long total = 0;
        long samples = 0;
        Rectangle clipped = Rectangle.Intersect(region, new Rectangle(0, 0, actual.Width, actual.Height));
        for (int y = clipped.Top + 2; y < clipped.Bottom; y += 3)
        {
            for (int x = clipped.Left + 2; x < clipped.Right; x += 3)
            {
                Color a = actual.GetPixel(x, y);
                Color b = expected.GetPixel(x, y);
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                samples += 3;
            }
        }

        return samples == 0 ? 0 : total / (double)samples;
    }
}
