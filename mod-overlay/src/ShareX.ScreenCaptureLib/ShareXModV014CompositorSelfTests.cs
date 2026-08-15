#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModV014CompositorSelfTests
{
    private const int Width = 720;
    private const int Height = 540;
    private const int Delta = 120;
    private const int Frames = 6;

    public static string RunOrThrow()
    {
        Bitmap? result = null;
        try
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                using Bitmap viewport = BuildViewport(frame * Delta, frame);
                Bitmap? next = ShareXModAnchorCompositorV014.TryAppend(result, viewport, Delta);
                if (next is null) throw new InvalidOperationException($"Anchor compositor rejected deterministic frame {frame}.");
                result?.Dispose();
                result = next;
            }

            Bitmap finalResult = result ?? throw new InvalidOperationException("Anchor compositor produced no result.");
            int expectedHeight = Height + Delta * (Frames - 1);
            if (finalResult.Width != Width || finalResult.Height != expectedHeight)
            {
                throw new InvalidOperationException($"Anchor compositor geometry mismatch: {finalResult.Width}x{finalResult.Height}, expected {Width}x{expectedHeight}.");
            }

            int overlayBands = CountDynamicOverlayBands(finalResult);
            if (overlayBands > 1)
            {
                throw new InvalidOperationException($"Dynamic fixed right-side control was stamped into {overlayBands} mosaic bands; expected at most one initial occurrence.");
            }

            // Every appended strip must equal the underlying document because the synthetic fixed
            // control sits above the newly exposed bottom delta. This models the Linux.do-style
            // changing right-side timeline control that should not be re-appended on every scroll.
            for (int frame = 1; frame < Frames; frame++)
            {
                int destinationTop = Height + (frame - 1) * Delta;
                int logicalTop = frame * Delta + Height - Delta;
                using Bitmap expected = BuildDocumentStrip(logicalTop, Delta);
                double error = RegionError(finalResult, expected, new Rectangle(0, destinationTop, Width, Delta));
                if (error > 0.6)
                {
                    throw new InvalidOperationException($"Anchor compositor seam error at frame {frame}: {error:F2}.");
                }
            }

            return $"v0.1.4 anchor-compositor integration passed: frames={Frames}, result={finalResult.Width}x{finalResult.Height}, dynamicFixedBands={overlayBands}.";
        }
        finally
        {
            result?.Dispose();
        }
    }

    private static Bitmap BuildViewport(int logicalOffset, int frame)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        DrawDocument(graphics, logicalOffset, Height);

        using var header = new SolidBrush(Color.FromArgb(28, 35, 44));
        graphics.FillRectangle(header, 0, 0, Width, 48);
        using var headerInk = new SolidBrush(Color.White);
        graphics.FillRectangle(headerInk, 24, 17, 150, 8);

        using var control = new SolidBrush(Color.FromArgb(0, 145, 220));
        graphics.FillRectangle(control, Width - 105, 205, 88, 86);
        using var changing = new SolidBrush(Color.FromArgb(255, 255 - frame * 22, 40 + frame * 25));
        graphics.FillRectangle(changing, Width - 88, 224 + (frame % 3) * 9, 53, 7);
        graphics.FillRectangle(changing, Width - 88, 259, 34 + frame * 4, 6);
        return bitmap;
    }

    private static Bitmap BuildDocumentStrip(int logicalTop, int height)
    {
        Bitmap bitmap = new(Width, height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        DrawDocument(graphics, logicalTop, height);
        return bitmap;
    }

    private static void DrawDocument(Graphics graphics, int logicalTop, int height)
    {
        graphics.Clear(Color.White);
        for (int y = 0; y < height; y += 12)
        {
            int logicalY = logicalTop + y;
            int row = logicalY / 12;
            using var background = new SolidBrush(Color.FromArgb(
                255,
                230 - row * 7 % 45,
                236 - row * 11 % 52,
                243 - row * 13 % 55));
            graphics.FillRectangle(background, 0, y, Width, 12);
            using var ink = new SolidBrush(Color.FromArgb(40 + row * 17 % 110, 60 + row * 23 % 100, 80 + row * 29 % 95));
            graphics.FillRectangle(ink, 30 + row * 43 % 510, y + 4, 75 + row % 120, 4);
        }
    }

    private static int CountDynamicOverlayBands(Bitmap bitmap)
    {
        int bands = 0;
        bool inside = false;
        for (int y = 0; y < bitmap.Height; y += 3)
        {
            bool hit = false;
            for (int x = Width - 110; x < Width - 10; x += 4)
            {
                Color c = bitmap.GetPixel(x, y);
                if (c.B > 170 && c.G > 105 && c.R < 40)
                {
                    hit = true;
                    break;
                }
            }

            if (hit && !inside) bands++;
            inside = hit;
        }
        return bands;
    }

    private static double RegionError(Bitmap actual, Bitmap expectedStrip, Rectangle destination)
    {
        long total = 0;
        long samples = 0;
        for (int y = 1; y < destination.Height; y += 4)
        {
            for (int x = 2; x < destination.Width; x += 5)
            {
                Color a = actual.GetPixel(destination.X + x, destination.Y + y);
                Color b = expectedStrip.GetPixel(x, y);
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                samples += 3;
            }
        }
        return samples == 0 ? 0 : total / (double)samples;
    }
}
