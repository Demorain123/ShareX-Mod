#nullable enable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Regression for the exact real-site failure exposed by v0.1.6: direct anchor detection can reject
/// some transitions while neighbouring transitions resolve cleanly. Those rejected transitions must
/// stay on the delayed-compositor path through a validated fallback; they must never fall through to
/// the old ShareX mosaic matcher where fixed controls bypass repair.
/// </summary>
internal static class ShareXModV017AnchorContinuitySelfTests
{
    private const int Width = 900;
    private const int Height = 640;
    private const int Delta = 360;
    private const int Frames = 12;
    private static readonly int[] ForcedDirectAnchorFailures = { 2, 5, 6, 9 };

    public static string RunOrThrow()
    {
        string temp = Path.Combine(Path.GetTempPath(), "LongCapture-v017-continuity-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(temp, "raw-frames-v016");
        Directory.CreateDirectory(raw);
        ShareXModAnchorContinuityV017.ResetLive();

        Bitmap? result = null;
        Bitmap? previous = null;
        using var compositor = new ShareXModDelayedCompositorV016.Session();
        int fallbacks = 0;

        try
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                using Bitmap current = BuildViewport(frame * Delta, frame);
                // Exported diagnostics use PNG, so the replay half deliberately uses PNG-only frames.
                current.Save(Path.Combine(raw, $"frame-{frame:D4}.png"), ImageFormat.Png);

                if (result is null)
                {
                    result = (Bitmap)current.Clone();
                    previous = (Bitmap)current.Clone();
                    continue;
                }

                bool forceFailure = Array.IndexOf(ForcedDirectAnchorFailures, frame) >= 0;
                ShareXModAnchorMatch direct = new(Delta, 0.2, 4);
                bool resolved = ShareXModAnchorContinuityV017.TryResolve(
                    previous!, current, !forceFailure, direct,
                    out int resolvedDelta, out string source, out double score);
                if (!resolved)
                    throw new InvalidOperationException($"v0.1.7 continuity failed forced direct-anchor rejection at frame {frame}.");
                if (Math.Abs(resolvedDelta - Delta) > 12)
                    throw new InvalidOperationException($"v0.1.7 resolved delta {resolvedDelta}, expected about {Delta}, frame={frame}, source={source}, score={score:F2}.");
                if (forceFailure)
                {
                    if (source == "direct-anchor") throw new InvalidOperationException("Forced anchor failure incorrectly used direct-anchor source.");
                    fallbacks++;
                }

                File.AppendAllText(Path.Combine(raw, "resolutions-v017.jsonl"),
                    $"{{\"frameIndex\":{frame},\"resolved\":true,\"scrollDelta\":{resolvedDelta},\"source\":\"{source}\",\"score\":{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}{Environment.NewLine}",
                    new UTF8Encoding(false));

                Bitmap next = compositor.TryAppend(result, previous!, current, resolvedDelta)
                    ?? throw new InvalidOperationException($"v0.1.7 delayed compositor rejected frame {frame}.");
                result.Dispose();
                result = next;
                previous!.Dispose();
                previous = (Bitmap)current.Clone();
            }

            int expectedHeight = Height + Delta * (Frames - 1);
            if (result!.Height < expectedHeight - 24 || result.Height > expectedHeight + 24)
                throw new InvalidOperationException($"v0.1.7 geometry drifted after forced failures: {result.Height}, expected {expectedHeight}.");
            int liveBands = CountFixedBands(result);
            if (liveBands > 1)
                throw new InvalidOperationException($"v0.1.7 live path repeated fixed control {liveBands} times after anchor fallback.");

            string replayPath = ShareXModOfflineReplay.Replay(temp, Path.Combine(temp, "replay-v017.png"));
            using Bitmap replay = new(replayPath);
            int replayBands = CountFixedBands(replay);
            if (replayBands > 1)
                throw new InvalidOperationException($"v0.1.7 PNG diagnostic replay repeated fixed control {replayBands} times.");
            if (Math.Abs(replay.Height - result.Height) > 2)
                throw new InvalidOperationException($"v0.1.7 replay geometry differs: live={result.Height}, replay={replay.Height}.");

            ShareXModV017AnchorTelemetry telemetry = ShareXModAnchorContinuityV017.SnapshotTelemetry();
            if (fallbacks != ForcedDirectAnchorFailures.Length || telemetry.FallbackAnchors + telemetry.PriorValidatedAnchors < fallbacks)
                throw new InvalidOperationException($"v0.1.7 fallback telemetry mismatch forced={fallbacks} telemetry={telemetry}.");
            if (telemetry.UnresolvedTransitions != 0)
                throw new InvalidOperationException($"v0.1.7 fixture left {telemetry.UnresolvedTransitions} unresolved transitions.");

            return $"v0.1.7 anchor-continuity passed: frames={Frames}, forcedDirectFailures={fallbacks}, liveFixedBands={liveBands}, replayFixedBands={replayBands}, height={result.Height}, unresolved={telemetry.UnresolvedTransitions}.";
        }
        finally
        {
            previous?.Dispose();
            result?.Dispose();
            ShareXModAnchorContinuityV017.ResetLive();
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static Bitmap BuildViewport(int logicalOffset, int frame)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (int y = 0; y < Height; y += 12)
        {
            int logical = logicalOffset + y;
            int row = logical / 12;
            using var bg = new SolidBrush(Color.FromArgb(248 - row * 5 % 25, 249 - row * 7 % 24, 250 - row * 11 % 22));
            g.FillRectangle(bg, 0, y, Width, 12);
            using var ink = new SolidBrush(Color.FromArgb(55 + row * 13 % 100, 65 + row * 17 % 100, 75 + row * 19 % 100));
            g.FillRectangle(ink, 40 + row * 37 % 610, y + 4, 90 + row % 170, 4);
        }

        // Repeating central cards make the conservative direct anchor plausibly ambiguous while the
        // vertical fallback still has enough distributed document motion to validate the true delta.
        for (int logicalTop = 160; logicalTop < logicalOffset + Height + 180; logicalTop += 240)
        {
            int y = logicalTop - logicalOffset;
            using var card = new SolidBrush(Color.FromArgb(240, 242, 244));
            g.FillRectangle(card, 170, y, 500, 100);
        }

        int fixedX = Width - 150;
        int fixedY = Height - 146;
        using var blue = new SolidBrush(Color.FromArgb(0, 145, 220));
        g.FillRectangle(blue, fixedX, fixedY, 126, 122);
        using var white = new SolidBrush(Color.White);
        g.FillRectangle(white, fixedX + 12, fixedY + 12, 84, 14);
        g.FillRectangle(white, fixedX + 14, fixedY + 78, 80, 24);
        using var changing = new SolidBrush(Color.FromArgb(245, 55 + frame * 23 % 175, 40 + frame * 17 % 180));
        g.FillRectangle(changing, fixedX + 18, fixedY + 43, 42 + frame * 7 % 55, 9);
        return bitmap;
    }

    private static int CountFixedBands(Bitmap bitmap)
    {
        int bands = 0;
        bool inBand = false;
        for (int y = 0; y < bitmap.Height; y += 3)
        {
            bool hit = false;
            for (int x = Width - 160; x < Width - 8; x += 4)
            {
                Color c = bitmap.GetPixel(x, y);
                if (c.B > 175 && c.G > 110 && c.R < 45) { hit = true; break; }
            }
            if (hit && !inBand) bands++;
            inBand = hit;
        }
        return bands;
    }
}
