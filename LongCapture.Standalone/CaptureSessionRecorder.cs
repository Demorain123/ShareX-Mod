using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class CaptureSessionRecorder : IDisposable
{
    private const int MaxDiagnosticFramePairs = 16;
    private static readonly object LatestSync = new();
    private static string? latestCompletedSessionDirectory;

    private readonly object sync = new();
    private readonly string telemetryPath;
    private readonly string sessionLogPath;
    private readonly string diagnosticsDirectory;
    private readonly DateTimeOffset started = DateTimeOffset.Now;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly string mode;
    private readonly string target;
    private readonly object optionsSnapshot;

    private Bitmap? previousFrame;
    private Bitmap? currentFrame;
    private int currentFrameIndex;
    private int diagnosticFramePairs;
    private int totalFrames;
    private int stitchCount;
    private int lowConfidenceStitches;
    private int bestGuessStitches;
    private int settleTimeouts;
    private int warningCount;
    private int stationaryOverlayEvents;
    private int stationaryTileCount;
    private long stationaryPixelArea;
    private bool completed;

    public CaptureSessionRecorder(
        string captureMode,
        string targetDescription,
        ScrollingCaptureOptions options)
    {
        mode = captureMode;
        target = targetDescription;
        optionsSnapshot = new
        {
            options.StartDelay,
            options.AutoScrollTop,
            options.ScrollDelay,
            options.ScrollMethod,
            options.ScrollAmount,
            options.AutoIgnoreBottomEdge,
            options.ShowRegion,
            options.AdaptiveSettle,
            options.AdaptiveSettleProbeInterval,
            options.AdaptiveSettleStableSamples,
            options.AdaptiveSettleMaxDelay,
            options.AdaptiveSettleChangedFraction,
            options.SuppressStationaryOverlays
        };

        string sessionId = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..34];
        SessionDirectory = Path.Combine(SessionRoot, sessionId);
        diagnosticsDirectory = Path.Combine(SessionDirectory, "diagnostics");
        telemetryPath = Path.Combine(SessionDirectory, "telemetry.jsonl");
        sessionLogPath = Path.Combine(SessionDirectory, "session.log");

        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(diagnosticsDirectory);
        WriteSessionManifest(null, null, null, null);
        AppendSessionLog($"session started mode={Sanitize(mode)} target={Sanitize(target)}");
        LongCaptureLog.Info($"capture flight recorder started directory={LongCaptureLog.OneLine(SessionDirectory)}");
    }

    public static string SessionRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LongCapture",
        "CaptureSessions");

    public string SessionDirectory { get; }

    public static string? LatestCompletedSessionDirectory
    {
        get
        {
            lock (LatestSync)
            {
                if (!string.IsNullOrWhiteSpace(latestCompletedSessionDirectory) && Directory.Exists(latestCompletedSessionDirectory))
                {
                    return latestCompletedSessionDirectory;
                }
            }

            try
            {
                Directory.CreateDirectory(SessionRoot);
                return new DirectoryInfo(SessionRoot)
                    .EnumerateDirectories()
                    .OrderByDescending(directory => directory.LastWriteTimeUtc)
                    .Select(directory => directory.FullName)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }

    public void Attach(ScrollingCaptureOptions options)
    {
        options.TelemetrySink = OnTelemetry;
        options.FrameSink = OnFrame;
    }

    public void OnFrame(ScrollingCaptureFrameEvent frame)
    {
        lock (sync)
        {
            previousFrame?.Dispose();
            previousFrame = currentFrame;
            currentFrame = (Bitmap)frame.Frame.Clone();
            currentFrameIndex = frame.FrameIndex;
        }
    }

    public void OnTelemetry(ScrollingCaptureTelemetryEvent evt)
    {
        try
        {
            string line = JsonSerializer.Serialize(evt);
            lock (sync)
            {
                File.AppendAllText(telemetryPath, line + Environment.NewLine, Encoding.UTF8);
                UpdateCounters(evt);
            }

            if (evt.Kind is ScrollingCaptureTelemetryKind.Warning or ScrollingCaptureTelemetryKind.StationaryOverlayDetected)
            {
                SaveDiagnosticFramePair(evt);
            }

            if (evt.Kind is ScrollingCaptureTelemetryKind.Warning or ScrollingCaptureTelemetryKind.SettleCompleted or ScrollingCaptureTelemetryKind.StitchComputed)
            {
                AppendSessionLog(
                    $"{evt.Kind} frame={evt.FrameIndex} confidence={evt.Confidence:F3} changed={evt.ChangedFraction:F4} scroll={evt.EstimatedScrollPixels} matchRows={evt.MatchRows} timeout={evt.TimedOut} bestGuess={evt.UsedBestGuess} message={Sanitize(evt.Message)}");
            }
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"flight recorder telemetry write failed type={ex.GetType().Name} message={LongCaptureLog.OneLine(ex.Message)}");
        }
    }

    public CaptureSessionQuality Complete(
        ScrollingCaptureStatus status,
        string? savedPath,
        Size? resultSize)
    {
        lock (sync)
        {
            if (completed)
            {
                return LoadQualityOrFallback(status);
            }
            completed = true;
        }

        CaptureSessionQuality quality = BuildQuality(status, savedPath, resultSize);
        string qualityPath = Path.Combine(SessionDirectory, "quality.json");
        File.WriteAllText(qualityPath, JsonSerializer.Serialize(quality, jsonOptions), Encoding.UTF8);
        WriteSessionManifest(status, savedPath, resultSize, quality);
        AppendSessionLog($"session completed status={status} quality={quality.Status} score={quality.Score}/100 result={Sanitize(savedPath)}");

        lock (LatestSync)
        {
            latestCompletedSessionDirectory = SessionDirectory;
        }

        LongCaptureLog.Info(
            $"capture flight recorder completed status={quality.Status} score={quality.Score}/100 directory={LongCaptureLog.OneLine(SessionDirectory)}");
        return quality;
    }

    public static string ExportLatestBundle(string destinationDirectory)
    {
        string? source = LatestCompletedSessionDirectory;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            throw new InvalidOperationException("No completed LongCapture diagnostic session is available yet.");
        }

        Directory.CreateDirectory(destinationDirectory);
        string fileName = $"LongCapture-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        string destination = Path.Combine(destinationDirectory, fileName);
        if (File.Exists(destination)) File.Delete(destination);
        ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Optimal, includeBaseDirectory: true);
        return destination;
    }

    private void UpdateCounters(ScrollingCaptureTelemetryEvent evt)
    {
        switch (evt.Kind)
        {
            case ScrollingCaptureTelemetryKind.FrameCaptured:
                totalFrames = Math.Max(totalFrames, evt.FrameIndex);
                break;
            case ScrollingCaptureTelemetryKind.StitchComputed:
                stitchCount++;
                if (evt.Confidence < 0.50) lowConfidenceStitches++;
                if (evt.UsedBestGuess) bestGuessStitches++;
                break;
            case ScrollingCaptureTelemetryKind.SettleCompleted:
                if (evt.TimedOut) settleTimeouts++;
                break;
            case ScrollingCaptureTelemetryKind.Warning:
                warningCount++;
                break;
            case ScrollingCaptureTelemetryKind.StationaryOverlayDetected:
                stationaryOverlayEvents++;
                stationaryTileCount += evt.StationaryTileCount;
                stationaryPixelArea += evt.StationaryPixelArea;
                break;
        }
    }

    private void SaveDiagnosticFramePair(ScrollingCaptureTelemetryEvent evt)
    {
        Bitmap? before = null;
        Bitmap? after = null;
        int frame;
        lock (sync)
        {
            if (diagnosticFramePairs >= MaxDiagnosticFramePairs) return;
            if (currentFrame is null) return;
            diagnosticFramePairs++;
            frame = currentFrameIndex;
            before = previousFrame is null ? null : (Bitmap)previousFrame.Clone();
            after = (Bitmap)currentFrame.Clone();
        }

        try
        {
            string prefix = $"frame-{frame:0000}-{evt.Kind}";
            before?.Save(Path.Combine(diagnosticsDirectory, prefix + "-previous.png"), ImageFormat.Png);
            after.Save(Path.Combine(diagnosticsDirectory, prefix + "-current.png"), ImageFormat.Png);
            File.WriteAllText(
                Path.Combine(diagnosticsDirectory, prefix + "-event.json"),
                JsonSerializer.Serialize(evt, jsonOptions),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"diagnostic frame save failed type={ex.GetType().Name} message={LongCaptureLog.OneLine(ex.Message)}");
        }
        finally
        {
            before?.Dispose();
            after?.Dispose();
        }
    }

    private CaptureSessionQuality BuildQuality(
        ScrollingCaptureStatus status,
        string? savedPath,
        Size? resultSize)
    {
        int score = 100;
        var reasons = new List<string>();

        if (status == ScrollingCaptureStatus.Failed)
        {
            score -= 65;
            reasons.Add("capture engine reported Failed");
        }
        else if (status == ScrollingCaptureStatus.PartiallySuccessful)
        {
            score -= 18;
            reasons.Add("capture used a partial/best-guess stitch path");
        }

        if (string.IsNullOrWhiteSpace(savedPath) || resultSize is null || resultSize.Value.Width <= 0 || resultSize.Value.Height <= 0)
        {
            score -= 50;
            reasons.Add("no usable output image was saved");
        }

        if (lowConfidenceStitches > 0)
        {
            score -= Math.Min(28, lowConfidenceStitches * 7);
            reasons.Add($"{lowConfidenceStitches} low-confidence stitch(es)");
        }

        if (bestGuessStitches > 0)
        {
            score -= Math.Min(20, bestGuessStitches * 10);
            reasons.Add($"{bestGuessStitches} historical best-guess stitch(es)");
        }

        if (settleTimeouts > 0)
        {
            score -= Math.Min(15, settleTimeouts * 3);
            reasons.Add($"{settleTimeouts} adaptive-settle timeout(s)");
        }

        if (warningCount > 0)
        {
            score -= Math.Min(10, warningCount * 2);
            reasons.Add($"{warningCount} engine warning event(s)");
        }

        if (totalFrames > 2 && stitchCount < 2)
        {
            score -= 12;
            reasons.Add("multiple frames were captured but too few successful stitch decisions were recorded");
        }

        score = Math.Max(0, Math.Min(100, score));
        string qualityStatus = score >= 90 && status == ScrollingCaptureStatus.Successful
            ? "PASS"
            : score >= 75 && status != ScrollingCaptureStatus.Failed
                ? "PASS_WITH_WARNING"
                : "FAIL";

        if (reasons.Count == 0)
        {
            reasons.Add("no capture-integrity warning was observed by the v0.1.3 flight recorder");
        }

        return new CaptureSessionQuality
        {
            Status = qualityStatus,
            Score = score,
            EngineStatus = status.ToString(),
            TotalFrames = totalFrames,
            StitchCount = stitchCount,
            LowConfidenceStitches = lowConfidenceStitches,
            BestGuessStitches = bestGuessStitches,
            SettleTimeouts = settleTimeouts,
            WarningEvents = warningCount,
            StationaryOverlayEvents = stationaryOverlayEvents,
            StationaryTileCount = stationaryTileCount,
            StationaryPixelArea = stationaryPixelArea,
            ResultPath = savedPath ?? string.Empty,
            ResultWidth = resultSize?.Width ?? 0,
            ResultHeight = resultSize?.Height ?? 0,
            Reasons = reasons.ToArray()
        };
    }

    private void WriteSessionManifest(
        ScrollingCaptureStatus? status,
        string? savedPath,
        Size? resultSize,
        CaptureSessionQuality? quality)
    {
        var manifest = new
        {
            schema = "longcapture.capture-session.v1",
            appVersion = StandaloneVersion.Value,
            started,
            completed = status.HasValue ? DateTimeOffset.Now : (DateTimeOffset?)null,
            mode,
            target,
            options = optionsSnapshot,
            engineStatus = status?.ToString(),
            result = savedPath is null ? null : new
            {
                path = savedPath,
                width = resultSize?.Width ?? 0,
                height = resultSize?.Height ?? 0
            },
            quality,
            diagnostics = new
            {
                telemetry = Path.GetFileName(telemetryPath),
                sessionLog = Path.GetFileName(sessionLogPath),
                anomalyDirectory = "diagnostics"
            }
        };

        File.WriteAllText(
            Path.Combine(SessionDirectory, "session.json"),
            JsonSerializer.Serialize(manifest, jsonOptions),
            Encoding.UTF8);
    }

    private CaptureSessionQuality LoadQualityOrFallback(ScrollingCaptureStatus status)
    {
        try
        {
            string path = Path.Combine(SessionDirectory, "quality.json");
            CaptureSessionQuality? quality = JsonSerializer.Deserialize<CaptureSessionQuality>(File.ReadAllText(path));
            if (quality is not null) return quality;
        }
        catch
        {
        }

        return new CaptureSessionQuality
        {
            Status = status == ScrollingCaptureStatus.Successful ? "PASS_WITH_WARNING" : "FAIL",
            Score = status == ScrollingCaptureStatus.Successful ? 75 : 0,
            EngineStatus = status.ToString(),
            Reasons = new[] { "quality report could not be reloaded" }
        };
    }

    private void AppendSessionLog(string value)
    {
        try
        {
            File.AppendAllText(
                sessionLogPath,
                $"{DateTimeOffset.Now:O} {value}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    public void Dispose()
    {
        lock (sync)
        {
            previousFrame?.Dispose();
            currentFrame?.Dispose();
            previousFrame = null;
            currentFrame = null;
        }
    }
}

internal sealed class CaptureSessionQuality
{
    public string Status { get; init; } = "FAIL";
    public int Score { get; init; }
    public string EngineStatus { get; init; } = string.Empty;
    public int TotalFrames { get; init; }
    public int StitchCount { get; init; }
    public int LowConfidenceStitches { get; init; }
    public int BestGuessStitches { get; init; }
    public int SettleTimeouts { get; init; }
    public int WarningEvents { get; init; }
    public int StationaryOverlayEvents { get; init; }
    public int StationaryTileCount { get; init; }
    public long StationaryPixelArea { get; init; }
    public string ResultPath { get; init; } = string.Empty;
    public int ResultWidth { get; init; }
    public int ResultHeight { get; init; }
    public string[] Reasons { get; init; } = Array.Empty<string>();
}
