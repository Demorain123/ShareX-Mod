using System.Buffers.Binary;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentCaptureSession
{
    private const int DefaultSettleMs = 550;
    private const double DefaultOverlapRatio = 0.18;
    private readonly BrowserAgentBridgeServer bridge;

    public BrowserAgentCaptureSession(BrowserAgentBridgeServer bridge)
    {
        this.bridge = bridge;
    }

    public async Task<BrowserAgentStitchResult> CaptureAsync(
        string outputRoot,
        int maxFrames,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        if (!bridge.IsConnected)
        {
            throw new InvalidOperationException("Browser Agent extension is not connected. Click the extension icon on the Chrome tab first.");
        }

        maxFrames = Math.Clamp(maxFrames, 2, 240);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string sessionDirectory = Path.Combine(outputRoot, "BrowserAgentCaptures", $"BrowserAgent-{stamp}");
        string framesDirectory = Path.Combine(sessionDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);

        var manifest = new BrowserAgentSessionManifest
        {
            StartedUtc = DateTime.UtcNow
        };
        string manifestPath = Path.Combine(sessionDirectory, "session.json");
        SaveManifest(manifestPath, manifest);

        status?.Invoke("Preparing active Chrome tab...");
        JsonElement begin = await bridge.SendRequestAsync(
            "begin",
            new { settleMs = DefaultSettleMs },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        double beginScrollY = ReadDouble(begin, "scrollY");
        if (Math.Abs(beginScrollY) > 2.0)
        {
            throw new InvalidOperationException($"Browser Agent could not reset the page to the top (scrollY={beginScrollY:F2}).");
        }

        bool cancelled = false;
        string stopReason = "unknown";

        try
        {
            for (int sequence = 1; sequence <= maxFrames; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status?.Invoke($"Capturing browser frame {sequence}/{maxFrames}...");

                JsonElement response = await bridge.SendRequestAsync(
                    "captureAndScroll",
                    new
                    {
                        settleMs = DefaultSettleMs,
                        overlapRatio = DefaultOverlapRatio,
                        hideFixed = sequence > 1
                    },
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);

                string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
                JsonElement before = response.GetProperty("before");
                JsonElement after = response.GetProperty("after");
                int hiddenCount = ReadInt(response, "hiddenCount");
                bool atBottom = ReadBool(response, "atBottom");

                byte[] png = DecodePngDataUrl(pngDataUrl);
                (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
                string relativePath = Path.Combine("frames", $"frame-{sequence:000}.png");
                string framePath = Path.Combine(sessionDirectory, relativePath);
                await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

                var record = new BrowserAgentFrameRecord
                {
                    Sequence = sequence,
                    FileName = relativePath,
                    ScrollYCss = ReadDouble(before, "scrollY"),
                    ScrollYAfterCss = ReadDouble(after, "scrollY"),
                    ScrollHeightCss = Math.Max(ReadDouble(before, "scrollHeight"), ReadDouble(after, "scrollHeight")),
                    ViewportWidthCss = ReadDouble(before, "viewportWidth"),
                    ViewportHeightCss = ReadDouble(before, "viewportHeight"),
                    DevicePixelRatio = ReadDouble(before, "devicePixelRatio"),
                    PixelWidth = pixelWidth,
                    PixelHeight = pixelHeight,
                    FixedCandidateCount = ReadInt(before, "fixedCandidateCount"),
                    HiddenCount = hiddenCount,
                    AtBottom = atBottom,
                    StateHash = ReadOptionalString(before, "stateHash"),
                    CapturedUtc = DateTime.UtcNow
                };

                ValidateProgress(manifest.Frames, record);
                manifest.Frames.Add(record);
                SaveManifest(manifestPath, manifest);
                status?.Invoke(
                    $"Frame {sequence}: y={record.ScrollYCss:F0}, {pixelWidth}x{pixelHeight}, fixed={record.FixedCandidateCount}, hidden={record.HiddenCount}");

                if (atBottom)
                {
                    stopReason = "document-bottom";
                    break;
                }

                if (Math.Abs(record.ScrollYAfterCss - record.ScrollYCss) < 0.5)
                {
                    stopReason = "document-did-not-scroll";
                    break;
                }

                if (sequence == maxFrames)
                {
                    stopReason = "max-frame-limit";
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            stopReason = "manual-stop";
        }

        if (manifest.Frames.Count == 0)
        {
            manifest.Status = cancelled ? "cancelled-no-frames" : "failed-no-frames";
            manifest.StopReason = stopReason;
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            throw new InvalidOperationException("Browser Agent capture ended before a usable frame was saved.");
        }

        status?.Invoke("Stitching saved frames using browser scroll geometry...");
        string outputPath = Path.Combine(sessionDirectory, $"LongCapture-BrowserAgent-v01-{stamp}.png");
        BrowserAgentStitchResult stitch = await Task.Run(
            () => BrowserAgentStreamingPngStitcher.Stitch(sessionDirectory, manifest.Frames, outputPath),
            CancellationToken.None).ConfigureAwait(false);

        manifest.Status = cancelled ? "partial-manual-stop" : "completed";
        manifest.StopReason = stopReason;
        manifest.FinalImage = Path.GetFileName(outputPath);
        manifest.CompletedUtc = DateTime.UtcNow;
        SaveManifest(manifestPath, manifest);

        LongCaptureLog.Info(
            $"Browser Agent PoC capture completed frames={stitch.FrameCount} output={stitch.Width}x{stitch.Height} stop={stopReason} path={LongCaptureLog.OneLine(outputPath)}");
        status?.Invoke($"Done: {stitch.FrameCount} frames -> {stitch.Width}x{stitch.Height}");
        return stitch;
    }

    private static void ValidateProgress(IReadOnlyList<BrowserAgentFrameRecord> existing, BrowserAgentFrameRecord current)
    {
        if (current.ViewportWidthCss <= 0 || current.ViewportHeightCss <= 0 || current.ScrollHeightCss <= 0)
        {
            throw new InvalidOperationException($"Browser Agent frame {current.Sequence} returned invalid DOM geometry.");
        }

        if (existing.Count == 0)
        {
            if (Math.Abs(current.ScrollYCss) > 2.0)
            {
                throw new InvalidOperationException($"Browser Agent first captured frame started at scrollY={current.ScrollYCss:F2}, not the document top.");
            }
            return;
        }

        BrowserAgentFrameRecord previous = existing[^1];
        if (current.ScrollYCss + 0.5 < previous.ScrollYCss)
        {
            throw new InvalidOperationException(
                $"Browser Agent scrollY moved backwards: {current.ScrollYCss:F2} after {previous.ScrollYCss:F2}.");
        }

        // An exact duplicate viewport cannot add pixels. It normally means the page
        // uses an unsupported inner scroller or a script prevented document scrolling.
        if (Math.Abs(current.ScrollYCss - previous.ScrollYCss) < 0.5)
        {
            throw new InvalidOperationException(
                $"Browser Agent received the same document position twice at frame {current.Sequence} (scrollY={current.ScrollYCss:F2}).");
        }
    }

    private static byte[] DecodePngDataUrl(string dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Browser Agent did not return a PNG data URL.");
        }

        byte[] bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
        if (bytes.Length < 24 || bytes.Length > BrowserAgentFrameCodec.MaxExtensionToDesktopBytes)
        {
            throw new InvalidDataException($"Browser Agent PNG payload size {bytes.Length} is invalid.");
        }
        return bytes;
    }

    private static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature) ||
            png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            throw new InvalidDataException("Browser Agent returned bytes that are not a valid PNG header.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
        {
            throw new InvalidDataException($"Browser Agent PNG dimensions {width}x{height} are outside PoC bounds.");
        }
        return (width, height);
    }

    private static void SaveManifest(string path, BrowserAgentSessionManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || !value.TryGetDouble(out double result))
        {
            throw new InvalidDataException($"Browser Agent response is missing numeric '{name}'.");
        }
        return result;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        string? value = ReadOptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Browser Agent response is missing '{name}'.");
        }
        return value;
    }

    private static string ReadOptionalString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
