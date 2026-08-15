#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Lossless raw-frame flight recorder. Capture uses BMP to keep encoder work out of the scrolling
/// loop. Diagnostic export may convert these BMPs to PNG after capture so the complete replay set
/// can be shared without hundreds of megabytes of uncompressed pixels.
/// </summary>
public static class ShareXModReplayDiagnostics
{
    private static readonly object Sync = new();
    private static bool enabled;
    private static string? activeSessionId;
    private static string? activeDirectory;
    private static int nextFrameIndex;
    private static int lastFrameIndex = -1;

    public static bool Enabled { get { lock (Sync) return enabled; } }

    public static void Configure(bool value)
    {
        lock (Sync)
        {
            enabled = value;
            if (!value)
            {
                activeSessionId = null;
                activeDirectory = null;
                nextFrameIndex = 0;
                lastFrameIndex = -1;
            }
        }
    }

    internal static void RecordRawFrame(Bitmap frame, Rectangle captureRectangle, ScrollingCaptureOptions options)
    {
        if (frame is null || !Enabled) return;
        try
        {
            lock (Sync)
            {
                string? directory = EnsureDirectoryUnsafe();
                if (directory is null) return;
                int index = nextFrameIndex++;
                lastFrameIndex = index;
                string fileName = $"frame-{index:D4}.bmp";
                frame.Save(Path.Combine(directory, fileName), ImageFormat.Bmp);
                AppendJsonLine(Path.Combine(directory, "frames.jsonl"), new
                {
                    index,
                    timestamp = DateTimeOffset.Now,
                    file = fileName,
                    frame.Width,
                    frame.Height,
                    captureRectangle = new { captureRectangle.X, captureRectangle.Y, captureRectangle.Width, captureRectangle.Height },
                    options = new
                    {
                        options.StartDelay,
                        options.ScrollDelay,
                        options.ScrollAmount,
                        ScrollMethod = options.ScrollMethod.ToString(),
                        options.AutoScrollTop,
                        options.AutoIgnoreBottomEdge,
                        options.ShowRegion
                    }
                });
            }
        }
        catch { }
    }

    internal static void RecordAnchor(bool hasAnchor, ShareXModAnchorMatch match)
    {
        if (!Enabled) return;
        try
        {
            lock (Sync)
            {
                string? directory = EnsureDirectoryUnsafe();
                if (directory is null || lastFrameIndex < 0) return;
                AppendJsonLine(Path.Combine(directory, "anchors.jsonl"), new
                {
                    frameIndex = lastFrameIndex,
                    timestamp = DateTimeOffset.Now,
                    hasAnchor,
                    scrollDelta = hasAnchor ? match.ScrollDelta : 0,
                    score = hasAnchor ? match.Score : -1,
                    agreementCount = hasAnchor ? match.AgreementCount : 0
                });
            }
        }
        catch { }
    }

    internal static void RecordResolution(bool resolved, int delta, string source, double score)
    {
        if (!Enabled) return;
        try
        {
            lock (Sync)
            {
                string? directory = EnsureDirectoryUnsafe();
                if (directory is null || lastFrameIndex < 0) return;
                AppendJsonLine(Path.Combine(directory, "resolutions-v017.jsonl"), new
                {
                    frameIndex = lastFrameIndex,
                    timestamp = DateTimeOffset.Now,
                    resolved,
                    scrollDelta = resolved ? delta : 0,
                    source,
                    score = double.IsFinite(score) ? score : -1
                });
            }
        }
        catch { }
    }

    private static string? EnsureDirectoryUnsafe()
    {
        string? sessionId = ShareXModCaptureSessionContext.CurrentSessionId;
        string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(root)) return null;

        if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            activeSessionId = sessionId;
            activeDirectory = Path.Combine(root, "raw-frames-v016");
            Directory.CreateDirectory(activeDirectory);
            ShareXModCaptureSessionContext.RegisterComponent("raw-frames-v016", activeDirectory);
            nextFrameIndex = 0;
            lastFrameIndex = -1;
            File.WriteAllText(
                Path.Combine(activeDirectory, "README.txt"),
                "LongCapture raw-frame replay evidence. v0.1.7 keeps every resolved transition on the delayed-compositor path; diagnostics export converts BMP frames to PNG when needed.\r\n",
                new UTF8Encoding(false));
        }
        return activeDirectory;
    }

    private static void AppendJsonLine(string path, object value) =>
        File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine, new UTF8Encoding(false));
}

