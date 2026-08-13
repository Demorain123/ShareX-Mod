#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModSegmentStore : IDisposable
{
    private readonly ShareXModV04Settings settings;
    private readonly string directory;
    private readonly List<PartInfo> parts = new();
    private long completedHeight;
    private bool finalized;

    private ShareXModSegmentStore(ShareXModV04Settings settings)
    {
        this.settings = settings;
        string root = ResolveOutputDirectory(settings.SegmentedOutputDirectory);
        directory = Path.Combine(root, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
    }

    public bool HasParts => parts.Count > 0;
    public string DirectoryPath => directory;
    public string FinalPngPath => Path.Combine(directory, "capture-full.png");

    public static ShareXModSegmentStore? TryCreate(ShareXModV04Settings settings)
    {
        if (!settings.SmartSegmentationEnabled) return null;
        try { return new ShareXModSegmentStore(settings); } catch { return null; }
    }

    public Bitmap? SegmentIfNeeded(Bitmap? result)
    {
        if (result == null || finalized || result.Height < settings.SegmentMaxWorkingHeight) return null;
        int cutY = ShareXModNaturalCutPlanner.FindCut(result, settings);
        cutY = Math.Clamp(cutY, settings.SegmentMinimumCutDistance, result.Height - 1024);

        using Bitmap completed = result.Clone(new Rectangle(0, 0, result.Width, cutY), PixelFormat.Format32bppArgb);
        SavePart(completed);
        completedHeight += cutY;
        WriteManifest(false, result.Width, completedHeight + result.Height - cutY);
        return result.Clone(new Rectangle(0, cutY, result.Width, result.Height - cutY), PixelFormat.Format32bppArgb);
    }

    public Bitmap? FinalizeAndCreatePreview(Bitmap? tail)
    {
        if (tail == null || finalized || parts.Count == 0) return null;
        finalized = true;
        SavePart(tail);
        completedHeight += tail.Height;
        BuildFinalPng(tail.Width, completedHeight);
        WriteManifest(true, tail.Width, completedHeight);
        return CreatePreview(tail.Width, completedHeight);
    }

    public Bitmap? AppendTailPartsAndCreatePreview(IEnumerable<string> tailPaths)
    {
        if (!finalized || parts.Count == 0 || tailPaths == null) return null;

        int expectedWidth = parts[0].Width;
        int appended = 0;

        foreach (string path in tailPaths.Where(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x)))
        {
            try
            {
                using Bitmap source = new(path);
                if (source.Width != expectedWidth || source.Height < 1) continue;
                SavePart(source);
                completedHeight += source.Height;
                appended++;
            }
            catch { }
        }

        if (appended == 0) return null;

        BuildFinalPng(expectedWidth, completedHeight);
        WriteManifest(true, expectedWidth, completedHeight);
        return CreatePreview(expectedWidth, completedHeight);
    }

    private void BuildFinalPng(int width, long height)
    {
        try
        {
            string[] paths = parts.Select(x => Path.Combine(directory, x.FileName)).ToArray();
            ShareXModSegmentedPngWriter.Write(FinalPngPath, paths, width, height);
        }
        catch
        {
            try { if (File.Exists(FinalPngPath)) File.Delete(FinalPngPath); } catch { }
        }
    }

    private void SavePart(Bitmap bitmap)
    {
        string fileName = $"part_{parts.Count + 1:D4}.png";
        bitmap.Save(Path.Combine(directory, fileName), ImageFormat.Png);
        parts.Add(new PartInfo(fileName, bitmap.Width, bitmap.Height, completedHeight));
    }

    private Bitmap CreatePreview(int sourceWidth, long sourceHeight)
    {
        int maxHeight = Math.Clamp(settings.SegmentPreviewMaxHeight, 2000, 30000);
        double scale = Math.Min(1.0, maxHeight / (double)sourceHeight);
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        Bitmap preview = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(preview);
        g.Clear(Color.White);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int y = 0;
        foreach (PartInfo partInfo in parts)
        {
            using Bitmap part = new(Path.Combine(directory, partInfo.FileName));
            int partHeight = Math.Max(1, (int)Math.Round(part.Height * scale));
            g.DrawImage(part, new Rectangle(0, y, width, partHeight), new Rectangle(0, 0, part.Width, part.Height), GraphicsUnit.Pixel);
            y += partHeight;
        }

        ShareXModSegmentedOutputRegistry.Register(preview, Path.Combine(directory, "manifest.json"), FinalPngPath, sourceWidth, sourceHeight);
        return preview;
    }

    private void WriteManifest(bool final, int width, long height)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod smart segmented capture",
                version = "0.4.1-dev",
                final,
                width,
                height,
                finalPng = File.Exists(FinalPngPath) ? Path.GetFileName(FinalPngPath) : null,
                partCount = parts.Count,
                parts,
                created = DateTimeOffset.Now
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string ResolveOutputDirectory(string configured)
    {
        string relative = string.IsNullOrWhiteSpace(configured) ? "ShareX-Mod\\SegmentedCaptures" : configured;
        string primary = Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
        try { Directory.CreateDirectory(primary); return primary; }
        catch
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShareX-Mod", "SegmentedCaptures");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public void Dispose() { }
    private sealed record PartInfo(string FileName, int Width, int Height, long StartY);
}

internal static class ShareXModSegmentedOutputRegistry
{
    private static readonly object Sync = new();
    private static WeakReference<Bitmap>? previewRef;
    private static string? manifestPath;
    private static string? finalPngPath;
    private static int sourceWidth;
    private static long sourceHeight;

    public static void Register(Bitmap preview, string manifest, string finalPng, int width, long height)
    {
        lock (Sync)
        {
            previewRef = new WeakReference<Bitmap>(preview);
            manifestPath = manifest;
            finalPngPath = finalPng;
            sourceWidth = width;
            sourceHeight = height;
        }
    }

    public static bool TryGet(Bitmap? bitmap, out string? manifest, out int width, out long height)
    {
        return TryGet(bitmap, out manifest, out _, out width, out height);
    }

    public static bool TryGet(Bitmap? bitmap, out string? manifest, out string? finalPng, out int width, out long height)
    {
        lock (Sync)
        {
            bool match = bitmap != null && previewRef != null && previewRef.TryGetTarget(out Bitmap? target) && ReferenceEquals(target, bitmap);
            manifest = match ? manifestPath : null;
            finalPng = match ? finalPngPath : null;
            width = match ? sourceWidth : 0;
            height = match ? sourceHeight : 0;
            return match;
        }
    }
}
