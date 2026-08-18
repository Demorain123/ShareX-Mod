using System.Text;

namespace LongCapture.Standalone;

internal static class BrowserAgentPocSelfTest
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "LongCapture-BrowserAgent-v01-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frames"));

            if (!RunFrameCodecRoundTrip())
            {
                LongCaptureLog.Warn("Browser Agent PoC self-test failed native-message frame codec round-trip");
                return 31;
            }

            var frames = new List<BrowserAgentFrameRecord>();
            int[] starts = [0, 30, 60];
            for (int i = 0; i < starts.Length; i++)
            {
                int start = starts[i];
                string relative = Path.Combine("frames", $"frame-{i + 1:000}.png");
                string path = Path.Combine(root, relative);
                WriteDeterministicFrame(path, 64, 40, start);
                frames.Add(new BrowserAgentFrameRecord
                {
                    Sequence = i + 1,
                    FileName = relative,
                    ScrollYCss = start,
                    ScrollYAfterCss = i == starts.Length - 1 ? start : starts[i + 1],
                    ScrollHeightCss = 100,
                    ViewportWidthCss = 64,
                    ViewportHeightCss = 40,
                    DevicePixelRatio = 1,
                    PixelWidth = 64,
                    PixelHeight = 40,
                    AtBottom = i == starts.Length - 1,
                    CapturedUtc = DateTime.UtcNow
                });
            }

            string output = Path.Combine(root, "stitched.png");
            BrowserAgentStitchResult result = BrowserAgentStreamingPngStitcher.Stitch(root, frames, output);
            if (result.Width != 64 || result.Height != 100 || result.FrameCount != 3 || !File.Exists(output))
            {
                LongCaptureLog.Warn($"Browser Agent PoC self-test wrong stitch dimensions {result.Width}x{result.Height} frames={result.FrameCount}");
                return 32;
            }

            using var bitmap = new Bitmap(output);
            foreach (int y in new[] { 0, 29, 30, 39, 59, 60, 79, 99 })
            {
                Color expected = ColorForAbsoluteRow(y);
                Color actual = bitmap.GetPixel(17, y);
                if (actual.ToArgb() != expected.ToArgb())
                {
                    LongCaptureLog.Warn(
                        $"Browser Agent PoC self-test pixel mismatch y={y} actual={actual.ToArgb():X8} expected={expected.ToArgb():X8}");
                    return 33;
                }
            }

            LongCaptureLog.Info("Browser Agent PoC self-test passed codec + bounded-memory DOM-geometry stitch");
            return 0;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("Browser Agent PoC self-test threw", ex);
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
        for (int y = 0; y < height; y++)
        {
            Color color = ColorForAbsoluteRow(absoluteStartY + y);
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color);
            }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