public static class ShareXModOfflineReplay
{
    public static string Replay(string sessionDirectory, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
            throw new ArgumentException("Replay session directory is required.", nameof(sessionDirectory));

        string root = Path.GetFullPath(sessionDirectory);
        string rawDirectory = Directory.Exists(Path.Combine(root, "raw-frames-v016"))
            ? Path.Combine(root, "raw-frames-v016")
            : Directory.Exists(Path.Combine(root, "engine-evidence", "raw-frames-v016"))
                ? Path.Combine(root, "engine-evidence", "raw-frames-v016")
                : root;

        string[] frames = Directory.EnumerateFiles(rawDirectory, "frame-*.*")
            .Where(x => string.Equals(Path.GetExtension(x), ".bmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetExtension(x), ".png", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(x => string.Equals(Path.GetExtension(x), ".bmp", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .OrderBy(x => Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (frames.Length < 2)
            throw new InvalidOperationException($"Replay requires at least two raw frames. Found {frames.Length} in {rawDirectory}.");

        Dictionary<int, int> recordedResolutions = LoadRecordedDeltas(Path.Combine(rawDirectory, "resolutions-v017.jsonl"), "resolved");
        Dictionary<int, int> recordedAnchors = LoadRecordedDeltas(Path.Combine(rawDirectory, "anchors.jsonl"), "hasAnchor");
        Bitmap? result = null;
        Bitmap? previous = null;
        using var replay = new ShareXModDelayedCompositorV016.Session();
        ShareXModAnchorContinuityV017.ResetLive();

        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                using Bitmap currentLoaded = new(frames[i]);
                using Bitmap current = new(currentLoaded);
                if (result is null)
                {
                    result = (Bitmap)current.Clone();
                    previous = (Bitmap)current.Clone();
                    continue;
                }
                if (previous is null) throw new InvalidOperationException("Replay previous frame state was lost.");

                int delta = 0;
                if (recordedResolutions.TryGetValue(i, out int savedResolution) && savedResolution > 0)
                {
                    delta = savedResolution;
                }
                else if (recordedAnchors.TryGetValue(i, out int savedAnchor) && savedAnchor > 0)
                {
                    delta = savedAnchor;
                    ShareXModAnchorContinuityV017.TryResolve(previous, current, true,
                        new ShareXModAnchorMatch(savedAnchor, 0, 3), out _, out _, out _);
                }
                else if (!ShareXModAnchorContinuityV017.TryResolve(previous, current, false, default,
                             out delta, out _, out _))
                {
                    throw new InvalidOperationException($"Replay could not safely resolve scroll delta for frame {i}; legacy mosaic fallback is intentionally disabled.");
                }

                if (delta <= 0 || delta >= current.Height)
                    throw new InvalidOperationException($"Replay resolved invalid scroll delta for frame {i}: {delta}.");

                Bitmap? next = replay.TryAppend(result, previous, current, delta);
                if (next is null) throw new InvalidOperationException($"Replay compositor rejected frame {i} with delta={delta}.");
                result.Dispose();
                result = next;
                previous.Dispose();
                previous = (Bitmap)current.Clone();
            }

            outputPath ??= Path.Combine(root, $"LongCapture-Replay-v017-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            result!.Save(outputPath, ImageFormat.Png);
            File.WriteAllText(Path.ChangeExtension(outputPath, ".json"), JsonSerializer.Serialize(new
            {
                format = "LongCapture Offline Replay",
                version = "0.1.7",
                created = DateTimeOffset.Now,
                rawDirectory,
                frameCount = frames.Length,
                outputPath,
                outputWidth = result.Width,
                outputHeight = result.Height,
                compositor = replay.SnapshotTelemetry(),
                anchorContinuity = ShareXModAnchorContinuityV017.SnapshotTelemetry()
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return outputPath;
        }
        finally
        {
            previous?.Dispose();
            result?.Dispose();
            ShareXModAnchorContinuityV017.ResetLive();
        }
    }

    private static Dictionary<int, int> LoadRecordedDeltas(string path, string acceptedFlag)
    {
        var result = new Dictionary<int, int>();
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement item = document.RootElement;
                if (!item.TryGetProperty(acceptedFlag, out JsonElement accepted) || !accepted.GetBoolean()) continue;
                int frameIndex = item.GetProperty("frameIndex").GetInt32();
                int delta = item.GetProperty("scrollDelta").GetInt32();
                if (delta > 0) result[frameIndex] = delta;
            }
            catch { }
        }
        return result;
    }
}
