#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRepairAlignmentResult(
    string ReportPath,
    int CandidateCount,
    int VerifiedCount,
    int AppliedCount,
    bool SegmentedTarget);

internal static class ShareXModRepairAlignmentGate
{
    private sealed record Candidate(
        string File,
        long LogicalStartY,
        long LogicalEndY,
        int ExpectedCropXPixel,
        int ExpectedCropWidthPixel,
        bool Stable,
        bool TimedOut,
        string[] Reasons);

    private sealed record Alignment(
        bool Passed,
        int CropX,
        int ShiftY,
        double Score,
        double SecondScore,
        double Gap,
        string Reason);

    private sealed record Audit(
        string File,
        long LogicalStartY,
        long LogicalEndY,
        bool Stable,
        bool Verified,
        bool Applied,
        int CropX,
        int ShiftY,
        double Score,
        double SecondScore,
        double UniquenessGap,
        string Decision,
        string Sha256,
        string[] Reasons);

    private sealed record SegmentPart(string FileName, int Width, int Height, long StartY);

    public static async Task<ShareXModRepairAlignmentResult?> TryApplyAsync(
        Bitmap? result,
        ShareXModV04Settings settings,
        ShareXModChromeRepairCaptureResult? capture)
    {
        if (result == null ||
            capture == null ||
            !settings.ChromeRepairAlignmentEnabled ||
            !File.Exists(capture.ManifestPath))
        {
            return null;
        }

        try
        {
            using JsonDocument manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(capture.ManifestPath));

            List<Candidate> candidates = ReadCandidates(manifest.RootElement);
            if (candidates.Count == 0)
            {
                return null;
            }

            bool segmented = ShareXModSegmentedOutputRegistry.TryGet(
                result,
                out string? segmentManifest,
                out string? finalPng,
                out int sourceWidth,
                out long sourceHeight) &&
                !string.IsNullOrWhiteSpace(segmentManifest) &&
                File.Exists(segmentManifest);

            using ITargetCanvas target = segmented
                ? new SegmentedTargetCanvas(result, segmentManifest!, finalPng, sourceWidth, sourceHeight)
                : new BitmapTargetCanvas(result);

            List<Audit> audit = new();
            int verified = 0;
            int applied = 0;

            foreach (Candidate candidate in candidates)
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(capture.ManifestPath)!,
                    candidate.File);

                if (!File.Exists(path))
                {
                    audit.Add(Failed(candidate, "candidate-file-missing"));
                    continue;
                }

                if (settings.ChromeRepairRequireStableRegion &&
                    (!candidate.Stable || candidate.TimedOut))
                {
                    audit.Add(Failed(candidate, "candidate-region-not-stable", path));
                    continue;
                }

                using Bitmap source = new(path);
                int targetWidth = target.Width;
                int expectedWidth = candidate.ExpectedCropWidthPixel > 0
                    ? candidate.ExpectedCropWidthPixel
                    : targetWidth;

                if (Math.Abs(expectedWidth - targetWidth) > Math.Max(4, targetWidth / 100))
                {
                    audit.Add(Failed(candidate, "candidate-width-mismatch", path));
                    continue;
                }

                int logicalHeight = checked((int)Math.Min(
                    int.MaxValue,
                    candidate.LogicalEndY - candidate.LogicalStartY));

                if (logicalHeight <= 0)
                {
                    audit.Add(Failed(candidate, "invalid-logical-range", path));
                    continue;
                }

                using Bitmap normalized = NormalizeHeight(source, logicalHeight);

                int radius = Math.Clamp(settings.ChromeRepairAlignmentSearchRadiusPx, 0, 1000);
                long windowStart = Math.Max(0, candidate.LogicalStartY - radius);
                long windowEnd = Math.Min(target.Height, candidate.LogicalEndY + radius);

                if (windowEnd <= windowStart || windowEnd - windowStart > int.MaxValue)
                {
                    audit.Add(Failed(candidate, "invalid-target-window", path));
                    continue;
                }

                using Bitmap targetWindow = target.ReadRegion(
                    windowStart,
                    (int)(windowEnd - windowStart));

                int expectedY = checked((int)(candidate.LogicalStartY - windowStart));
                Alignment alignment = FindAlignment(
                    normalized,
                    targetWindow,
                    candidate.ExpectedCropXPixel,
                    expectedY,
                    targetWidth,
                    settings);

