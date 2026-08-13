#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModImageAppendixTail
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif"
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

        if (files.Length == 0)
        {
            return output;
        }

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

                const int margin = 36;
                const int headerHeight = 54;
                int availableWidth = Math.Max(1, targetWidth - margin * 2);
                double scale = Math.Min(1.0, availableWidth / (double)source.Width);

                // Keep appendix pages bounded so GDI+/preview paths remain reliable. Extremely
                // tall resources are reduced only as much as necessary; ordinary screenshots
                // remain at native 1:1 resolution whenever they fit the long-capture width.
                int scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
                int maxImageHeight = 28000 - headerHeight - margin * 2;
                if (scaledHeight > maxImageHeight)
                {
                    scale *= maxImageHeight / (double)scaledHeight;
                    scaledHeight = maxImageHeight;
                }

                int scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
                int pageHeight = headerHeight + margin * 2 + scaledHeight;
                Bitmap page = new(targetWidth, pageHeight, PixelFormat.Format32bppArgb);

                using (Graphics graphics = Graphics.FromImage(page))
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.InterpolationMode = scale < 1.0 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;

                    using Font font = new(FontFamily.GenericSansSerif, 14, FontStyle.Regular, GraphicsUnit.Pixel);
                    using Brush textBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
                    string label = $"Image appendix · {Path.GetFileName(file)} · {source.Width}×{source.Height}";
                    graphics.DrawString(label, font, textBrush, new PointF(margin, 18));

                    int x = (targetWidth - scaledWidth) / 2;
                    int y = headerHeight + margin;
                    graphics.DrawImage(
                        source,
                        new Rectangle(x, y, scaledWidth, scaledHeight),
                        new Rectangle(0, 0, source.Width, source.Height),
                        GraphicsUnit.Pixel);
                }

                pageIndex++;
                string outputPath = Path.Combine(captureDirectory, $"appendix_tail_{pageIndex:D4}.png");
                page.Save(outputPath, ImageFormat.Png);
                page.Dispose();
                output.Add(outputPath);
            }
            catch
            {
                // Best-effort appendix generation must never invalidate the main long capture.
            }
        }

        return output;
    }
}
