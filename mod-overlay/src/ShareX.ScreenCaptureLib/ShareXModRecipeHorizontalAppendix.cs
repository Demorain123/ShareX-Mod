#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipeHorizontalAppendix
{
    private sealed record VerticalPart(string File, int Width, int Height);
    private sealed record JoinInfo(string Left, string Right, int OverlapPx, double Score, double SecondScore, double Gap, bool Passed);
    private sealed record SweepInfo(string Directory, int PanelCount, int PanoramaWidth, int PanoramaHeight, int TailPageCount, string Status, IReadOnlyList<JoinInfo> Joins);

    public static ShareXModChromeBackgroundCaptureResult? TryAppend(
        ShareXModChromeBackgroundCaptureResult result,
        ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipeHorizontalAppendixEnabled ||
            result == null ||
            !Directory.Exists(result.DirectoryPath))
        {
            return result;
        }

        try
        {
            string[] sweepDirectories = Directory.GetDirectories(
                result.DirectoryPath,
                "horizontal_step_*",
                SearchOption.TopDirectoryOnly);

            if (sweepDirectories.Length == 0)
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

            string appendixDirectory = Path.Combine(result.DirectoryPath, "horizontal-appendix");
            Directory.CreateDirectory(appendixDirectory);
            ShareXModCaptureSessionContext.RegisterComponent("horizontal-appendix", appendixDirectory);

            List<string> tailPages = new();
            List<SweepInfo> sweeps = new();
            int pageIndex = 0;

            foreach (string sweepDirectory in sweepDirectories.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string[] panels = Directory.GetFiles(sweepDirectory, "panel_*.png")
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (panels.Length == 0)
                {
                    continue;
                }

                using Bitmap? panorama = TryBuildPanorama(
                    panels,
                    settings,
                    out List<JoinInfo> joins);

                if (panorama == null)
                {
                    sweeps.Add(new SweepInfo(
                        Path.GetFileName(sweepDirectory),
                        panels.Length,
                        0,
                        0,
                        0,
                        "kept-independent-panels",
                        joins));
                    continue;
                }

                string panoramaPath = Path.Combine(
                    appendixDirectory,
                    $"{Path.GetFileName(sweepDirectory)}_panorama.png");
                panorama.Save(panoramaPath, ImageFormat.Png);

                int generated = SlicePanoramaToTailPages(
                    panorama,
                    targetWidth,
                    appendixDirectory,
                    Path.GetFileName(sweepDirectory),
                    ref pageIndex,
                    tailPages);

                sweeps.Add(new SweepInfo(
                    Path.GetFileName(sweepDirectory),
                    panels.Length,
                    panorama.Width,
                    panorama.Height,
                    generated,
                    "panorama-and-tail-pages",
                    joins));
            }

            string evidencePath = Path.Combine(appendixDirectory, "horizontal-appendix.json");
            File.WriteAllText(
                evidencePath,
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Horizontal Capture Recipe Appendix",
                    version = "0.6.2-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    targetWidth,
                    sweepCount = sweeps.Count,
                    appendedTailPageCount = tailPages.Count,
                    note = "Only sweeps whose adjacent panels pass pixel-overlap validation are stitched. Failed sweeps remain as independent source panels.",
                    sweeps
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            if (tailPages.Count == 0)
            {
                return result;
            }

            List<string> allParts = vertical
                .Select(x => Path.Combine(result.DirectoryPath, x.File))
                .Where(File.Exists)
                .ToList();
            allParts.AddRange(tailPages);

            long sourceHeight = 0;
            foreach (string path in allParts)
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
                allParts.ToArray(),
                targetWidth,
                sourceHeight);

            Bitmap preview = CreatePreview(
                allParts,
                targetWidth,
                sourceHeight,
                settings.SegmentPreviewMaxHeight);

            ShareXModSegmentedOutputRegistry.Register(
                preview,
                result.ManifestPath,
                finalPng,
                targetWidth,
                sourceHeight);

            result.Preview.Dispose();

            return new ShareXModChromeBackgroundCaptureResult
            {
                Preview = preview,
                DirectoryPath = result.DirectoryPath,
                ManifestPath = result.ManifestPath,
                PartCount = allParts.Count,
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

    private static Bitmap? TryBuildPanorama(
        string[] panelPaths,
        ShareXModV04Settings settings,
        out List<JoinInfo> joins)
    {
        joins = new List<JoinInfo>();

        List<Bitmap> panels = new();
        try
        {
            foreach (string path in panelPaths)
            {
                Bitmap panel = new(path);
                if (panel.Width < 32 || panel.Height < 16)
                {
                    panel.Dispose();
                    continue;
                }
                panels.Add(panel);
            }

            if (panels.Count == 0)
            {
                return null;
            }

            if (panels.Count == 1)
            {
                return panels[0].Clone(
                    new Rectangle(0, 0, panels[0].Width, panels[0].Height),
                    PixelFormat.Format32bppArgb);
            }

            int height = panels.Min(x => x.Height);
            List<int> placements = new() { 0 };
            int x = 0;

            for (int i = 1; i < panels.Count; i++)
            {
                Bitmap left = panels[i - 1];
                Bitmap right = panels[i];

                if (left.Height != right.Height ||
                    Math.Abs(left.Width - right.Width) > Math.Max(4, left.Width / 50))
                {
                    joins.Add(new JoinInfo(
                        Path.GetFileName(panelPaths[i - 1]),
                        Path.GetFileName(panelPaths[i]),
                        0,
                        double.MaxValue,
                        double.MaxValue,
                        0,
                        false));
                    return null;
                }

                (int overlap, double score, double secondScore) = FindHorizontalOverlap(
                    left,
                    right,
                    settings);

                double gap = secondScore < double.MaxValue
                    ? secondScore - score
                    : double.MaxValue;

                bool pass =
                    overlap > 0 &&
                    score <= settings.CaptureRecipeHorizontalAppendixMaxScore &&
                    gap >= settings.CaptureRecipeHorizontalAppendixMinGap;

                joins.Add(new JoinInfo(
                    Path.GetFileName(panelPaths[i - 1]),
                    Path.GetFileName(panelPaths[i]),
                    overlap,
                    score,
                    secondScore,
                    gap,
                    pass));

                if (!pass)
                {
                    return null;
                }

                x += left.Width - overlap;
                placements.Add(x);
            }

            int width = placements[^1] + panels[^1].Width;
            Bitmap panorama = new(width, height, PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(panorama))
            {
                graphics.Clear(Color.White);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;

                for (int i = 0; i < panels.Count; i++)
                {
                    graphics.DrawImage(
                        panels[i],
                        new Rectangle(placements[i], 0, panels[i].Width, height),
                        new Rectangle(0, 0, panels[i].Width, height),
                        GraphicsUnit.Pixel);
                }
            }

            return panorama;
        }
        finally
        {
            foreach (Bitmap panel in panels)
            {
                panel.Dispose();
            }
        }
    }

    private static (int overlap, double bestScore, double secondScore) FindHorizontalOverlap(
        Bitmap left,
        Bitmap right,
        ShareXModV04Settings settings)
    {
        int width = Math.Min(left.Width, right.Width);
        int minOverlap = Math.Clamp(
            (int)Math.Round(width * settings.CaptureRecipeHorizontalAppendixMinOverlapRatio),
            12,
            Math.Max(12, width - 1));
        int maxOverlap = Math.Clamp(
            (int)Math.Round(width * settings.CaptureRecipeHorizontalAppendixMaxOverlapRatio),
            minOverlap,
            Math.Max(minOverlap, width - 1));

        int coarseStep = Math.Max(2, (maxOverlap - minOverlap) / 32);
        (int overlap, double score) best = (0, double.MaxValue);
        (int overlap, double score) second = (0, double.MaxValue);

        using FastBitmap a = new(left);
        using FastBitmap b = new(right);

        for (int overlap = minOverlap; overlap <= maxOverlap; overlap += coarseStep)
        {
            double score = ScoreOverlap(a, b, overlap, 8);
            Update(overlap, score, ref best, ref second);
        }

        int refineStart = Math.Max(minOverlap, best.overlap - coarseStep * 2);
        int refineEnd = Math.Min(maxOverlap, best.overlap + coarseStep * 2);
        best = (0, double.MaxValue);
        second = (0, double.MaxValue);

        for (int overlap = refineStart; overlap <= refineEnd; overlap++)
        {
            double score = ScoreOverlap(a, b, overlap, 4);
            Update(overlap, score, ref best, ref second);
        }

        return (best.overlap, best.score, second.score);
    }

    private static double ScoreOverlap(
        FastBitmap left,
        FastBitmap right,
        int overlap,
        int sampleStep)
    {
        int height = Math.Min(left.Height, right.Height);
        int leftStart = left.Width - overlap;
        double total = 0;
        int count = 0;

        int top = Math.Max(2, height / 20);
        int bottom = Math.Min(height - 2, height - height / 20);

        for (int y = top; y < bottom; y += sampleStep)
        {
            for (int x = 2; x < overlap - 2; x += sampleStep)
            {
                int lx = leftStart + x;
                int rx = x;

                int l = left.Luma(lx, y);
                int r = right.Luma(rx, y);
                int lgx = left.Luma(lx + 1, y) - left.Luma(lx - 1, y);
                int rgx = right.Luma(rx + 1, y) - right.Luma(rx - 1, y);
                int lgy = left.Luma(lx, y + 1) - left.Luma(lx, y - 1);
                int rgy = right.Luma(rx, y + 1) - right.Luma(rx, y - 1);

                total += Math.Abs(l - r) * 0.22 +
                         Math.Abs(lgx - rgx) * 0.39 +
                         Math.Abs(lgy - rgy) * 0.39;
                count++;
            }
        }

        return count > 0 ? total / count : double.MaxValue;
    }

    private static void Update(
        int overlap,
        double score,
        ref (int overlap, double score) best,
        ref (int overlap, double score) second)
    {
        if (score < best.score)
        {
            second = best;
            best = (overlap, score);
        }
        else if (score < second.score && overlap != best.overlap)
        {
            second = (overlap, score);
        }
    }

    private static int SlicePanoramaToTailPages(
        Bitmap panorama,
        int targetWidth,
        string directory,
        string sweepName,
        ref int pageIndex,
        List<string> tailPages)
    {
        const int margin = 28;
        const int headerHeight = 52;
        int availableWidth = Math.Max(1, targetWidth - margin * 2);
        int sliceIndex = 0;
        int generated = 0;

        for (int sourceX = 0; sourceX < panorama.Width; sourceX += availableWidth)
        {
            int sliceWidth = Math.Min(availableWidth, panorama.Width - sourceX);
            int pageHeight = headerHeight + margin * 2 + panorama.Height;
            using Bitmap page = new(targetWidth, pageHeight, PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(page))
            {
                graphics.Clear(Color.White);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;

                using Font font = new(FontFamily.GenericSansSerif, 14, FontStyle.Regular, GraphicsUnit.Pixel);
                using Brush brush = new SolidBrush(Color.FromArgb(60, 60, 60));
                string label = $"Horizontal appendix · {sweepName} · slice {++sliceIndex} · source x={sourceX}";
                graphics.DrawString(label, font, brush, new PointF(margin, 16));

                int x = (targetWidth - sliceWidth) / 2;
                int y = headerHeight + margin;
                graphics.DrawImage(
                    panorama,
                    new Rectangle(x, y, sliceWidth, panorama.Height),
                    new Rectangle(sourceX, 0, sliceWidth, panorama.Height),
                    GraphicsUnit.Pixel);
            }

            string path = Path.Combine(directory, $"horizontal_tail_{++pageIndex:D4}.png");
            page.Save(path, ImageFormat.Png);
            tailPages.Add(path);
            generated++;
        }

        return generated;
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

    private sealed class FastBitmap : IDisposable
    {
        private readonly Bitmap bitmap;
        private readonly BitmapData data;
        private readonly byte[] bytes;

        public int Width => bitmap.Width;
        public int Height => bitmap.Height;

        public FastBitmap(Bitmap source)
        {
            bitmap = source.Clone(
                new Rectangle(0, 0, source.Width, source.Height),
                PixelFormat.Format24bppRgb);
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);
            bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        }

        public int Luma(int x, int y)
        {
            int row = data.Stride >= 0
                ? y * data.Stride
                : (Height - 1 - y) * -data.Stride;
            int p = row + x * 3;
            return (bytes[p + 2] * 77 + bytes[p + 1] * 150 + bytes[p] * 29) >> 8;
        }

        public void Dispose()
        {
            bitmap.UnlockBits(data);
            bitmap.Dispose();
        }
    }
}
