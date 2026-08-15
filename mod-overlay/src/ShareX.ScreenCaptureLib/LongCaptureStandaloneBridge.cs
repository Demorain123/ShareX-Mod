#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

public enum LongCaptureStandaloneMode
{
    Normal,
    SmartWeb,
    Teach,
    RunRecipe
}

public sealed record LongCaptureStandaloneReadiness(
    bool Ready,
    string Detail,
    string RecipePath);

public sealed record LongCaptureBrowserLaunchResult(
    bool Started,
    string Detail,
    string Endpoint,
    string ProfileDirectory);

public sealed record LongCaptureRecipeStepInfo(
    int Index,
    string Title,
    string Detail,
    bool CanDisable,
    bool Enabled,
    string Risk);

public sealed record LongCaptureRecipeReviewInfo(
    string RecipePath,
    string Summary,
    bool HasValidApproval,
    bool Approved,
    LongCaptureRecipeStepInfo[] Steps,
    int[] DisabledSteps);

public sealed record LongCaptureQualityInfo(
    string Status,
    string Confidence,
    int IntegrityScore,
    string SummaryPath,
    string SessionDirectory);

public static class LongCaptureStandaloneBridge
{
    public static void ConfigureMode(LongCaptureStandaloneMode mode, string? recipePath = null)
    {
        ShareXModCaptureMode internalMode = ToInternal(mode);
        if (!string.IsNullOrWhiteSpace(recipePath))
        {
            ShareXModCaptureModeProfile.SetRecipePath(recipePath);
        }
        ShareXModCaptureModeProfile.SetMode(internalMode);
    }

    public static string GetLatestRecipePath() =>
        ShareXModCaptureModeProfile.FindLatestRecipe() ?? string.Empty;

    public static string DescribeRecipe() =>
        ShareXModCaptureModeProfile.DescribeRecipe();

    public static async Task<LongCaptureStandaloneReadiness> ProbeAsync(
        LongCaptureStandaloneMode mode,
        string? recipePath = null)
    {
        ConfigureMode(mode, recipePath);
        ShareXModCaptureMode internalMode = ToInternal(mode);
        ShareXModCaptureModeProfileData profile = ShareXModCaptureModeProfile.Current;
        ShareXModChromeReadinessResult result =
            await ShareXModChromeReadiness.ProbeAsync(
                ShareXModV04Settings.Load(),
                internalMode,
                profile.RecipePath);

        return new LongCaptureStandaloneReadiness(
            result.Ready,
            result.Detail,
            profile.RecipePath);
    }

    public static async Task<LongCaptureBrowserLaunchResult> LaunchCaptureBrowserAsync()
    {
        ShareXModChromeDedicatedProfileResult result =
            await ShareXModChromeDedicatedProfileLauncher.LaunchAsync(
                ShareXModV04Settings.Load());

        return new LongCaptureBrowserLaunchResult(
            result.Started,
            result.Detail,
            result.Endpoint,
            result.ProfileDirectory);
    }

    public static LongCaptureRecipeReviewInfo? LoadRecipeReview(string? recipePath)
    {
        ShareXModRecipeReviewSnapshot? snapshot =
            ShareXModCaptureRecipeReviewService.Load(recipePath);
        return ConvertReview(snapshot);
    }

    public static LongCaptureRecipeReviewInfo? SaveRecipeReview(
        string recipePath,
        IEnumerable<int> disabledSteps,
        bool approved,
        string? note = null)
    {
        ShareXModRecipeReviewSnapshot? snapshot =
            ShareXModCaptureRecipeReviewService.Save(
                recipePath,
                disabledSteps,
                approved,
                note);
        return ConvertReview(snapshot);
    }

    public static LongCaptureQualityInfo? FindLatestQualityInfo()
    {
        try
        {
            IEnumerable<string> roots = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ShareX-Mod", "CaptureSessions"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShareX-Mod",
                    "CaptureSessions")
            };

            string? summary = roots
                .Where(Directory.Exists)
                .SelectMany(root => SafeEnumerate(root, "quality-summary.json"))
                .OrderByDescending(path =>
                {
                    try { return File.GetLastWriteTimeUtc(path); }
                    catch { return DateTime.MinValue; }
                })
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(summary) || !File.Exists(summary)) return null;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(summary));
            JsonElement rootElement = document.RootElement;
            string status = rootElement.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString() ?? "unknown"
                : "unknown";
            string confidence = rootElement.TryGetProperty("confidence", out JsonElement confidenceElement)
                ? confidenceElement.GetString() ?? "unknown"
                : "unknown";

            int score = 0;
            if (rootElement.TryGetProperty("originalCaptureEvidence", out JsonElement evidence) &&
                evidence.TryGetProperty("baseIntegrityScore", out JsonElement scoreElement))
            {
                scoreElement.TryGetInt32(out score);
            }

            string qualityDirectory = Path.GetDirectoryName(summary) ?? string.Empty;
            string sessionDirectory = Directory.GetParent(qualityDirectory)?.FullName ?? qualityDirectory;

            // v0.1.4 could report clean/high/100 even while the final mosaic visibly contained a
            // fixed Back/counter control once per scroll step. The anchor compositor now emits
            // session-local evidence. If it detected/repaired stationary tiles and the newest tail
            // is still pending (no future frame exists yet to reveal the covered pixels), never
            // silently promote the capture to clean/high.
            string compositorEvidence = Path.Combine(
                sessionDirectory,
                "anchor-compositor-v015",
                "fixed-overlay-evidence.jsonl");
            if (File.Exists(compositorEvidence))
            {
                ShareXModAnchorCompositorTelemetry telemetry = ShareXModAnchorCompositorV014.SnapshotTelemetry();
                if (telemetry.DetectedStationaryTiles > 0 && telemetry.PendingTailTiles > 0)
                {
                    status = "partially-repaired";
                    confidence = telemetry.LowOverlapRisk ? "low" : "medium";
                }
            }

            return new LongCaptureQualityInfo(
                status,
                confidence,
                score,
                summary,
                sessionDirectory);
        }
        catch
        {
            return null;
        }
    }

    private static LongCaptureRecipeReviewInfo? ConvertReview(ShareXModRecipeReviewSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        return new LongCaptureRecipeReviewInfo(
            snapshot.RecipePath,
            snapshot.Summary,
            snapshot.HasValidApproval,
            snapshot.Approved,
            snapshot.Steps.Select(step => new LongCaptureRecipeStepInfo(
                step.Index,
                step.Title,
                step.Detail,
                step.CanDisable,
                step.Enabled,
                step.Risk)).ToArray(),
            snapshot.DisabledSteps);
    }

    private static ShareXModCaptureMode ToInternal(LongCaptureStandaloneMode mode) => mode switch
    {
        LongCaptureStandaloneMode.SmartWeb => ShareXModCaptureMode.SmartWeb,
        LongCaptureStandaloneMode.Teach => ShareXModCaptureMode.RecordRecipe,
        LongCaptureStandaloneMode.RunRecipe => ShareXModCaptureMode.RunRecipe,
        _ => ShareXModCaptureMode.Normal
    };

    private static IEnumerable<string> SafeEnumerate(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
