#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModVisualIntegritySelfTests
{
    public static string RunOrThrow()
    {
        AssertNativeSliceCoverage();
        AssertNativeSliceRendering();
        AssertStaticOverlayRepair();
        AssertStaticOverlayNoFalsePositive();
        AssertStaticOverlayRejectsInvalidDelta();
        return "ShareX-Mod visual integrity self-tests passed: 5";
    }

    private static void AssertNativeSliceCoverage()
    {
        const int sourceWidth = 257;
        const int sourceHeight = 30001;
        const int targetWidth = 200;

        IReadOnlyList<ShareXModImageAppendixSlice> slices =
            ShareXModImageAppendixTailV042.PlanSlices(sourceWidth, sourceHeight, targetWidth);

        if (slices.Count != 6)
            throw new InvalidOperationException($"Appendix slice-plan test failed: expected 6 slices, got {slices.Count}.");

        Rectangle[] expected =
        {
            new(0, 0, 128, 27874),
            new(128, 0, 128, 27874),
            new(256, 0, 1, 27874),
            new(0, 27874, 128, 2127),
            new(128, 27874, 128, 2127),
            new(256, 27874, 1, 2127)
        };

        long area = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            ShareXModImageAppendixSlice slice = slices[i];
            if (slice.Source != expected[i])
                throw new InvalidOperationException($"Appendix slice-plan test failed at {i}: expected {expected[i]}, got {slice.Source}.");
            if (slice.Index != i + 1 || slice.Count != slices.Count)
                throw new InvalidOperationException("Appendix slice-plan index/count metadata is inconsistent.");
            if (slice.DestinationX < 0 || slice.DestinationY < 0 ||
                slice.DestinationX + slice.Source.Width > targetWidth ||
                slice.PageHeight > 28000)
                throw new InvalidOperationException("Appendix slice-plan destination exceeds page bounds.");

            area += (long)slice.Source.Width * slice.Source.Height;
        }

        if (area != (long)sourceWidth * sourceHeight)
            throw new InvalidOperationException("Appendix slice-plan did not preserve exact native source area.");

        if (ShareXModImageAppendixTailV042.PlanSlices(100, 100, 199).Count != 0)
            throw new InvalidOperationException("Appendix slice-plan should reject target widths below 200 pixels.");
    }

    private static void AssertNativeSliceRendering()
    {
        string root = Path.Combine(Path.GetTempPath(), "sharex-mod-appendix-selftest-" + Guid.NewGuid().ToString("N"));
        string appendix = Path.Combine(root, "image-appendix");
        Directory.CreateDirectory(appendix);

        try
        {
            string sourcePath = Path.Combine(appendix, "native-test.png");
            using (Bitmap source = new(257, 13, PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < source.Height; y++)
                {
                    for (int x = 0; x < source.Width; x++)
                    {
                        source.SetPixel(x, y, Color.FromArgb(
                            255,
                            (x * 31) & 255,
                            (y * 47) & 255,
                            (x + y * 3) & 255));
                    }
                }
                source.Save(sourcePath, ImageFormat.Png);
            }

            IReadOnlyList<ShareXModImageAppendixSlice> plan =
                ShareXModImageAppendixTailV042.PlanSlices(257, 13, 200);
            IReadOnlyList<string> output = ShareXModImageAppendixTailV042.Build(root, 200);

            if (output.Count != plan.Count || output.Count != 3)
                throw new InvalidOperationException($"Appendix render test failed: expected 3 pages, got {output.Count}.");

            using Bitmap original = new(sourcePath);
            for (int i = 0; i < output.Count; i++)
            {
                ShareXModImageAppendixSlice slice = plan[i];
                using Bitmap page = new(output[i]);
                if (page.Width != 200 || page.Height != slice.PageHeight)
                    throw new InvalidOperationException("Appendix render page dimensions do not match the slice plan.");

                AssertPixel(
                    page,
                    slice.DestinationX,
                    slice.DestinationY,
                    original.GetPixel(slice.Source.Left, slice.Source.Top),
                    $"slice {i + 1} first pixel");

                AssertPixel(
                    page,
                    slice.DestinationX + slice.Source.Width - 1,
                    slice.DestinationY + slice.Source.Height - 1,
                    original.GetPixel(slice.Source.Right - 1, slice.Source.Bottom - 1),
                    $"slice {i + 1} last pixel");
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void AssertStaticOverlayRepair()
    {
        const int width = 400;
        const int height = 300;
        const int delta = 60;
        Rectangle overlay = new(326, 82, 58, 38);

        using Bitmap before = CreateDocumentFrame(width, height, 0);
        using Bitmap after = CreateDocumentFrame(width, height, delta);
        PaintFixedOverlay(before, overlay);
        PaintFixedOverlay(after, overlay);

        ShareXModOverlayCleanResult? result =
            ShareXModStaticOverlayCleaner.TryClean(before, after, delta);
        if (result == null || result.Value.RepairedTiles < 1)
            throw new InvalidOperationException("Static-overlay repair test failed: fixed edge UI was not detected.");

        using Bitmap cleaned = result.Value.Image;
        int restored = 0;
        for (int y = overlay.Top; y < overlay.Bottom; y += 3)
        {
            for (int x = overlay.Left; x < overlay.Right; x += 3)
            {
                Color actual = cleaned.GetPixel(x, y);
                Color fixedPixel = after.GetPixel(x, y);
                Color expectedDocument = before.GetPixel(x, y + delta);
                if (actual.ToArgb() == expectedDocument.ToArgb() &&
                    actual.ToArgb() != fixedPixel.ToArgb())
                {
                    restored++;
                }
            }
        }

        if (restored < 3)
            throw new InvalidOperationException($"Static-overlay repair test failed: only {restored} sampled pixels were restored to logical document content.");
    }

    private static void AssertStaticOverlayNoFalsePositive()
    {
        const int width = 400;
        const int height = 300;
        const int delta = 60;
        using Bitmap before = CreateDocumentFrame(width, height, 0);
        using Bitmap after = CreateDocumentFrame(width, height, delta);

        ShareXModOverlayCleanResult? result =
            ShareXModStaticOverlayCleaner.TryClean(before, after, delta);
        if (result != null)
        {
            result.Value.Image.Dispose();
            throw new InvalidOperationException($"Static-overlay false-positive test failed: repaired {result.Value.RepairedTiles} tiles on a clean scrolling document.");
        }
    }

    private static void AssertStaticOverlayRejectsInvalidDelta()
    {
        using Bitmap frame = CreateDocumentFrame(200, 160, 0);
        if (ShareXModStaticOverlayCleaner.TryClean(frame, frame, 4) != null)
            throw new InvalidOperationException("Static-overlay invalid-delta test failed for a tiny scroll delta.");
        if (ShareXModStaticOverlayCleaner.TryClean(frame, frame, 156) != null)
            throw new InvalidOperationException("Static-overlay invalid-delta test failed for an oversized scroll delta.");
    }

    private static Bitmap CreateDocumentFrame(int width, int height, int logicalOffsetY)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        {
            int logicalY = y + logicalOffsetY;
            for (int x = 0; x < width; x++)
            {
                int value = 20 + ((logicalY + x / 9) % 210);
                bitmap.SetPixel(x, y, Color.FromArgb(255, value, value, value));
            }
        }
        return bitmap;
    }

    private static void PaintFixedOverlay(Bitmap bitmap, Rectangle rect)
    {
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.FillRectangle(Brushes.White, rect);
        for (int x = rect.Left; x < rect.Right; x += 8)
        {
            using Brush brush = ((x - rect.Left) / 8) % 2 == 0 ? Brushes.Black : Brushes.LightGray;
            graphics.FillRectangle(brush, x, rect.Top, Math.Min(8, rect.Right - x), rect.Height);
        }
        graphics.DrawRectangle(Pens.Black, rect.Left, rect.Top, rect.Width - 1, rect.Height - 1);
    }

    private static void AssertPixel(Bitmap bitmap, int x, int y, Color expected, string name)
    {
        Color actual = bitmap.GetPixel(x, y);
        if (actual.ToArgb() != expected.ToArgb())
            throw new InvalidOperationException($"Appendix render test failed at {name}: expected {expected}, got {actual}.");
    }
}
