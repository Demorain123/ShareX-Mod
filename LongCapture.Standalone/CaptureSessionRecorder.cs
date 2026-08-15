using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class CaptureSessionRecorder : IDisposable
{
    private const long MaxCopiedEvidenceBytes = 128L * 1024L * 1024L;
    private const long MaxSingleEvidenceFileBytes = 16L * 1024L * 1024L;
    private static readonly object LatestSync = new();
    private static string? latestCompletedSessionDirectory;

    private readonly DateTimeOffset started = DateTimeOffset.Now;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly string mode;
    private readonly string target;
    private readonly object optionsSnapshot;
    private readonly string sessionLogPath;
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
            options.ShowRegion
        };

        string sessionId = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-p{Environment.ProcessId}-{Guid.NewGuid():N}";
        SessionDirectory = Path.Combine(SessionRoot, sessionId);
        sessionLogPath = Path.Combine(SessionDirectory, "session.log");
        Directory.CreateDirectory(SessionDirectory);
        WriteManifest(null, null, null, null, null);
        AppendSessionLog($"session-start mode={Sanitize(mode)} target={Sanitize(target)}");
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
                if (!Directory.Exists(SessionRoot)) return null;
                return new DirectoryInfo(SessionRoot)
                    .EnumerateDirectories()
                    .Where(directory => File.Exists(Path.Combine(directory.FullName, "quality.json")))
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

    public CaptureSessionQuality Complete(
        ScrollingCaptureStatus status,
        string? savedPath,
        Size? resultSize,
        LongCaptureQualityInfo? engineQuality)
    {
        if (completed)
        {
            return LoadQualityOrFallback(status);
        }
        completed = true;

        LongCaptureQualityInfo? correlated = IsCurrentEngineEvidence(engineQuality) ? engineQuality : null;
        EngineEvidenceCopy copy = CopyEngineEvidence(correlated);
        CaptureSessionQuality quality = BuildQuality(status, savedPath, resultSize, correlated, copy);

        string qualityPath = Path.Combine(SessionDirectory, "quality.json");
        File.WriteAllText(qualityPath, JsonSerializer.Serialize(quality, jsonOptions), Encoding.UTF8);
        WriteManifest(status, savedPath, resultSize, quality, correlated);
        AppendSessionLog(
            $"session-end engineStatus={status} quality={quality.Status} score={quality.Score}/100 engineEvidence={Sanitize(correlated?.SessionDirectory)} copiedFiles={copy.FilesCopied} copiedBytes={copy.BytesCopied} truncated={copy.Truncated}");

        lock (LatestSync)
        {
            latestCompletedSessionDirectory = SessionDirectory;
        }

        LongCaptureLog.Info(
            $"capture flight recorder completed status={quality.Status} score={quality.Score}/100 engineEvidence={(correlated is null ? "not-correlated" : "correlated")} directory={LongCaptureLog.OneLine(SessionDirectory)}");
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
        string destination = Path.Combine(
            destinationDirectory,
            $"LongCapture-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(destination)) File.Delete(destination);
        ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Optimal, includeBaseDirectory: true);
        return destination;
    }

    private bool IsCurrentEngineEvidence(LongCaptureQualityInfo? quality)
    {
        if (quality is null || string.IsNullOrWhiteSpace(quality.SummaryPath) || !File.Exists(quality.SummaryPath))
        {
            return false;
        }

        try
        {
            DateTimeOffset written = File.GetLastWriteTimeUtc(quality.SummaryPath);
            // Filesystem timestamps can have coarse resolution. Accept a small margin, but do
            // not accidentally attach a previous run's quality summary to this capture.
            return written >= started.UtcDateTime.AddSeconds(-3);
        }
        catch
        {
            return false;
        }
    }

    private EngineEvidenceCopy CopyEngineEvidence(LongCaptureQualityInfo? quality)
    {
        if (quality is null || string.IsNullOrWhiteSpace(quality.SessionDirectory) || !Directory.Exists(quality.SessionDirectory))
        {
            File.WriteAllText(
                Path.Combine(SessionDirectory, "engine-evidence-unavailable.txt"),
                "No new ShareX-Mod capture-session evidence could be correlated with this LongCapture run.\r\n",
                Encoding.UTF8);
            return new EngineEvidenceCopy(0, 0, false);
        }

        string destinationRoot = Path.Combine(SessionDirectory, "engine-evidence");
        Directory.CreateDirectory(destinationRoot);
        long copiedBytes = 0;
        int copiedFiles = 0;
        bool truncated = false;
        var skipped = new List<string>();

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(quality.SessionDirectory, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex)
        {
            skipped.Add($"enumeration failed: {ex.GetType().Name}: {ex.Message}");
            candidates = Array.Empty<string>();
        }

        foreach (string source in candidates)
        {
            try
            {
                FileInfo info = new(source);
                string extension = info.Extension.ToLowerInvariant();
                bool evidenceType = extension is ".json" or ".jsonl" or ".txt" or ".log" or ".csv" or ".png";
                if (!evidenceType)
                {
                    skipped.Add($"unsupported type: {Path.GetRelativePath(quality.SessionDirectory, source)}");
                    continue;
                }

                if (info.Length > MaxSingleEvidenceFileBytes || copiedBytes + info.Length > MaxCopiedEvidenceBytes)
                {
                    truncated = true;
                    skipped.Add($"size cap: {Path.GetRelativePath(quality.SessionDirectory, source)} ({info.Length} bytes)");
                    continue;
                }

                string relative = Path.GetRelativePath(quality.SessionDirectory, source);
                string destination = Path.Combine(destinationRoot, relative);
                string? parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.Copy(source, destination, overwrite: true);
                copiedBytes += info.Length;
                copiedFiles++;
            }
            catch (Exception ex)
            {
                skipped.Add($"copy failed: {source}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllText(
            Path.Combine(destinationRoot, "copy-manifest.json"),
            JsonSerializer.Serialize(new
            {
                source = quality.SessionDirectory,
                qualitySummary = quality.SummaryPath,
                copiedFiles,
                copiedBytes,
                truncated,
                maxBytes = MaxCopiedEvidenceBytes,
                maxSingleFileBytes = MaxSingleEvidenceFileBytes,
                skipped
            }, jsonOptions),
            Encoding.UTF8);

        return new EngineEvidenceCopy(copiedFiles, copiedBytes, truncated);
    }

    private CaptureSessionQuality BuildQuality(
        ScrollingCaptureStatus status,
        string? savedPath,
        Size? resultSize,
        LongCaptureQualityInfo? engineQuality,
        EngineEvidenceCopy evidenceCopy)
    {
        int score = engineQuality?.IntegrityScore is > 0 and <= 100
            ? engineQuality.IntegrityScore
            : 75;
        var reasons = new List<string>();

        if (status == ScrollingCaptureStatus.Failed)
        {
            score = Math.Min(score, 30);
            reasons.Add("capture engine reported Failed");
        }
        else if (status == ScrollingCaptureStatus.PartiallySuccessful)
        {
            score = Math.Min(score, 78);
            reasons.Add("capture engine reported PartiallySuccessful");
        }

        if (string.IsNullOrWhiteSpace(savedPath) || resultSize is null || resultSize.Value.Width <= 0 || resultSize.Value.Height <= 0)
        {
            score = Math.Min(score, 25);
            reasons.Add("no usable output image was saved");
        }

        if (engineQuality is null)
        {
            score = Math.Min(score, 72);
            reasons.Add("no new final ShareX-Mod Quality Guard summary could be correlated with this capture");
        }
        else
        {
            if (!string.Equals(engineQuality.Confidence, "high", StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Min(score, string.Equals(engineQuality.Confidence, "medium", StringComparison.OrdinalIgnoreCase) ? 84 : 70);
                reasons.Add($"engine quality confidence is {engineQuality.Confidence}");
            }

            if (string.Equals(engineQuality.Status, "unresolved", StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Min(score, 68);
                reasons.Add("Quality Guard reports unresolved suspect ranges");
            }
            else if (string.Equals(engineQuality.Status, "partially-repaired", StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Min(score, 82);
                reasons.Add("Quality Guard reports partially repaired suspect ranges");
            }
        }

        if (evidenceCopy.Truncated)
        {
            reasons.Add("diagnostic evidence copy hit the safety size cap; original engine evidence remains on disk");
        }

        score = Math.Clamp(score, 0, 100);
        string qualityStatus =
            score >= 90 && status == ScrollingCaptureStatus.Successful && engineQuality is not null
                ? "PASS"
                : score >= 75 && status != ScrollingCaptureStatus.Failed
                    ? "PASS_WITH_WARNING"
                    : "FAIL";

        if (reasons.Count == 0)
        {
            reasons.Add("capture engine and final Quality Guard did not report a blocking integrity issue");
        }

        return new CaptureSessionQuality
        {
            Status = qualityStatus,
            Score = score,
            EngineStatus = status.ToString(),
            EngineQualityStatus = engineQuality?.Status ?? "unavailable",
            EngineConfidence = engineQuality?.Confidence ?? "unavailable",
            EngineIntegrityScore = engineQuality?.IntegrityScore ?? 0,
            EngineQualitySummary = engineQuality?.SummaryPath ?? string.Empty,
            EngineSessionDirectory = engineQuality?.SessionDirectory ?? string.Empty,
            EvidenceFilesCopied = evidenceCopy.FilesCopied,
            EvidenceBytesCopied = evidenceCopy.BytesCopied,
            EvidenceTruncated = evidenceCopy.Truncated,
            ResultPath = savedPath ?? string.Empty,
            ResultWidth = resultSize?.Width ?? 0,
            ResultHeight = resultSize?.Height ?? 0,
            Reasons = reasons.ToArray()
        };
    }

    private void WriteManifest(
        ScrollingCaptureStatus? status,
        string? savedPath,
        Size? resultSize,
        CaptureSessionQuality? quality,
        LongCaptureQualityInfo? engineQuality)
    {
        var manifest = new
        {
            schema = "longcapture.capture-session.v2",
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
            engineEvidence = engineQuality is null ? null : new
            {
                engineQuality.Status,
                engineQuality.Confidence,
                engineQuality.IntegrityScore,
                engineQuality.SummaryPath,
                engineQuality.SessionDirectory
            },
            quality,
            diagnostics = new
            {
                sessionLog = Path.GetFileName(sessionLogPath),
                engineEvidenceCopy = "engine-evidence"
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
            Score = status == ScrollingCaptureStatus.Successful ? 70 : 0,
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
        // The recorder intentionally owns no long-lived frame buffers. Detailed per-frame
        // evidence is produced by the existing ShareX-Mod Capture Map / Robust session and
        // correlated into this session at Complete().
    }

    private readonly record struct EngineEvidenceCopy(int FilesCopied, long BytesCopied, bool Truncated);
}

internal sealed class CaptureSessionQuality
{
    public string Status { get; init; } = "FAIL";
    public int Score { get; init; }
    public string EngineStatus { get; init; } = string.Empty;
    public string EngineQualityStatus { get; init; } = string.Empty;
    public string EngineConfidence { get; init; } = string.Empty;
    public int EngineIntegrityScore { get; init; }
    public string EngineQualitySummary { get; init; } = string.Empty;
    public string EngineSessionDirectory { get; init; } = string.Empty;
    public int EvidenceFilesCopied { get; init; }
    public long EvidenceBytesCopied { get; init; }
    public bool EvidenceTruncated { get; init; }
    public string ResultPath { get; init; } = string.Empty;
    public int ResultWidth { get; init; }
    public int ResultHeight { get; init; }
    public string[] Reasons { get; init; } = Array.Empty<string>();
}
