#nullable enable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModV016ReplaySelfTests
{
    private const int Width = 840;
    private const int Height = 600;
    private const int Delta = 180;
    private const int Frames = 10;
    private const int FixedX = Width - 152;
    private const int FixedY = Height - 150;
    private const int FixedWidth = 128;
    private const int FixedHeight = 128;

    public static string RunOrThrow()
    {
        string temp = Path.Combine(Path.GetTempPath(), "LongCapture-v016-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string raw = Path.Combine(temp, "raw-frames-v016");
        Directory.CreateDirectory(raw);

        Bitmap? result = null;
        Bitmap? previous = null;
        using var session = new ShareXModDelayedCompositorV016.Session();

        try
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                using Bitmap viewport = BuildViewport(frame * Delta, frame);
                viewport.Save(Path.Combine(raw, $"frame-{frame:D4}.bmp"), ImageFormat.Bmp);
                if (frame > 0)
                {
                    File.AppendAllText(
                        Path.Combine(raw, "anchors.jsonl"),
                        $"{{\"frameIndex\":{frame},\"hasAnchor\":true,\"scrollDelta\":{Delta},\"score\":0.99,\"agreementCount\":4}}{Environment.NewLine}",
                        new UTF8Encoding(false));
                }

                if (result is null)
                {
                    result = (Bitmap)viewport.Clone();
                    previous = (Bitmap)viewport.Clone();
                    continue;
                }

                Bitmap next = session.TryAppend(result, previous!, viewport, Delta)
                    ?? throw new InvalidOperationException($"v0.1.6 delayed compositor rejected frame {frame}.");
                result.Dispose();
                result = next;
                previous!.Dispose();
                previous = (Bitmap)viewport.Clone();
            }

            Bitmap finalResult = result ?? throw new InvalidOperationException("v0.1.6 delayed compositor produced no result.");
            int expectedHeight = Height + Delta * (Frames - 1);
            if (finalResult.Size != new Size(Width, expectedHeight))
                throw new InvalidOperationException($"v0.1.6 geometry mismatch: {finalResult.Size}, expected {Width}x{expectedHeight}.");

            int liveBands = CountBlueFixedBands(finalResult);
            if (liveBands > 1)
                throw new InvalidOperationException($"v0.1.6 live delayed compositor left {liveBands} repeated fixed-control bands.");

            ShareXModV016Telemetry telemetry = session.SnapshotTelemetry();
            if (telemetry.RepairedComponents <= 0 || telemetry.DetectedComponents <= 0)
                throw new InvalidOperationException("v0.1.6 fixture did not exercise component detection/repair.");

            // The deliberately static-looking left body card must still move with the document and
            // must not be removed simply because two screenshots contain similar neutral pixels.
            for (int logicalTop = 300; logicalTop < 1000; logicalTop += 180)
            {
                int y = logicalTop;
                if (y + 24 >= finalResult.Height) break;
                Color sample = finalResult.GetPixel(270, y + 8);
                if (sample.R > 248 && sample.G > 248 && sample.B > 248)
                    throw new InvalidOperationException($"v0.1.6 false-positive guard removed moving body content near y={y}.");
            }

            string replayPath = ShareXModOfflineReplay.Replay(temp, Path.Combine(temp, "replay.png"));
            using Bitmap replay = new(replayPath);
            int replayBands = CountBlueFixedBands(replay);
            if (replayBands > 1)
                throw new InvalidOperationException($"v0.1.6 offline replay left {replayBands} repeated fixed-control bands.");
            if (replay.Size != finalResult.Size)
                throw new InvalidOperationException($"Offline replay geometry differs from live compositor: {replay.Size} vs {finalResult.Size}.");

            return $"v0.1.6 raw-frame delayed compositor/replay passed: frames={Frames}, result={finalResult.Width}x{finalResult.Height}, liveFixedBands={liveBands}, replayFixedBands={replayBands}, detectedComponents={telemetry.DetectedComponents}, repairedComponents={telemetry.RepairedComponents}, pendingTail={telemetry.PendingTailComponents}.";
        }
        finally
        {
            previous?.Dispose();
            result?.Dispose();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static Bitmap BuildViewport(int logicalOffset, int frame)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        DrawDocument(graphics, logicalOffset, Height);

        // Moving body card: visually quiet and repetitive, but document-relative. The detector must
        // prefer the y-delta motion hypothesis rather than classify this as fixed UI.
        int cardLogicalTop = 330;
        int cardY = cardLogicalTop - logicalOffset;
        if (cardY < Height && cardY + 120 > 0)
        {
            using var card = new SolidBrush(Color.FromArgb(241, 243, 245));
            graphics.FillRectangle(card, 220, cardY, 300, 116);
            using var cardInk = new SolidBrush(Color.FromArgb(115, 122, 130));
            for (int i = 0; i < 5; i++) graphics.FillRectangle(cardInk, 248, cardY + 18 + i * 17, 210 - i * 12, 5);
        }

        // Fixed right-bottom Back/counter cluster. Background is stable while internal counter/text
        // changes each frame, matching the Linux.do failure shape that defeated v0.1.4/v0.1.5.
        using var blue = new SolidBrush(Color.FromArgb(0, 145, 220));
        graphics.FillRectangle(blue, FixedX, FixedY, FixedWidth, FixedHeight);
        using var white = new SolidBrush(Color.White);
        graphics.FillRectangle(white, FixedX + 14, FixedY + 14, 80, 14);
        graphics.FillRectangle(white, FixedX + 14, FixedY + 76, 88, 28);
        using var changing = new SolidBrush(Color.FromArgb(250, 60 + frame * 13 % 170, 35 + frame * 21 % 190));
        graphics.FillRectangle(changing, FixedX + 17 + frame % 4, FixedY + 43, 54 + frame * 3 % 40, 9);
        graphics.FillRectangle(changing, FixedX + 58, FixedY + 86, 12 + frame % 5 * 6, 8);

        // Small independently changing fixed avatar/indicator above the Back control. This forces
        // component tracking to deal with more than one fixed object on the same edge.
        using var avatar = new SolidBrush(Color.FromArgb(245, 195 - frame * 7 % 90, 45 + frame * 11 % 150));
        graphics.FillEllipse(avatar, Width - 56, Height - 230, 34, 34);
        return bitmap;
    }

    private static void DrawDocument(Graphics graphics, int logicalTop, int height)
    {
        graphics.Clear(Color.White);
        for (int y = 0; y < height; y += 14)
        {
            int logicalY = logicalTop + y;
            int row = logicalY / 14;
            using var background = new SolidBrush(Color.FromArgb(
                255,
                232 - row * 7 % 42,
                238 - row * 11 % 48,
                244 - row * 13 % 50));
            graphics.FillRectangle(background, 0, y, Width, 14);
            using var ink = new SolidBrush(Color.FromArgb(45 + row * 17 % 105, 60 + row * 23 % 100, 75 + row * 31 % 105));
            graphics.FillRectangle(ink, 28 + row * 47 % 590, y + 5, 70 + row % 145, 4);
        }
    }

    private static int CountBlueFixedBands(Bitmap bitmap)
    {
        int bands = 0;
        bool inside = false;
        for (int y = 0; y < bitmap.Height; y += 3)
        {
            bool hit = false;
            for (int x = FixedX - 5; x < Math.Min(bitmap.Width, FixedX + FixedWidth + 5); x += 4)
            {
                Color c = bitmap.GetPixel(Math.Max(0, x), y);
                if (c.B > 175 && c.G > 105 && c.R < 45)
                {
                    hit = true;
                    break;
                }
            }
            if (hit && !inside) bands++;
            inside = hit;
        }
        return bands;
    }
}
