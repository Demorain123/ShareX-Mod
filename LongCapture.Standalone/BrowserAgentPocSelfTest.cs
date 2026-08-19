using System.Text;

namespace LongCapture.Standalone;

internal static class BrowserAgentPocSelfTest
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "LongCapture-BrowserAgent-v018-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frames"));

            if (!RunFrameCodecRoundTrip())
            {
                LongCaptureLog.Warn("Browser Agent self-test failed native-message frame codec round-trip");
                return 31;
            }

            var frames = new List<BrowserAgentFrameRecord>();
            int[] starts = [0, 30, 60];
            for (int i = 0; i < starts.Length; i++)
            {
                int start = starts[i];
                string relative = Path.Combine("frames", $"frame-{i + 1:000}.png");
                string path = Path.Combine(root, relative);
                WriteDeterministicFrame(path, 96, 80, start);
                frames.Add(new BrowserAgentFrameRecord
                {
                    Sequence = i + 1,
                    FileName = relative,
                    ScrollYCss = start,
                    ScrollYAfterCss = i == starts.Length - 1 ? start : starts[i + 1],
                    ScrollHeightCss = 140,
                    ViewportWidthCss = 96,
                    ViewportHeightCss = 80,
                    DevicePixelRatio = 1,
                    PixelWidth = 96,
                    PixelHeight = 80,
                    AtBottom = i == starts.Length - 1,
                    CapturedUtc = DateTime.UtcNow
                });
            }

            BrowserAgentOverlapCheck cleanOverlap = BrowserAgentOverlapVerifier.Measure(root, frames[0], frames[1]);
            if (!cleanOverlap.Comparable || !cleanOverlap.Acceptable || cleanOverlap.EdgeContamination)
            {
                LongCaptureLog.Warn($"Browser Agent self-test rejected deterministic clean overlap: {cleanOverlap.Detail}");
                return 34;
            }

            string badRelative = Path.Combine("frames", "frame-bad.png");
            string badPath = Path.Combine(root, badRelative);
            WriteMutatedFrame(badPath, 96, 80, 30, 24);

            var badFrame = new BrowserAgentFrameRecord
            {
                Sequence = 2,
                FileName = badRelative,
                ScrollYCss = 30,
                ScrollYAfterCss = 60,
                ScrollHeightCss = 140,
                ViewportWidthCss = 96,
                ViewportHeightCss = 80,
                DevicePixelRatio = 1,
                PixelWidth = 96,
                PixelHeight = 80
            };
            BrowserAgentOverlapCheck badOverlap = BrowserAgentOverlapVerifier.Measure(root, frames[0], badFrame);
            if (!badOverlap.Comparable || badOverlap.Acceptable)
            {
                LongCaptureLog.Warn($"Browser Agent self-test failed to reject full-width mutated overlap: {badOverlap.Detail}");
                return 35;
            }

            // v0.1.8 regression: mutate ONLY the left rail. The old 7%-93% + median
            // verifier could report a perfect overlap while a sticky avatar visibly jumped.
            string railRelative = Path.Combine("frames", "frame-left-rail-bad.png");
            string railPath = Path.Combine(root, railRelative);
            WriteLeftRailMutatedFrame(railPath, 96, 80, 30, 24, 50);
            var railFrame = new BrowserAgentFrameRecord
            {
                Sequence = 2,
                FileName = railRelative,
                ScrollYCss = 30,
                ScrollYAfterCss = 60,
                ScrollHeightCss = 140,
                ViewportWidthCss = 96,
                ViewportHeightCss = 80,
                DevicePixelRatio = 1,
                PixelWidth = 96,
                PixelHeight = 80
            };
            BrowserAgentOverlapCheck railOverlap = BrowserAgentOverlapVerifier.Measure(root, frames[0], railFrame);
            if (!railOverlap.Comparable || railOverlap.Acceptable || !railOverlap.LeftEdgeContamination)
            {
                LongCaptureLog.Warn($"Browser Agent v0.1.8 self-test failed to detect left-rail contamination: {railOverlap.Detail}");
                return 38;
            }

            string output = Path.Combine(root, "stitched.png");
            BrowserAgentStitchResult result = BrowserAgentStreamingPngStitcher.Stitch(root, frames, output);
            if (result.Width != 96 || result.Height != 140 || result.FrameCount != 3 || !File.Exists(output))
            {
                LongCaptureLog.Warn($"Browser Agent self-test wrong stitch dimensions {result.Width}x{result.Height} frames={result.FrameCount}");
                return 32;
            }

            using var bitmap = new Bitmap(output);
            foreach (int y in new[] { 0, 29, 30, 59, 60, 79, 109, 139 })
            {
                Color expected = ColorForAbsoluteRow(y);
                Color actual = bitmap.GetPixel(37, y);
                if (actual.ToArgb() != expected.ToArgb())
                {
                    LongCaptureLog.Warn(
                        $"Browser Agent self-test pixel mismatch y={y} actual={actual.ToArgb():X8} expected={expected.ToArgb():X8}");
                    return 33;
                }
            }

            LongCaptureLog.Info("Browser Agent v0.1.8 self-test passed codec + rail-aware overlap gate + bounded-memory stitch");
            return 0;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("Browser Agent v0.1.8 self-test threw", ex);
            return 39;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test evidence is disposable; do not hide the actual result because cleanup failed.
            }
        }
    }

    private static bool RunFrameCodecRoundTrip()
    {
        byte[] expected = Encoding.UTF8.GetBytes("{\"id\":1,\"type\":\"ping\"}");
        using var stream = new MemoryStream();
        BrowserAgentFrameCodec.WriteAsync(
            stream,
            expected,
            BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
            CancellationToken.None).GetAwaiter().GetResult();
        stream.Position = 0;
        byte[]? actual = BrowserAgentFrameCodec.ReadAsync(
            stream,
            BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
            CancellationToken.None).GetAwaiter().GetResult();
        return actual is not null && actual.SequenceEqual(expected);
    }

    private static void WriteDeterministicFrame(string path, int width, int height, int absoluteStartY)
    {
        using var bitmap = new Bitmap(width, height);
        PaintDeterministic(bitmap, absoluteStartY);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void WriteMutatedFrame(string path, int width, int height, int absoluteStartY, int mutatedRows)
    {
        using var bitmap = new Bitmap(width, height);
        PaintDeterministic(bitmap, absoluteStartY);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.FillRectangle(Brushes.Black, 0, 0, width, Math.Min(height, mutatedRows));
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void WriteLeftRailMutatedFrame(
        string path,
        int width,
        int height,
        int absoluteStartY,
        int railWidth,
        int mutatedRows)
    {
        using var bitmap = new Bitmap(width, height);
        PaintDeterministic(bitmap, absoluteStartY);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.FillRectangle(
                Brushes.Magenta,
                0,
                0,
                Math.Min(width, railWidth),
                Math.Min(height, mutatedRows));
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void PaintDeterministic(Bitmap bitmap, int absoluteStartY)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            Color color = ColorForAbsoluteRow(absoluteStartY + y);
            for (int x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, color);
            }
        }
    }

    private static Color ColorForAbsoluteRow(int y)
    {
        return Color.FromArgb(
            255,
            (y * 3 + 17) % 251,
            (y * 5 + 29) % 251,
            (y * 7 + 43) % 251);
    }
}