                bool didApply = false;
                if (alignment.Passed)
                {
                    verified++;

                    if (settings.ChromeRepairAutoApplyEnabled)
                    {
                        int guard = Math.Clamp(
                            settings.RepairPlanMarginPx,
                            0,
                            normalized.Height / 3);

                        int replaceTop = candidate.LogicalStartY > 0 ? guard : 0;
                        int replaceBottom = candidate.LogicalEndY < target.Height ? guard : 0;
                        int replaceHeight = normalized.Height - replaceTop - replaceBottom;

                        if (replaceHeight >= settings.ChromeRepairMinimumReplaceHeightPx)
                        {
                            Rectangle sourceRect = new(
                                alignment.CropX,
                                replaceTop,
                                targetWidth,
                                replaceHeight);

                            long destinationY =
                                windowStart + expectedY + alignment.ShiftY + replaceTop;

                            if (destinationY >= 0 &&
                                destinationY + replaceHeight <= target.Height &&
                                sourceRect.Right <= normalized.Width)
                            {
                                target.WriteRegion(
                                    destinationY,
                                    normalized,
                                    sourceRect,
                                    Math.Clamp(settings.ChromeRepairSeamBlendPx, 0, 64));

                                didApply = true;
                                applied++;
                            }
                        }
                    }
                }

