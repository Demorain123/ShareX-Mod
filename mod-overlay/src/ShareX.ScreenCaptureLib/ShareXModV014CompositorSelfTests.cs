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
    private const int FixedX = Width - 118;
    private const int FixedY = Height - 104;
    private const int FixedWidth = 96;
    private const int FixedHeight = 88;

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
                throw new InvalidOperationException($"Bottom-right fixed control was stamped into {overlayBands} mosaic bands; expected only the newest unresolved tail occurrence.");
            }

            // Every occurrence except the newest tail must have been repaired one frame later from
            // current(y-delta), which is the same logical document pixel previously hidden by the
            // fixed control. This is the exact case v0.1.4 failed to test.
            for (int frame = 0; frame < Frames - 1; frame++)
            {
                int destinationTop = frame * Delta + FixedY;
                int logicalTop = frame * Delta + FixedY;
                using Bitmap expected = BuildDocumentStrip(logicalTop, FixedHeight);
                double error = RegionError(
                    finalResult,
                    expected,
                    new Rectangle(FixedX, destinationTop, FixedWidth, FixedHeight),
                    expectedX: FixedX);
                if (error > 18.0)
                {
                    throw new InvalidOperationException($"Deferred fixed-overlay recovery error at frame {frame}: {error:F2}.");
                }
            }

            ShareXModAnchorCompositorTelemetry telemetry = ShareXModAnchorCompositorV014.SnapshotTelemetry();
            if (telemetry.DetectedStationaryTiles <= 0 || telemetry.RepairedStationaryTiles <= 0)
            {
                throw new InvalidOperationException("Bottom fixed-overlay fixture did not exercise the v0.1.5 stationary-tile detector/repair path.");
            }
            if (telemetry.PendingTailTiles <= 0)
            {
                throw new InvalidOperationException("Fixture should expose one newest fixed tail as pending evidence at manual stop.");
            }

            return $"v0.1.5 bottom-fixed deferred-repair integration passed: frames={Frames}, result={finalResult.Width}x{finalResult.Height}, dynamicFixedBands={overlayBands}, detectedTiles={telemetry.DetectedStationaryTiles}, repairedTiles={telemetry.RepairedStationaryTiles}, pendingTailTiles={telemetry.PendingTailTiles}.";
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

        // Fixed header remains outside the newly appended bottom strip; it verifies that ordinary
        // top chrome does not perturb delta geometry.
        using var header = new SolidBrush(Color.FromArgb(28, 35, 44));
        graphics.FillRectangle(header, 0, 0, Width, 48);
        using var headerInk = new SolidBrush(Color.White);
        graphics.FillRectangle(headerInk, 24, 17, 150, 8);

        // Linux.do-shaped failure: a blue fixed Back/counter control intersects the bottom Delta
        // strip and changes a small amount of internal content every frame.
        using var control = new SolidBrush(Color.FromArgb(0, 145, 220));
        graphics.FillRectangle(control, FixedX, FixedY, FixedWidth, FixedHeight);
        using var inner = new SolidBrush(Color.White);
        graphics.FillRectangle(inner, FixedX + 13, FixedY + 13, 58, 10);
        using var changing = new SolidBrush(Color.FromArgb(255, 245 - frame * 20, 35 + frame * 28));
        graphics.FillRectangle(changing, FixedX + 14, FixedY + 38 + (frame % 3) * 5, 54, 7);
        graphics.FillRectangle(changing, FixedX + 14, FixedY + 63, 28 + frame * 6, 6);
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
            for (int x = FixedX - 4; x < FixedX + FixedWidth + 4 && x < bitmap.Width; x += 4)
            {
                Color c = bitmap.GetPixel(Math.Max(0, x), y);
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

    private static double RegionError(Bitmap actual, Bitmap expectedStrip, Rectangle destination, int expectedX)
    {
        long total = 0;
        long samples = 0;
        for (int y = 1; y < destination.Height; y += 4)
        {
            for (int x = 2; x < destination.Width; x += 5)
            {
                Color a = actual.GetPixel(destination.X + x, destination.Y + y);
                Color b = expectedStrip.GetPixel(expectedX + x, y);
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                samples += 3;
            }
        }
        return samples == 0 ? 0 : total / (double)samples;
    }
}
