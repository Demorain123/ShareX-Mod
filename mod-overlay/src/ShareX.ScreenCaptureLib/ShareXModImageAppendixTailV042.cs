#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModImageAppendixSlice(
    Rectangle Source,
    int DestinationX,
    int DestinationY,
    int PageHeight,
    int Index,
    int Count);

internal static class ShareXModImageAppendixTailV042
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    private const int Margin = 36;
    private const int HeaderHeight = 54;
    private const int MaxPageHeight = 28000;

    internal static IReadOnlyList<ShareXModImageAppendixSlice> PlanSlices(
        int sourceWidth,
        int sourceHeight,
        int targetWidth)
    {
        if (sourceWidth < 1 || sourceHeight < 1 || targetWidth < 200)
        {
            return Array.Empty<ShareXModImageAppendixSlice>();
        }

        int availableWidth = Math.Max(1, targetWidth - Margin * 2);
        int availableHeight = Math.Max(1, MaxPageHeight - HeaderHeight - Margin * 2);
        int sliceWidth = Math.Max(1, Math.Min(sourceWidth, availableWidth));
        int sliceHeight = Math.Max(1, Math.Min(sourceHeight, availableHeight));
        int xParts = (sourceWidth + sliceWidth - 1) / sliceWidth;
        int yParts = (sourceHeight + sliceHeight - 1) / sliceHeight;
        int count = checked(xParts * yParts);

        List<ShareXModImageAppendixSlice> slices = new(count);
        int index = 0;

        for (int yPart = 0; yPart < yParts; yPart++)
        {
            int sourceY = yPart * sliceHeight;
            int currentHeight = Math.Min(sliceHeight, sourceHeight - sourceY);

            for (int xPart = 0; xPart < xParts; xPart++)
            {
                int sourceX = xPart * sliceWidth;
                int currentWidth = Math.Min(sliceWidth, sourceWidth - sourceX);
                int destinationX = (targetWidth - currentWidth) / 2;
                int destinationY = HeaderHeight + Margin;
                int pageHeight = HeaderHeight + Margin * 2 + currentHeight;

                slices.Add(new ShareXModImageAppendixSlice(
                    new Rectangle(sourceX, sourceY, currentWidth, currentHeight),
                    destinationX,
                    destinationY,
                    pageHeight,
                    ++index,
                    count));
            }
        }

        return slices;
    }

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

                // Preserve native pixels. Planning is separated from rendering so the exact
                // source coverage can be regression-tested without allocating giant bitmaps.
                IReadOnlyList<ShareXModImageAppendixSlice> slices =
                    PlanSlices(source.Width, source.Height, targetWidth);

                foreach (ShareXModImageAppendixSlice slice in slices)
                {
                    using Bitmap page = new(targetWidth, slice.PageHeight, PixelFormat.Format32bppArgb);

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

                        string label = slice.Count == 1
                            ? $"Image appendix · {Path.GetFileName(file)} · {source.Width}×{source.Height} · native 1:1"
                            : $"Image appendix · {Path.GetFileName(file)} · {source.Width}×{source.Height} · native slice {slice.Index}/{slice.Count}";

                        graphics.DrawString(label, font, textBrush, new PointF(Margin, 18));

                        graphics.DrawImage(
                            source,
                            new Rectangle(slice.DestinationX, slice.DestinationY, slice.Source.Width, slice.Source.Height),
                            slice.Source,
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
            catch
            {
                // Appendix generation is best-effort and must never invalidate the main capture.
            }
        }

        return output;
    }
}
