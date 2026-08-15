#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipeImageAppendix
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    private sealed record VerticalPart(string File, int Width, int Height);
    private sealed record SourceAsset(string File, int Width, int Height, int TailPages, string Status);

    public static ShareXModChromeBackgroundCaptureResult? TryAppend(
        ShareXModChromeBackgroundCaptureResult result,
        ShareXModV04Settings settings)
    {
        if (!settings.ChromeImageAppendixEnabled ||
            result == null ||
            !Directory.Exists(result.DirectoryPath))
        {
            return result;
        }

        try
        {
            string sourceDirectory = Path.Combine(result.DirectoryPath, "image-appendix");
            if (!Directory.Exists(sourceDirectory))
            {
                return result;
            }

            string[] sources = Directory.GetFiles(sourceDirectory)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(settings.ChromeImageAppendixMaxAssets, 1, 500))
                .ToArray();

            if (sources.Length == 0)
            {
                return result;
            }

            List<VerticalPart> vertical = ReadVerticalParts(result.ManifestPath);
            if (vertical.Count == 0)
            {
                return result;
            }

            int targetWidth = vertical[0].Width;
            if (targetWidth < 200 || vertical.Any(x => x.Width != targetWidth))
            {
                return result;
            }

            string tailDirectory = Path.Combine(result.DirectoryPath, "image-appendix-tail");
            Directory.CreateDirectory(tailDirectory);
            ShareXModCaptureSessionContext.RegisterComponent("image-appendix-tail", tailDirectory);

            List<string> imageTailPages = new();
            List<SourceAsset> assetAudit = new();
            int pageIndex = 0;

            foreach (string sourcePath in sources)
            {
                try
                {
                    using Bitmap source = new(sourcePath);
                    if (source.Width < 2 || source.Height < 2)
                    {
                        assetAudit.Add(new SourceAsset(Path.GetFileName(sourcePath), source.Width, source.Height, 0, "invalid-dimensions"));
                        continue;
                    }

                    int before = imageTailPages.Count;
                    SliceSourceToTailPages(
                        source,
                        Path.GetFileName(sourcePath),
                        targetWidth,
                        tailDirectory,
                        ref pageIndex,
                        imageTailPages);

                    assetAudit.Add(new SourceAsset(
                        Path.GetFileName(sourcePath),
                        source.Width,
                        source.Height,
                        imageTailPages.Count - before,
                        imageTailPages.Count > before ? "embedded-native-slices" : "not-embedded"));
                }
                catch
                {
                    assetAudit.Add(new SourceAsset(Path.GetFileName(sourcePath), 0, 0, 0, "decode-failed"));
                }
            }

            if (imageTailPages.Count == 0)
            {
                return result;
            }

            List<string> baseParts = vertical
                .Select(x => Path.Combine(result.DirectoryPath, x.File))
                .Where(File.Exists)
                .ToList();

            // If horizontal appendices were already created, preserve them before adding image tails.
            string horizontalDirectory = Path.Combine(result.DirectoryPath, "horizontal-appendix");
            if (Directory.Exists(horizontalDirectory))
            {
                baseParts.AddRange(Directory.GetFiles(horizontalDirectory, "horizontal_tail_*.png")
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }

            baseParts.AddRange(imageTailPages);

            long sourceHeight = 0;
            foreach (string path in baseParts)
            {
                using Bitmap image = new(path);
                if (image.Width != targetWidth)
                {
                    return result;
                }
                sourceHeight += image.Height;
            }

            string finalPng = Path.Combine(result.DirectoryPath, "capture-full.png");
            ShareXModSegmentedPngWriter.Write(
                finalPng,
                baseParts.ToArray(),
                targetWidth,
                sourceHeight);

            Bitmap preview = CreatePreview(
                baseParts,
                targetWidth,
                sourceHeight,
                settings.SegmentPreviewMaxHeight);

            ShareXModSegmentedOutputRegistry.Register(
                preview,
                result.ManifestPath,
                finalPng,
                targetWidth,
                sourceHeight);

            string retention = NormalizeRetention(settings.ChromeImageAppendixOriginalRetention);
            string retentionStatus = ApplyRetention(
                sourceDirectory,
                sources,
                retention,
                embeddedSuccessfully: true,
                tailDirectory);

            File.WriteAllText(
                Path.Combine(tailDirectory, "image-appendix-tail.json"),
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Native Image Appendix Tail",
                    version = "0.6.2-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    targetWidth,
                    sourceAssetCount = sources.Length,
                    tailPageCount = imageTailPages.Count,
                    retentionRequested = retention,
                    retentionStatus,
                    note = "Images are never scaled down merely to fit a page. Oversized assets are split into native-resolution x/y slices.",
                    assets = assetAudit
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            result.Preview.Dispose();

            return new ShareXModChromeBackgroundCaptureResult
            {
                Preview = preview,
                DirectoryPath = result.DirectoryPath,
                ManifestPath = result.ManifestPath,
                PartCount = baseParts.Count,
                SourceWidth = targetWidth,
                SourceHeight = sourceHeight,
                StoppedByUser = result.StoppedByUser,
                TruncatedBySafetyLimit = result.TruncatedBySafetyLimit
            };
        }
        catch
        {
            return result;
        }
    }

    private static void SliceSourceToTailPages(
        Bitmap source,
        string sourceName,
        int targetWidth,
        string directory,
        ref int pageIndex,
        List<string> output)
    {
        const int margin = 32;
        const int headerHeight = 54;
        const int maxPageHeight = 28000;

        int availableWidth = Math.Max(1, targetWidth - margin * 2);
        int availableHeight = Math.Max(1, maxPageHeight - headerHeight - margin * 2);
        int sliceWidth = Math.Min(source.Width, availableWidth);
        int sliceHeight = Math.Min(source.Height, availableHeight);
        int xParts = (source.Width + sliceWidth - 1) / sliceWidth;
        int yParts = (source.Height + sliceHeight - 1) / sliceHeight;
        int totalParts = xParts * yParts;
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
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    using Font font = new(FontFamily.GenericSansSerif, 14, FontStyle.Regular, GraphicsUnit.Pixel);
                    using Brush brush = new SolidBrush(Color.FromArgb(60, 60, 60));
                    string label = totalParts == 1
                        ? $"Image appendix · {sourceName} · {source.Width}×{source.Height} · native 1:1"
                        : $"Image appendix · {sourceName} · {source.Width}×{source.Height} · native slice {sliceIndex}/{totalParts} · x={sourceX}, y={sourceY}";
                    graphics.DrawString(label, font, brush, new PointF(margin, 17));

                    int x = (targetWidth - currentWidth) / 2;
                    int y = headerHeight + margin;
                    graphics.DrawImage(
                        source,
                        new Rectangle(x, y, currentWidth, currentHeight),
                        new Rectangle(sourceX, sourceY, currentWidth, currentHeight),
                        GraphicsUnit.Pixel);
                }

                string path = Path.Combine(directory, $"image_tail_{++pageIndex:D4}.png");
                page.Save(path, ImageFormat.Png);
                output.Add(path);
            }
        }
    }

    private static string NormalizeRetention(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "delete" or "ask" ? normalized : "keep";
    }

    private static string ApplyRetention(
        string sourceDirectory,
        string[] sources,
        string retention,
        bool embeddedSuccessfully,
        string auditDirectory)
    {
        if (!embeddedSuccessfully)
        {
            return "kept-because-embedding-failed";
        }

        if (retention == "delete")
        {
            int deleted = 0;
            foreach (string source in sources)
            {
                try
                {
                    File.Delete(source);
                    deleted++;
                }
                catch
                {
                }
            }
            return deleted == sources.Length
                ? "deleted-after-successful-embedding"
                : $"partially-deleted-{deleted}-of-{sources.Length}";
        }

        if (retention == "ask")
        {
            string pending = Path.Combine(auditDirectory, "original-retention-pending.json");
            File.WriteAllText(
                pending,
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Original Appendix Retention Prompt",
                    version = "0.6.2-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    requested = "ask",
                    safeFallback = "keep",
                    sourceDirectory,
                    files = sources.Select(Path.GetFileName).ToArray(),
                    message = "UI confirmation has not yet been collected. Originals are kept until the user explicitly chooses deletion."
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            return "pending-user-confirmation-originals-kept";
        }

        return "kept";
    }

    private static List<VerticalPart> ReadVerticalParts(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return new List<VerticalPart>();
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return new List<VerticalPart>();
        }

        List<VerticalPart> result = new();
        foreach (JsonElement item in parts.EnumerateArray())
        {
            string file = GetString(item, "File", "file");
            int width = GetInt(item, "Width", "width");
            int height = GetInt(item, "Height", "height");
            if (file.Length > 0 && width > 0 && height > 0)
            {
                result.Add(new VerticalPart(file, width, height));
            }
        }
        return result;
    }

    private static Bitmap CreatePreview(
        IReadOnlyList<string> paths,
        int sourceWidth,
        long sourceHeight,
        int configuredMaxHeight)
    {
        int maxHeight = Math.Clamp(configuredMaxHeight, 2000, 30000);
        double scale = Math.Min(1, maxHeight / (double)Math.Max(1, sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        Bitmap preview = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.White);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int y = 0;
        foreach (string path in paths)
        {
            using Bitmap image = new(path);
            int drawHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            graphics.DrawImage(
                image,
                new Rectangle(0, y, width, drawHeight),
                new Rectangle(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            y += drawHeight;
        }

        return preview;
    }

    private static string GetString(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a)) return a.GetString() ?? string.Empty;
        return item.TryGetProperty(second, out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
    }

    private static int GetInt(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a) && a.TryGetInt32(out int value)) return value;
        if (item.TryGetProperty(second, out JsonElement b) && b.TryGetInt32(out value)) return value;
        return 0;
    }
}