                audit.Add(new Audit(
                    candidate.File,
                    candidate.LogicalStartY,
                    candidate.LogicalEndY,
                    candidate.Stable,
                    alignment.Passed,
                    didApply,
                    alignment.CropX,
                    alignment.ShiftY,
                    alignment.Score,
                    alignment.SecondScore,
                    alignment.Gap,
                    didApply
                        ? "verified-and-applied"
                        : alignment.Passed
                            ? "verified-not-applied"
                            : alignment.Reason,
                    ComputeSha256(path),
                    candidate.Reasons));
            }

            if (applied > 0)
            {
                target.Flush();
            }

            string directory = ResolveReportDirectory(capture.ManifestPath);
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("repair-alignment", directory);

            string reportPath = Path.Combine(directory, "repair-alignment.json");
            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Repair Alignment Gate",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                target = segmented ? "segmented" : "in-memory",
                targetWidth = target.Width,
                targetHeight = target.Height,
                candidateCount = candidates.Count,
                verifiedCount = verified,
                appliedCount = applied,
                autoApplyEnabled = settings.ChromeRepairAutoApplyEnabled,
                thresholds = new
                {
                    settings.ChromeRepairAlignmentSearchRadiusPx,
                    settings.ChromeRepairAlignmentHorizontalSearchPx,
                    settings.ChromeRepairAlignmentBandHeightPx,
                    settings.ChromeRepairAlignmentMaxScore,
                    settings.ChromeRepairAlignmentMinUniquenessGap
                },
                audit
            }, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(reportPath, json, new UTF8Encoding(false));

            return new ShareXModRepairAlignmentResult(
                reportPath,
                candidates.Count,
                verified,
                applied,
                segmented);
        }
        catch
        {
            return null;
        }
    }

    private static Alignment FindAlignment(
        Bitmap candidate,
        Bitmap target,
        int expectedCropX,
        int expectedY,
        int targetWidth,
        ShareXModV04Settings settings)
    {
        int xRadius = Math.Clamp(settings.ChromeRepairAlignmentHorizontalSearchPx, 0, 500);
        int yRadius = Math.Clamp(settings.ChromeRepairAlignmentSearchRadiusPx, 0, 500);
        int minX = Math.Max(0, expectedCropX - xRadius);
        int maxX = Math.Min(candidate.Width - targetWidth, expectedCropX + xRadius);
        int minY = Math.Max(0, expectedY - yRadius);
        int maxY = Math.Min(target.Height - candidate.Height, expectedY + yRadius);

        if (maxX < minX || maxY < minY)
        {
            return new Alignment(false, 0, 0, double.MaxValue, double.MaxValue, 0, "search-window-invalid");
        }

        int band = Math.Clamp(settings.ChromeRepairAlignmentBandHeightPx, 48, 320);
        band = Math.Min(band, Math.Max(24, candidate.Height / 4));

        using FastBitmap c = new(candidate);
        using FastBitmap t = new(target);

        (int x, int y, double score) best = (expectedCropX, expectedY, double.MaxValue);
        (int x, int y, double score) second = (expectedCropX, expectedY, double.MaxValue);

        int coarse = Math.Clamp(settings.ChromeRepairAlignmentCoarseSampleStep, 4, 32);
        for (int y = minY; y <= maxY; y += 8)
        {
            for (int x = minX; x <= maxX; x += 8)
            {
                double score = Score(c, t, x, y, targetWidth, band, coarse);
                UpdateBest(x, y, score, ref best, ref second);
            }
        }

        int fine = Math.Clamp(settings.ChromeRepairAlignmentFineSampleStep, 2, 16);
        var refinedBest = (x: best.x, y: best.y, score: double.MaxValue);
        var refinedSecond = (x: best.x, y: best.y, score: double.MaxValue);

        for (int y = Math.Max(minY, best.y - 10); y <= Math.Min(maxY, best.y + 10); y++)
        {
            for (int x = Math.Max(minX, best.x - 10); x <= Math.Min(maxX, best.x + 10); x++)
            {
                double score = Score(c, t, x, y, targetWidth, band, fine);
                UpdateBest(x, y, score, ref refinedBest, ref refinedSecond);
            }
        }

        double gap = refinedSecond.score < double.MaxValue
            ? refinedSecond.score - refinedBest.score
            : double.MaxValue;

        bool pass = refinedBest.score <= settings.ChromeRepairAlignmentMaxScore &&
                    gap >= settings.ChromeRepairAlignmentMinUniquenessGap;

        return new Alignment(
            pass,
            refinedBest.x,
            refinedBest.y - expectedY,
            refinedBest.score,
            refinedSecond.score,
            gap,
            pass ? "verified" : refinedBest.score > settings.ChromeRepairAlignmentMaxScore
                ? "alignment-score-too-high"
                : "alignment-not-unique");
    }

    private static double Score(
        FastBitmap candidate,
        FastBitmap target,
        int cropX,
        int targetY,
        int width,
        int bandHeight,
        int step)
    {
        double sum = 0;
        int count = 0;

        int[] bandStarts =
        {
            0,
            Math.Max(0, candidate.Height - bandHeight)
        };

        foreach (int bandY in bandStarts)
        {
            for (int y = bandY + 2; y < bandY + bandHeight - 2; y += step)
            {
                int ty = targetY + y;
                if (ty < 2 || ty >= target.Height - 2) continue;

                for (int x = Math.Max(2, width / 20); x < width - Math.Max(2, width / 20); x += step)
                {
                    int cx = cropX + x;
                    if (cx < 2 || cx >= candidate.Width - 2 || x >= target.Width - 2) continue;

                    int c0 = candidate.Luma(cx, y);
                    int t0 = target.Luma(x, ty);
                    int cgx = candidate.Luma(cx + 1, y) - candidate.Luma(cx - 1, y);
                    int tgx = target.Luma(x + 1, ty) - target.Luma(x - 1, ty);
                    int cgy = candidate.Luma(cx, y + 1) - candidate.Luma(cx, y - 1);
                    int tgy = target.Luma(x, ty + 1) - target.Luma(x, ty - 1);

                    sum += Math.Abs(c0 - t0) * 0.25 +
                           (Math.Abs(cgx - tgx) + Math.Abs(cgy - tgy)) * 0.375;
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : double.MaxValue;
    }

    private static void UpdateBest(
        int x,
        int y,
        double score,
        ref (int x, int y, double score) best,
        ref (int x, int y, double score) second)
    {
        if (score < best.score)
        {
            second = best;
            best = (x, y, score);
        }
        else if (score < second.score && (x != best.x || y != best.y))
        {
            second = (x, y, score);
        }
    }

    private static Bitmap NormalizeHeight(Bitmap source, int height)
    {
        if (source.Height == height)
        {
            return source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb);
        }

        Bitmap output = new(source.Width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(output);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source,
            new Rectangle(0, 0, output.Width, output.Height),
            new Rectangle(0, 0, source.Width, source.Height),
            GraphicsUnit.Pixel);
        return output;
    }

    private static List<Candidate> ReadCandidates(JsonElement root)
    {
        List<Candidate> result = new();
        if (!root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in files.EnumerateArray())
        {
            string file = GetString(item, "File", "file");
            if (file.Length == 0 ||
                !GetLong(item, "LogicalStartY", "logicalStartY", out long start) ||
                !GetLong(item, "LogicalEndY", "logicalEndY", out long end) ||
                end <= start)
            {
                continue;
            }

            int cropX = GetInt(item, "ExpectedCropXPixel", "expectedCropXPixel", 0);
            int cropWidth = GetInt(item, "ExpectedCropWidthPixel", "expectedCropWidthPixel", 0);

            bool stable = false;
            bool timedOut = true;
            if (item.TryGetProperty("Stability", out JsonElement stability) ||
                item.TryGetProperty("stability", out stability))
            {
                if (stability.ValueKind == JsonValueKind.Object)
                {
                    stable = GetBool(stability, "Stable", "stable");
                    timedOut = GetBool(stability, "TimedOut", "timedOut");
                }
            }

            result.Add(new Candidate(file, start, end, cropX, cropWidth, stable, timedOut, ReadReasons(item)));
        }

        return result;
    }

    private static Audit Failed(Candidate c, string reason, string? path = null) =>
        new(c.File, c.LogicalStartY, c.LogicalEndY, c.Stable, false, false, 0, 0,
            double.MaxValue, double.MaxValue, 0, reason,
            path != null && File.Exists(path) ? ComputeSha256(path) : string.Empty,
            c.Reasons);

    private static string[] ReadReasons(JsonElement item)
    {
        JsonElement reasons;
        if (!item.TryGetProperty("Reasons", out reasons) && !item.TryGetProperty("reasons", out reasons))
        {
            return Array.Empty<string>();
        }

        return reasons.ValueKind == JsonValueKind.Array
            ? reasons.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
    }

    private static string GetString(JsonElement item, string a, string b)
    {
        if (item.TryGetProperty(a, out JsonElement x)) return x.GetString() ?? string.Empty;
        return item.TryGetProperty(b, out JsonElement y) ? y.GetString() ?? string.Empty : string.Empty;
    }

    private static bool GetLong(JsonElement item, string a, string b, out long value)
    {
        value = 0;
        return (item.TryGetProperty(a, out JsonElement x) && x.TryGetInt64(out value)) ||
               (item.TryGetProperty(b, out JsonElement y) && y.TryGetInt64(out value));
    }

    private static int GetInt(JsonElement item, string a, string b, int fallback)
    {
        if (item.TryGetProperty(a, out JsonElement x) && x.TryGetInt32(out int v)) return v;
        if (item.TryGetProperty(b, out JsonElement y) && y.TryGetInt32(out v)) return v;
        return fallback;
    }

    private static bool GetBool(JsonElement item, string a, string b)
    {
        if (item.TryGetProperty(a, out JsonElement x)) return x.ValueKind == JsonValueKind.True;
        return item.TryGetProperty(b, out JsonElement y) && y.ValueKind == JsonValueKind.True;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ResolveReportDirectory(string manifestPath)
    {
        string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
        return !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "repair-alignment")
            : Path.Combine(Path.GetDirectoryName(manifestPath)!, "alignment");
    }

    private interface ITargetCanvas : IDisposable
    {
        int Width { get; }
        long Height { get; }
        Bitmap ReadRegion(long startY, int height);
        void WriteRegion(long destinationY, Bitmap source, Rectangle sourceRect, int blendPx);
        void Flush();
    }

    private sealed class BitmapTargetCanvas : ITargetCanvas
    {
        private readonly Bitmap target;
        public BitmapTargetCanvas(Bitmap target) => this.target = target;
        public int Width => target.Width;
        public long Height => target.Height;

        public Bitmap ReadRegion(long startY, int height)
        {
            int y = checked((int)startY);
            return target.Clone(
                new Rectangle(0, y, target.Width, Math.Min(height, target.Height - y)),
                PixelFormat.Format32bppArgb);
        }

        public void WriteRegion(long destinationY, Bitmap source, Rectangle sourceRect, int blendPx)
        {
            int y = checked((int)destinationY);
            using Graphics g = Graphics.FromImage(target);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(source,
                new Rectangle(0, y, sourceRect.Width, sourceRect.Height),
                sourceRect,
                GraphicsUnit.Pixel);
        }

        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class SegmentedTargetCanvas : ITargetCanvas
    {
        private readonly Bitmap preview;
        private readonly string manifestPath;
        private readonly string? finalPng;
        private readonly string directory;
        private readonly List<SegmentPart> parts;
        private readonly int width;
        private readonly long height;
        private bool dirty;

        public SegmentedTargetCanvas(
            Bitmap preview,
            string manifestPath,
            string? finalPng,
            int width,
            long height)
        {
            this.preview = preview;
            this.manifestPath = manifestPath;
            this.finalPng = finalPng;
            this.width = width;
            this.height = height;
            directory = Path.GetDirectoryName(manifestPath)!;
            parts = ReadParts(manifestPath);
            if (parts.Count == 0) throw new InvalidOperationException("No segment parts.");
        }

        public int Width => width;
        public long Height => height;

        public Bitmap ReadRegion(long startY, int heightRequested)
        {
            long endY = Math.Min(height, startY + heightRequested);
            Bitmap output = new(width, checked((int)(endY - startY)), PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(output);
            g.Clear(Color.White);
            g.CompositingMode = CompositingMode.SourceCopy;

            foreach (SegmentPart part in parts)
            {
                long partEnd = part.StartY + part.Height;
                long s = Math.Max(startY, part.StartY);
                long e = Math.Min(endY, partEnd);
                if (e <= s) continue;

                using Bitmap bitmap = new(Path.Combine(directory, part.FileName));
                int srcY = checked((int)(s - part.StartY));
                int dstY = checked((int)(s - startY));
                int h = checked((int)(e - s));
                g.DrawImage(bitmap,
                    new Rectangle(0, dstY, width, h),
                    new Rectangle(0, srcY, width, h),
                    GraphicsUnit.Pixel);
            }

            return output;
        }

        public void WriteRegion(long destinationY, Bitmap source, Rectangle sourceRect, int blendPx)
        {
            long destinationEnd = destinationY + sourceRect.Height;

            foreach (SegmentPart part in parts)
            {
                long partEnd = part.StartY + part.Height;
                long s = Math.Max(destinationY, part.StartY);
                long e = Math.Min(destinationEnd, partEnd);
                if (e <= s) continue;

                string path = Path.Combine(directory, part.FileName);
                using Bitmap bitmap = new(path);
                int targetY = checked((int)(s - part.StartY));
                int sourceY = sourceRect.Y + checked((int)(s - destinationY));
                int h = checked((int)(e - s));

                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(source,
                        new Rectangle(0, targetY, width, h),
                        new Rectangle(sourceRect.X, sourceY, width, h),
                        GraphicsUnit.Pixel);
                }

                string temp = path + ".repair.tmp.png";
                bitmap.Save(temp, ImageFormat.Png);
                File.Move(temp, path, true);
                dirty = true;
            }
        }

        public void Flush()
        {
            if (!dirty) return;

            if (!string.IsNullOrWhiteSpace(finalPng))
            {
                string[] paths = parts.Select(x => Path.Combine(directory, x.FileName)).ToArray();
                ShareXModSegmentedPngWriter.Write(finalPng!, paths, width, height);
            }

            using Graphics g = Graphics.FromImage(preview);
            g.Clear(Color.White);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double scaleY = preview.Height / (double)Math.Max(1, height);

            foreach (SegmentPart part in parts)
            {
                using Bitmap bitmap = new(Path.Combine(directory, part.FileName));
                int y = (int)Math.Round(part.StartY * scaleY);
                int h = Math.Max(1, (int)Math.Round(part.Height * scaleY));
                g.DrawImage(bitmap,
                    new Rectangle(0, y, preview.Width, h),
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    GraphicsUnit.Pixel);
            }
        }

        public void Dispose() { }

        private static List<SegmentPart> ReadParts(string manifestPath)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("parts", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return new List<SegmentPart>();
            }

            List<SegmentPart> result = new();
            foreach (JsonElement item in items.EnumerateArray())
            {
                string file = GetString(item, "FileName", "fileName");
                int width = GetInt(item, "Width", "width", 0);
                int height = GetInt(item, "Height", "height", 0);
                if (file.Length == 0 || width <= 0 || height <= 0 || !GetLong(item, "StartY", "startY", out long startY)) continue;
                result.Add(new SegmentPart(file, width, height, startY));
            }
            return result.OrderBy(x => x.StartY).ToList();
        }
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
            int row = data.Stride >= 0 ? y * data.Stride : (Height - 1 - y) * -data.Stride;
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
