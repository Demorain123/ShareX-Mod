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

internal static class ShareXModCaptureQualityGuard
{
    public static ShareXModCaptureQualitySession? TryCreate(Rectangle captureRectangle)
    {
        try
        {
            return new ShareXModCaptureQualitySession(captureRectangle);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class ShareXModCaptureQualitySession : IDisposable
{
    private readonly Rectangle captureRectangle;
    private readonly string directory;
    private readonly List<FrameEntry> frames = new();
    private readonly List<SuspectRange> suspectRanges = new();

    private int capturedFrameIndex;
    private long logicalTop;
    private ShareXModSettleResult? pendingSettle;
    private bool disposed;

    internal ShareXModCaptureQualitySession(Rectangle captureRectangle)
    {
        this.captureRectangle = captureRectangle;
        directory = CreateSessionDirectory();

        WriteJson(Path.Combine(directory, "capture-map.json"), new
        {
            format = "ShareX-Mod Capture Map",
            version = "0.4.2-dev",
            final = false,
            created = DateTimeOffset.Now,
            captureRectangle = new
            {
                captureRectangle.X,
                captureRectangle.Y,
                captureRectangle.Width,
                captureRectangle.Height
            }
        });
    }

    public void OnFrameCaptured(Bitmap frame)
    {
        capturedFrameIndex++;

        if (capturedFrameIndex == 1)
        {
            StripQuality quality = AnalyzeStrip(frame, 0, frame.Height);
            frames.Add(new FrameEntry(
                capturedFrameIndex,
                0,
                frame.Height,
                0,
                0,
                -1,
                0,
                false,
                0,
                quality.MeanLuma,
                quality.Complexity,
                quality.BlankRatio,
                false,
                Array.Empty<string>()));
        }
    }

    public void OnSettle(ShareXModSettleResult result)
    {
        pendingSettle = result;
    }

    public void OnAppend(
        int estimatedDelta,
        int actualGrowth,
        double anchorScore,
        int agreementCount,
        bool fallbackCombined,
        int repairedOverlayTiles,
        Bitmap frame)
    {
        int delta = estimatedDelta > 0 ? estimatedDelta : actualGrowth;
        delta = Math.Clamp(delta, 1, Math.Max(1, frame.Height - 1));

        logicalTop += delta;
        long logicalBottom = logicalTop + frame.Height;
        long exposedTop = Math.Max(logicalTop, logicalBottom - delta);

        StripQuality exposed = AnalyzeStrip(frame, Math.Max(0, frame.Height - delta), delta);
        List<string> reasons = new();

        int tolerance = Math.Max(40, (int)Math.Round(delta * 0.30));
        if (actualGrowth <= 0 || Math.Abs(actualGrowth - delta) > tolerance)
        {
            reasons.Add("growth-mismatch");
        }

        if (fallbackCombined)
        {
            reasons.Add("fallback-join");
        }

        if (agreementCount > 0 && agreementCount < 2)
        {
            reasons.Add("weak-anchor-agreement");
        }

        if (anchorScore >= 0 && anchorScore > 12.0)
        {
            reasons.Add("weak-anchor-score");
        }

        if (exposed.BlankRatio >= 0.82 && exposed.Complexity <= 1.0)
        {
            reasons.Add("blank-like-new-strip");
        }

        if (pendingSettle is ShareXModSettleResult settle)
        {
            if (settle.TimedOut)
            {
                reasons.Add("settle-timeout");
            }

            if (settle.BlankLike)
            {
                reasons.Add("settle-blank-like");
            }
        }

        bool suspect = reasons.Count > 0;

        frames.Add(new FrameEntry(
            capturedFrameIndex,
            logicalTop,
            logicalBottom,
            delta,
            actualGrowth,
            anchorScore,
            agreementCount,
            fallbackCombined,
            repairedOverlayTiles,
            exposed.MeanLuma,
            exposed.Complexity,
            exposed.BlankRatio,
            suspect,
            reasons.ToArray()));

        if (suspect)
        {
            suspectRanges.Add(new SuspectRange(
                exposedTop,
                logicalBottom,
                reasons.ToArray()));
        }

        pendingSettle = null;
    }

    public void Complete(ScrollingCaptureStatus status, Bitmap? result)
    {
        if (disposed) return;

        List<SuspectRange> merged = MergeSuspectRanges(suspectRanges);

        int score = 100;
        foreach (FrameEntry frame in frames)
        {
            if (!frame.Suspect) continue;

            if (frame.Reasons.Contains("growth-mismatch")) score -= 20;
            if (frame.Reasons.Contains("settle-timeout")) score -= 12;
            if (frame.Reasons.Contains("settle-blank-like")) score -= 10;
            if (frame.Reasons.Contains("blank-like-new-strip")) score -= 10;
            if (frame.Reasons.Contains("fallback-join")) score -= 6;
            if (frame.Reasons.Contains("weak-anchor-score") ||
                frame.Reasons.Contains("weak-anchor-agreement")) score -= 4;
        }

        int uncommittedFrames = Math.Max(0, capturedFrameIndex - frames.Count);
        if (uncommittedFrames > 0)
        {
            score -= Math.Min(25, uncommittedFrames * 8);
        }

        score = Math.Clamp(score, 0, 100);
        string rating = score >= 90 ? "high" : score >= 75 ? "medium" : "low";

        WriteJson(Path.Combine(directory, "capture-map.json"), new
        {
            format = "ShareX-Mod Capture Map",
            version = "0.4.2-dev",
            final = true,
            created = DateTimeOffset.Now,
            completed = DateTimeOffset.Now,
            status = status.ToString(),
            rating,
            integrityScore = score,
            capturedFrameCount = capturedFrameIndex,
            committedFrameCount = frames.Count,
            uncommittedFrames,
            captureRectangle = new
            {
                captureRectangle.X,
                captureRectangle.Y,
                captureRectangle.Width,
                captureRectangle.Height
            },
            result = new
            {
                width = result?.Width ?? 0,
                height = result?.Height ?? 0
            },
            logicalCapturedHeight = frames.Count > 0 ? frames.Max(x => x.LogicalBottom) : 0,
            suspectRangeCount = merged.Count,
            suspectRanges = merged,
            frames
        });
    }

    public void Dispose()
    {
        disposed = true;
    }

    private static StripQuality AnalyzeStrip(Bitmap source, int top, int height)
    {
        top = Math.Clamp(top, 0, Math.Max(0, source.Height - 1));
        height = Math.Clamp(height, 1, source.Height - top);

        int width = 80;
        int probeHeight = Math.Clamp((int)Math.Round(height * (48.0 / source.Height)), 8, 48);

        using Bitmap probe = new(width, probeHeight, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(probe))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Low;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, probeHeight),
                new Rectangle(0, top, source.Width, height),
                GraphicsUnit.Pixel);
        }

        BitmapData data = probe.LockBits(
            new Rectangle(0, 0, width, probeHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            byte[] row = new byte[Math.Abs(data.Stride)];
            long lumaSum = 0;
            long edgeSum = 0;
            int pixels = 0;
            int edges = 0;
            int blankPixels = 0;

            for (int y = 0; y < probeHeight; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                int previous = -1;

                for (int x = 0; x < width; x++)
                {
                    int p = x * 3;
                    int luma = (row[p + 2] * 77 + row[p + 1] * 150 + row[p] * 29) >> 8;

                    lumaSum += luma;
                    pixels++;
                    if (luma >= 247) blankPixels++;

                    if (previous >= 0)
                    {
                        edgeSum += Math.Abs(luma - previous);
                        edges++;
                    }

                    previous = luma;
                }
            }

            return new StripQuality(
                pixels > 0 ? lumaSum / (double)pixels : 0,
                edges > 0 ? edgeSum / (double)edges : 0,
                pixels > 0 ? blankPixels / (double)pixels : 0);
        }
        finally
        {
            probe.UnlockBits(data);
        }
    }

    private static List<SuspectRange> MergeSuspectRanges(IEnumerable<SuspectRange> input)
    {
        List<SuspectRange> ordered = input
            .OrderBy(x => x.StartY)
            .ThenBy(x => x.EndY)
            .ToList();

        List<SuspectRange> merged = new();

        foreach (SuspectRange item in ordered)
        {
            if (merged.Count == 0 || item.StartY > merged[^1].EndY + 32)
            {
                merged.Add(item);
                continue;
            }

            SuspectRange previous = merged[^1];
            string[] reasons = previous.Reasons
                .Concat(item.Reasons)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            merged[^1] = new SuspectRange(
                previous.StartY,
                Math.Max(previous.EndY, item.EndY),
                reasons);
        }

        return merged;
    }

    private static string CreateSessionDirectory()
    {
        string name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}";

        string preferred = Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "CaptureQualitySessions",
            name);

        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX-Mod",
                "CaptureQualitySessions",
                name);

            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static void WriteJson(string path, object value)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private readonly record struct StripQuality(
        double MeanLuma,
        double Complexity,
        double BlankRatio);

    private sealed record FrameEntry(
        int Frame,
        long LogicalTop,
        long LogicalBottom,
        int EstimatedScrollDelta,
        int ActualGrowth,
        double AnchorScore,
        int AnchorAgreementCount,
        bool FallbackCombined,
        int RepairedOverlayTiles,
        double NewStripMeanLuma,
        double NewStripComplexity,
        double NewStripBlankRatio,
        bool Suspect,
        string[] Reasons);

    private sealed record SuspectRange(
        long StartY,
        long EndY,
        string[] Reasons);
}
