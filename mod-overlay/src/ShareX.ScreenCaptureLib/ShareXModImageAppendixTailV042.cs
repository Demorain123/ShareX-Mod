#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModImageAppendixTailV042
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    public static IReadOnlyList<string> Build(string captureDirectory, int targetWidth)
    {
        List<string> output = new();
        string appendixDirectory = Path.Combine(captureDirectory, "image-appendix");

        if (!Directory.Exists(appendixDirectory) || targetWidth < 200)
        {
            return output;
        }

        string[] files = Directory.GetFiles(appendixDirectory)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        const int margin = 36;
        const int headerHeight = 54;
        const int maxPageHeight = 28000;

        int availableWidth = Math.Max(1, targetWidth - margin * 2);
        int availableHeight = Math.Max(1, maxPageHeight - headerHeight - margin * 2);
        int pageIndex = 0;

        foreach (string file in files)
        {
            try
            {
                using Bitmap source = new(file);
                if (source.Width < 2 || source.Height < 2)
                {
                    continue;
                }

                // Do not shrink a high-resolution appendix just to fit a single tail page.
                // Preserve native pixels and split the asset into bounded tiles instead.
                int sliceWidth = Math.Max(1, Math.Min(source.Width, availableWidth));
                int sliceHeight = Math.Max(1, Math.Min(source.Height, availableHeight));
                int xParts = (source.Width + sliceWidth - 1) / sliceWidth;
                int yParts = (source.Height + sliceHeight - 1) / sliceHeight;
                int sliceCount = xParts * yParts;
                int sliceIndex = 0;

                for (int yPart = 0; yPart < yParts; yPart++)
                {
                    int sourceY = yPart * sliceHeight;
                    int currentHeight = Math.Min(sliceHeight, source.Height - sourceY);

                    for (int xPart = 0; xPart < xParts; xPart++)
                    {
                        int sourceX = xPart * sliceWidth;
                        int currentWidth = Math.Min(sliceWidth, source.Width - sourceX);
                        sliceIndex++;

                        int pageHeight = headerHeight + margin * 2 + currentHeight;
                        using Bitmap page = new(targetWidth, pageHeight, PixelFormat.Format32bppArgb);

                        using (Graphics graphics = Graphics.FromImage(page))
                        {
                            graphics.Clear(Color.White);
                            graphics.CompositingMode = CompositingMode.SourceOver;
                            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.SmoothingMode = SmoothingMode.HighQuality;

                            using Font font = new(
                                FontFamily.GenericSansSerif,
                                14,
                                FontStyle.Regular,
                                GraphicsUnit.Pixel);

                            using Brush textBrush = new SolidBrush(Color.FromArgb(70, 70, 70));

                            string label = sliceCount == 1
                                ? $"Image appendix · {Path.GetFileName(file)} · {source.Width}×{source.Height} · native 1:1"
                                : $"Image appendix · {Path.GetFileName(file)} · {source.Width}×{source.Height} · native slice {sliceIndex}/{sliceCount}";

                            graphics.DrawString(label, font, textBrush, new PointF(margin, 18));

                            int x = (targetWidth - currentWidth) / 2;
                            int y = headerHeight + margin;

                            graphics.DrawImage(
                                source,
                                new Rectangle(x, y, currentWidth, currentHeight),
                                new Rectangle(sourceX, sourceY, currentWidth, currentHeight),
                                GraphicsUnit.Pixel);
                        }

                        pageIndex++;
                        string outputPath = Path.Combine(
                            captureDirectory,
                            $"appendix_tail_{pageIndex:D4}.png");

                        page.Save(outputPath, ImageFormat.Png);
                        output.Add(outputPath);
                    }
                }
            }
            catch
            {
                // Appendix generation is best-effort and must never invalidate the main capture.
            }
        }

        return output;
    }
}
