using System.Buffers.Binary;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentCaptureSession
{
    private const int StableWindowMs = 900;
    private const int MaxStabilityWaitMs = 7000;
    private const int StabilitySampleMs = 120;
    private const double DefaultOverlapRatio = 0.26;
    private const int RecoveryWindowFrames = 3;
    private const int MaximumSafetyFrameLimit = 1200;
    private const int PreviewDurationMs = 650;

    private readonly BrowserAgentBridgeServer bridge;

    public string? LastSessionDirectory { get; private set; }

    public BrowserAgentCaptureSession(BrowserAgentBridgeServer bridge)
    {
        this.bridge = bridge;
    }

    public async Task<BrowserAgentStitchResult> CaptureAsync(
        string outputRoot,
        int safetyFrameLimit,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        if (!bridge.IsConnected)
        {
            throw new InvalidOperationException("Browser Agent extension is not connected. Attach the active Chromium tab first.");
        }

        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string sessionDirectory = Path.Combine(outputRoot, "BrowserAgentCaptures", $"BrowserAgent-{stamp}");
        string framesDirectory = Path.Combine(sessionDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);
        LastSessionDirectory = sessionDirectory;

        var manifest = new BrowserAgentSessionManifest
        {
            StartedUtc = DateTime.UtcNow,
            SafetyFrameLimit = safetyFrameLimit
        };
        string manifestPath = Path.Combine(sessionDirectory, "session.json");
        SaveManifest(manifestPath, manifest);

        status?.Invoke("Showing the Browser Assisted capture region...");
        await bridge.SendRequestAsync(
            "preview",
            new { durationMs = PreviewDurationMs },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        status?.Invoke("Preparing the active Chromium tab and waiting for DOM/layout stability...");
        JsonElement begin = await bridge.SendRequestAsync(
            "begin",
            StabilityPayload(),
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        double beginScrollY = ReadDouble(begin, "scrollY");
        if (Math.Abs(beginScrollY) > 2.0)
        {
            throw new InvalidOperationException($"Browser Agent could not reset the page to the top (scrollY={beginScrollY:F2}).");
        }
        if (begin.TryGetProperty("stability", out JsonElement beginStability) && !ReadBool(beginStability, "stable"))
        {
            throw new InvalidOperationException("The page did not become stable at the document top within the Browser Agent stability timeout.");
        }

        bool cancelled = false;
        string stopReason = "unknown";

        try
        {
            for (int sequence = 1; sequence <= safetyFrameLimit; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status?.Invoke($"Capturing browser frame {sequence} — Auto until page end (F8 stops early)...");

                JsonElement response = await bridge.SendRequestAsync(
                    "captureAndScroll",
                    new
                    {
                        stableWindowMs = StableWindowMs,
                        maxWaitMs = MaxStabilityWaitMs,
                        sampleMs = StabilitySampleMs,
                        overlapRatio = DefaultOverlapRatio,
                        hideFixed = sequence > 1
                    },
                    TimeSpan.FromSeconds(45),
                    cancellationToken).ConfigureAwait(false);

                BrowserAgentFrameRecord record = await SaveNewFrameAsync(
                    sessionDirectory,
                    sequence,
                    response,
                    cancellationToken).ConfigureAwait(false);

                ValidateProgress(manifest.Frames, record);

                if (record.StabilityTimedOut)
                {
                    throw new InvalidOperationException(
                        $"Browser Agent frame {sequence} did not reach DOM/layout stability within {MaxStabilityWaitMs} ms.");
                }

                manifest.Frames.Add(record);

                if (manifest.Frames.Count == 1)
                {
                    record.OverlapVerified = true;
                    record.OverlapStatus = "first-frame";
                }
                else if (record.Sequence == 2)
                {
                    record.OverlapVerified = true;
                    record.OverlapStatus = "first-fixed-transition-skipped";
                }
                else
                {
                    BrowserAgentOverlapCheck check = VerifyAndStoreOverlap(
                        sessionDirectory,
                        manifest.Frames[^2],
                        record);

                    if (!check.Acceptable || record.CaptureStateChanged)
                    {
                        status?.Invoke(
                            $"Dynamic-page change near frame {sequence}; re-capturing the last {Math.Min(RecoveryWindowFrames, manifest.Frames.Count)} viewports...");

                        bool recovered = await RecoverRecentWindowAsync(
                            sessionDirectory,
                            manifest,
                            status,
                            cancellationToken).ConfigureAwait(false);

                        if (!recovered)
                        {
                            SaveManifest(manifestPath, manifest);
                            throw new InvalidOperationException(
                                $"Browser Agent detected unstable overlap near frame {sequence} ({check.Detail}). " +
                                "The recent viewport window was re-captured but still did not agree, so capture stopped instead of silently producing a broken long image.");
                        }
                    }
                }

                SaveManifest(manifestPath, manifest);
                BrowserAgentFrameRecord accepted = manifest.Frames[^1];
                string pageHint = accepted.PageCounterTotal > 0
                    ? $", page={accepted.PageCounterCurrent}/{accepted.PageCounterTotal}"
                    : string.Empty;
                string remainingHint = accepted.EstimatedFramesToLoadedEnd > 0
                    ? $", loaded-end≈{accepted.EstimatedFramesToLoadedEnd}f"
                    : string.Empty;
                status?.Invoke(
                    $"Frame {sequence}: y={accepted.ScrollYCss:F0}, stable={accepted.StabilityWaitMs}ms, " +
                    $"grow={accepted.LazyWarmupGrowthCss + accepted.EndConfirmationGrowthCss:F0}px, " +
                    $"overlap={accepted.OverlapMeanAbsoluteError:F2}{pageHint}{remainingHint}");

                if (accepted.AtBottom && accepted.EndConfirmed)
                {
                    stopReason = "document-bottom-confirmed";
                    break;
                }

                if (Math.Abs(accepted.ScrollYAfterCss - accepted.ScrollYCss) < 0.5)
                {
                    stopReason = "document-did-not-scroll";
                    break;
                }

                if (sequence == safetyFrameLimit)
                {
                    stopReason = "safety-frame-limit";
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            stopReason = "manual-stop";
        }
        catch (Exception ex)
        {
            manifest.Status = "failed";
            manifest.StopReason = "capture-error";
            manifest.Error = ex.Message;
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            LongCaptureLog.Error("Browser Agent v0.1.2 capture aborted", ex);
            throw;
        }

        if (manifest.Frames.Count == 0)
        {
            manifest.Status = cancelled ? "cancelled-no-frames" : "failed-no-frames";
            manifest.StopReason = stopReason;
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            throw new InvalidOperationException("Browser Agent capture ended before a usable frame was saved.");
        }

        status?.Invoke("Stitching verified frames using browser scroll geometry...");
        string outputPath = Path.Combine(sessionDirectory, $"LongCapture-BrowserAgent-v012-{stamp}.png");
        BrowserAgentStitchResult rawStitch = await Task.Run(
            () => BrowserAgentStreamingPngStitcher.Stitch(sessionDirectory, manifest.Frames, outputPath),
            CancellationToken.None).ConfigureAwait(false);

        bool complete = string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal);
        manifest.Status = complete
            ? "completed"
            : cancelled
                ? "partial-manual-stop"
                : "partial-" + stopReason;
        manifest.StopReason = stopReason;
        manifest.FinalImage = Path.GetFileName(outputPath);
        manifest.CompletedUtc = DateTime.UtcNow;
        SaveManifest(manifestPath, manifest);

        BrowserAgentFrameRecord last = manifest.Frames[^1];
        var stitch = new BrowserAgentStitchResult
        {
            OutputPath = rawStitch.OutputPath,
            Width = rawStitch.Width,
            Height = rawStitch.Height,
            ScaleX = rawStitch.ScaleX,
            ScaleY = rawStitch.ScaleY,
            FrameCount = rawStitch.FrameCount,
            IsComplete = complete,
            StopReason = stopReason,
            PageCounterCurrent = last.PageCounterCurrent,
            PageCounterTotal = last.PageCounterTotal
        };

        int recoveries = manifest.Frames.Count(frame => frame.RecoveryGeneration > 0);
        LongCaptureLog.Info(
            $"Browser Agent v0.1.2 capture ended complete={complete} frames={stitch.FrameCount} recoveries={recoveries} " +
            $"output={stitch.Width}x{stitch.Height} stop={stopReason} page={last.PageCounterCurrent}/{last.PageCounterTotal} " +
            $"path={LongCaptureLog.OneLine(outputPath)}");
        status?.Invoke(
            complete
                ? $"Complete: true page end confirmed after {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}"
                : $"Partial: {stopReason}, {stitch.FrameCount} verified frames -> {stitch.Width}x{stitch.Height}");
        return stitch;
    }

    private static object StabilityPayload() => new
    {
        stableWindowMs = StableWindowMs,
        maxWaitMs = MaxStabilityWaitMs,
        sampleMs = StabilitySampleMs
    };

    private async Task<BrowserAgentFrameRecord> SaveNewFrameAsync(
        string sessionDirectory,
        int sequence,
        JsonElement response,
        CancellationToken cancellationToken)
    {
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
        string relativePath = Path.Combine("frames", $"frame-{sequence:0000}.png");
        string framePath = Path.Combine(sessionDirectory, relativePath);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        JsonElement before = response.GetProperty("before");
        JsonElement after = response.GetProperty("after");
        double viewportHeight = ReadDouble(before, "viewportHeight");
        double scrollHeight = Math.Max(ReadDouble(before, "scrollHeight"), ReadDouble(after, "scrollHeight"));
        double scrollY = ReadDouble(before, "scrollY");
        double delta = Math.Max(1, Math.Floor(viewportHeight * (1 - DefaultOverlapRatio)));
        int estimatedRemaining = (int)Math.Ceiling(Math.Max(0, scrollHeight - (scrollY + viewportHeight)) / delta);

        var record = new BrowserAgentFrameRecord
        {
            Sequence = sequence,
            FileName = relativePath,
            ScrollYCss = scrollY,
            ScrollYAfterCss = ReadDouble(after, "scrollY"),
            ScrollHeightCss = scrollHeight,
            ViewportWidthCss = ReadDouble(before, "viewportWidth"),
            ViewportHeightCss = viewportHeight,
            DevicePixelRatio = ReadDouble(before, "devicePixelRatio"),
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            FixedCandidateCount = ReadInt(before, "fixedCandidateCount"),
            HiddenCount = ReadInt(response, "hiddenCount"),
            AtBottom = ReadBool(response, "atBottom"),
            StateHash = ReadOptionalString(before, "stateHash"),
            LazyWarmupTriggered = ReadBool(response, "warmupTriggered"),
            LazyWarmupGrowthCss = ReadDoubleOrDefault(response, "warmupGrowthCss"),
            CaptureStateChanged = ReadBool(response, "captureStateChanged"),
            CapturedUtc = DateTime.UtcNow,
            PageCounterCurrent = ReadInt(before, "pageCounterCurrent"),
            PageCounterTotal = ReadInt(before, "pageCounterTotal"),
            PageCounterText = ReadOptionalString(before, "pageCounterText"),
            EstimatedFramesToLoadedEnd = estimatedRemaining
        };
        ApplyStability(record, response);
        ApplyEndConfirmation(record, response);
        return record;
    }

    private async Task<bool> RecoverRecentWindowAsync(
        string sessionDirectory,
        BrowserAgentSessionManifest manifest,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        if (manifest.Frames.Count < 2) return true;

        int lastIndex = manifest.Frames.Count - 1;
        int firstIndex = Math.Max(0, manifest.Frames.Count - RecoveryWindowFrames);
        double resumeY = manifest.Frames[lastIndex].ScrollYAfterCss;

        for (int i = firstIndex; i <= lastIndex; i++)
        {
            BrowserAgentFrameRecord frame = manifest.Frames[i];
            JsonElement response = await bridge.SendRequestAsync(
                "captureAt",
                new
                {
                    scrollY = frame.ScrollYCss,
                    stableWindowMs = Math.Max(StableWindowMs, 1100),
                    maxWaitMs = Math.Max(MaxStabilityWaitMs, 8500),
                    sampleMs = StabilitySampleMs,
                    hideFixed = frame.Sequence > 1
                },
                TimeSpan.FromSeconds(35),
                cancellationToken).ConfigureAwait(false);

            await ReplaceFrameFromRecaptureAsync(
                sessionDirectory,
                frame,
                response,
                cancellationToken).ConfigureAwait(false);
            frame.RecoveryGeneration++;
        }

        bool accepted = true;
        int verifyFrom = Math.Max(1, firstIndex);
        for (int i = verifyFrom; i <= lastIndex; i++)
        {
            BrowserAgentFrameRecord current = manifest.Frames[i];
            if (current.Sequence == 2)
            {
                current.OverlapVerified = true;
                current.OverlapStatus = "first-fixed-transition-skipped";
                continue;
            }

            BrowserAgentOverlapCheck check = VerifyAndStoreOverlap(
                sessionDirectory,
                manifest.Frames[i - 1],
                current);
            if (!check.Acceptable)
            {
                accepted = false;
                status?.Invoke($"Recovery overlap still unstable at frame {current.Sequence}: {check.Detail}");
                break;
            }
        }

        JsonElement move = await bridge.SendRequestAsync(
            "moveTo",
            new
            {
                scrollY = resumeY,
                stableWindowMs = StableWindowMs,
                maxWaitMs = MaxStabilityWaitMs,
                sampleMs = StabilitySampleMs
            },
            TimeSpan.FromSeconds(25),
            cancellationToken).ConfigureAwait(false);

        if (move.TryGetProperty("stability", out JsonElement resumeStability) && !ReadBool(resumeStability, "stable"))
        {
            accepted = false;
        }

        return accepted;
    }

    private static async Task ReplaceFrameFromRecaptureAsync(
        string sessionDirectory,
        BrowserAgentFrameRecord frame,
        JsonElement response,
        CancellationToken cancellationToken)
    {
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
        string framePath = Path.Combine(sessionDirectory, frame.FileName);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        JsonElement before = response.GetProperty("before");
        frame.ScrollYCss = ReadDouble(before, "scrollY");
        frame.ScrollHeightCss = Math.Max(frame.ScrollHeightCss, ReadDouble(before, "scrollHeight"));
        frame.ViewportWidthCss = ReadDouble(before, "viewportWidth");
        frame.ViewportHeightCss = ReadDouble(before, "viewportHeight");
        frame.DevicePixelRatio = ReadDouble(before, "devicePixelRatio");
        frame.PixelWidth = pixelWidth;
        frame.PixelHeight = pixelHeight;
        frame.FixedCandidateCount = ReadInt(before, "fixedCandidateCount");
        frame.HiddenCount = ReadInt(response, "hiddenCount");
        frame.AtBottom = ReadBool(response, "atBottom");
        frame.StateHash = ReadOptionalString(before, "stateHash");
        frame.CaptureStateChanged = ReadBool(response, "captureStateChanged");
        frame.PageCounterCurrent = ReadInt(before, "pageCounterCurrent");
        frame.PageCounterTotal = ReadInt(before, "pageCounterTotal");
        frame.PageCounterText = ReadOptionalString(before, "pageCounterText");
        frame.CapturedUtc = DateTime.UtcNow;
        frame.EndConfirmed = false;
        frame.EndConfirmationRounds = 0;
        frame.EndConfirmationGrowthCss = 0;
        frame.EndCounterIncomplete = false;
        frame.EndConfidence = string.Empty;
        ApplyStability(frame, response);
    }

    private static BrowserAgentOverlapCheck VerifyAndStoreOverlap(
        string sessionDirectory,
        BrowserAgentFrameRecord previous,
        BrowserAgentFrameRecord current)
    {
        BrowserAgentOverlapCheck check = BrowserAgentOverlapVerifier.Measure(sessionDirectory, previous, current);
        current.OverlapVerified = check.Comparable && check.Acceptable;
        current.OverlapMeanAbsoluteError = check.MeanAbsoluteError;
        current.OverlapStrongDiffRatio = check.StrongDiffRatio;
        current.OverlapPixels = check.OverlapPixels;
        current.OverlapStatus = check.Detail;
        return check;
    }

    private static void ApplyStability(BrowserAgentFrameRecord record, JsonElement response)
    {
        if (!response.TryGetProperty("stabilityBefore", out JsonElement stability))
        {
            record.StabilityTimedOut = true;
            return;
        }

        record.StabilityWaitMs = ReadInt(stability, "waitedMs");
        record.StabilityTimedOut = !ReadBool(stability, "stable") || ReadBool(stability, "timedOut");
        record.StabilityMutationCount = ReadInt(stability, "mutationCount");
        record.StabilityResizeCount = ReadInt(stability, "resizeCount");
        record.StabilityHeightChangeCount = ReadInt(stability, "heightChangeCount");
        record.StabilityPendingImages = ReadInt(stability, "pendingImages");
        record.StabilityHeightGrowthCss = Math.Max(
            0,
            ReadDoubleOrDefault(stability, "finalScrollHeight") -
            ReadDoubleOrDefault(stability, "initialScrollHeight"));
    }

    private static void ApplyEndConfirmation(BrowserAgentFrameRecord record, JsonElement response)
    {
        if (!response.TryGetProperty("endConfirmation", out JsonElement end)) return;
        record.EndConfirmed = ReadBool(end, "confirmed");
        record.EndConfirmationRounds = ReadInt(end, "rounds");
        record.EndConfirmationGrowthCss = ReadDoubleOrDefault(end, "growthCss");
        record.EndCounterIncomplete = ReadBool(end, "counterIncomplete");
        record.EndConfidence = ReadOptionalString(end, "confidence");
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
            throw new InvalidDataException($"Browser Agent PNG dimensions {width}x{height} are outside Browser Assisted bounds.");
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

    private static double ReadDoubleOrDefault(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : 0;
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
