#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Raw-frame flight recorder used to make real-site failures replayable without asking the user to
/// repeat the capture after every compositor change. Recording is opt-in from LongCapture Debug.
/// Frames are stored as lossless BMP for low encoder overhead and exact pixel replay.
/// </summary>
public static class ShareXModReplayDiagnostics
{
    private static readonly object Sync = new();
    private static bool enabled;
    private static string? activeSessionId;
    private static string? activeDirectory;
    private static int nextFrameIndex;
    private static int lastFrameIndex = -1;

    public static bool Enabled
    {
        get { lock (Sync) return enabled; }
    }

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
                string path = Path.Combine(directory, fileName);
                frame.Save(path, ImageFormat.Bmp);

                AppendJsonLine(Path.Combine(directory, "frames.jsonl"), new
                {
                    index,
                    timestamp = DateTimeOffset.Now,
                    file = fileName,
                    frame.Width,
                    frame.Height,
                    captureRectangle = new
                    {
                        captureRectangle.X,
                        captureRectangle.Y,
                        captureRectangle.Width,
                        captureRectangle.Height
                    },
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
        catch
        {
            // Diagnostics must never invalidate a capture.
        }
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
        catch
        {
        }
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
                "LongCapture v0.1.6 raw-frame replay evidence. Keep this directory/diagnostics ZIP when reporting a real-site stitch failure.\r\n",
                new UTF8Encoding(false));
        }

        return activeDirectory;
    }

    private static void AppendJsonLine(string path, object value)
    {
        File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine, new UTF8Encoding(false));
    }
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
            : root;

        string[] frames = Directory.GetFiles(rawDirectory, "frame-*.bmp")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (frames.Length < 2)
            throw new InvalidOperationException($"Replay requires at least two raw frames. Found {frames.Length} in {rawDirectory}.");

        Dictionary<int, int> recordedDeltas = LoadRecordedDeltas(Path.Combine(rawDirectory, "anchors.jsonl"));
        Bitmap? result = null;
        Bitmap? previous = null;
        var replay = new ShareXModDelayedCompositorV016.Session();

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

                int delta = recordedDeltas.TryGetValue(i, out int savedDelta) && savedDelta > 0
                    ? savedDelta
                    : EstimateDelta(previous, current);
                if (delta <= 0 || delta >= current.Height)
                    throw new InvalidOperationException($"Replay could not resolve a valid scroll delta for frame {i}: {delta}.");

                Bitmap? next = replay.TryAppend(result, previous, current, delta);
                if (next is null)
                    throw new InvalidOperationException($"Replay compositor rejected frame {i} with delta={delta}.");

                result.Dispose();
                result = next;
                previous.Dispose();
                previous = (Bitmap)current.Clone();
            }

            outputPath ??= Path.Combine(root, $"LongCapture-Replay-v016-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            result!.Save(outputPath, ImageFormat.Png);

            string manifestPath = Path.ChangeExtension(outputPath, ".json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                format = "LongCapture Offline Replay",
                version = "0.1.6",
                created = DateTimeOffset.Now,
                rawDirectory,
                frameCount = frames.Length,
                outputPath,
                outputWidth = result.Width,
                outputHeight = result.Height,
                telemetry = replay.SnapshotTelemetry()
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            return outputPath;
        }
        finally
        {
            previous?.Dispose();
            result?.Dispose();
            replay.Dispose();
        }
    }

    private static int EstimateDelta(Bitmap previous, Bitmap current)
    {
        return ShareXModAnchorMatcher.TryEstimateScrollDelta(previous, current, out ShareXModAnchorMatch match)
            ? match.ScrollDelta
            : 0;
    }

    private static Dictionary<int, int> LoadRecordedDeltas(string path)
    {
        var result = new Dictionary<int, int>();
        if (!File.Exists(path)) return result;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("hasAnchor", out JsonElement has) || !has.GetBoolean()) continue;
                int frameIndex = root.GetProperty("frameIndex").GetInt32();
                int delta = root.GetProperty("scrollDelta").GetInt32();
                if (delta > 0) result[frameIndex] = delta;
            }
            catch
            {
            }
        }
        return result;
    }
}
