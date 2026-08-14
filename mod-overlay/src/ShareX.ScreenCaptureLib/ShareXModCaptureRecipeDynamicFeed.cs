#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureRecipeDynamicFeed
{
    private const int ImageSnapshotEveryCapturedRanges = 2;

    private sealed record Metrics(double CssWidth, double CssHeight, double ViewportHeight, double DipPerCss);
    private sealed record Part(string File, double StartY, double EndY, int Width, int Height);
    private sealed record GrowthPass(int Pass, double BeforeHeight, double AfterHeight, bool Grew, int StableWaitMs);

    public static async Task<ShareXModChromeBackgroundCaptureResult?> TryRunAsync(
        ShareXModChromeCdpClient client,
        ShareXModChromeTarget target,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        if (!settings.CaptureRecipeAutomationEnabled ||
            string.IsNullOrWhiteSpace(settings.CaptureRecipeReplayPath) ||
            !File.Exists(settings.CaptureRecipeReplayPath))
        {
            return null;
        }

        ShareXModCaptureRecipe? recipe;
        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            recipe = JsonSerializer.Deserialize<ShareXModCaptureRecipe>(
                await File.ReadAllTextAsync(settings.CaptureRecipeReplayPath),
                options);
        }
        catch
        {
            return null;
        }

        if (recipe == null ||
            !recipe.StopBoundary.Kind.Equals("dynamic-feed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string directory = ResolveDirectory(settings);
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("capture-recipe-dynamic-feed", directory);

            Metrics initial = await GetMetricsAsync(client);
            if (initial.CssWidth <= 0 || initial.CssHeight <= 0)
            {
                return null;
            }

            double startY = Math.Clamp(
                recipe.StartBoundary.DocumentY ??
                recipe.Steps.FirstOrDefault(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange)?.StartY ??
                0,
                0,
                Math.Max(0, initial.CssHeight - 1));

            int maxSteps = Math.Clamp(
                recipe.StopBoundary.MaxSteps ?? settings.CaptureRecipeDynamicFeedMaxSteps,
                1,
                100000);

            int maxDurationSeconds = Math.Clamp(
                recipe.StopBoundary.MaxDurationSeconds ?? settings.CaptureRecipeDynamicFeedMaxDurationSeconds,
                1,
                86400);

            int maxUnchanged = Math.Clamp(
                recipe.StopBoundary.MaxUnchangedPasses ?? settings.CaptureRecipeDynamicFeedMaxUnchangedPasses,
                1,
                100);

            double growthThreshold = Math.Clamp(
                settings.CaptureRecipeDynamicFeedGrowthThresholdCss,
                1,
                10000);

            int tileHeight = Math.Clamp(settings.ChromeBackgroundTileHeight, 900, 12000);
            double maxCssHeight = Math.Max(10000, settings.ChromeBackgroundMaxCssHeight);

            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Part> parts = new();
            List<GrowthPass> growthPasses = new();

            string pageKey = recipe.Steps
                .FirstOrDefault(x => x.Kind == ShareXModCaptureRecipeStepKind.PageCheckpoint)?.PageKey ??
                recipe.Steps.FirstOrDefault()?.PageKey ??
                "dynamic-feed";

            ShareXModDynamicFeedRollingGuard.Session rollingGuard =
                ShareXModDynamicFeedRollingGuard.Create(
                    client,
                    settings,
                    directory,
                    pageKey);

            double y = startY;
            double knownEnd = Math.Min(initial.CssHeight, maxCssHeight);
            int steps = 0;
            int unchangedPasses = 0;
            int imageSnapshotIndex = 0;
            bool stoppedByUser = false;
            bool truncated = false;
            string stopReason = "dynamic-feed-complete";

            while (steps < maxSteps && stopwatch.Elapsed.TotalSeconds < maxDurationSeconds)
            {
                if (shouldStop?.Invoke() == true)
                {
                    stoppedByUser = true;
                    stopReason = "manual-stop";
                    break;
                }

                Metrics metrics = await GetMetricsAsync(client);
                knownEnd = Math.Min(Math.Max(knownEnd, metrics.CssHeight), maxCssHeight);

                if (y < knownEnd - 1)
                {
                    double rangeStart = y;
                    double cut = Math.Min(knownEnd, y + tileHeight);
                    double height = cut - y;
                    if (height <= 1)
                    {
                        break;
                    }

                    ShareXModBrowserStabilityResult stable =
                        await ShareXModBrowserStabilityProbe.WaitForRegionAsync(
                            client,
                            settings,
                            y,
                            height);

                    if (settings.ChromeRepairRequireStableRegion && !stable.Stable)
                    {
                        stopReason = "dynamic-feed-region-unstable";
                        truncated = true;
                        break;
                    }

                    metrics = await GetMetricsAsync(client);
                    double actualEnd = Math.Min(cut, metrics.CssHeight);
                    height = actualEnd - y;
                    if (height <= 1)
                    {
                        break;
                    }

                    double dip = metrics.DipPerCss > 0 ? metrics.DipPerCss : 1;
                    using JsonDocument response = await client.SendCdpCommandAsync(
                        "Page.captureScreenshot",
                        new
                        {
                            format = "png",
                            fromSurface = true,
                            captureBeyondViewport = true,
                            optimizeForSpeed = true,
                            clip = new
                            {
                                x = 0d,
                                y = y * dip,
                                width = metrics.CssWidth * dip,
                                height = height * dip,
                                scale = 1d
                            }
                        });

                    string? base64 = response.RootElement
                        .GetProperty("result")
                        .GetProperty("data")
                        .GetString();

                    if (string.IsNullOrWhiteSpace(base64))
                    {
                        stopReason = "dynamic-feed-screenshot-empty";
                        truncated = true;
                        break;
                    }

                    byte[] png = Convert.FromBase64String(base64);
                    string file = $"part_{parts.Count + 1:D5}.png";
                    string path = Path.Combine(directory, file);
                    await File.WriteAllBytesAsync(path, png);

                    using (MemoryStream stream = new(png, writable: false))
                    using (Bitmap bitmap = new(stream))
                    {
                        parts.Add(new Part(file, rangeStart, actualEnd, bitmap.Width, bitmap.Height));
                    }

                    await rollingGuard.RecordAsync(file, rangeStart, actualEnd);

                    y = actualEnd;
                    steps++;

                    ShareXModDynamicFeedGuardPassResult guardPass =
                        await rollingGuard.VerifyDueAsync(
                            y,
                            flush: false,
                            shouldStop);
                    ApplyGuardReplacements(directory, parts, guardPass.Replacements);

                    if (settings.DynamicFeedCollectImages &&
                        parts.Count > 0 &&
                        parts.Count % ImageSnapshotEveryCapturedRanges == 0)
                    {
                        await ShareXModDynamicFeedImageCollector.CollectAsync(
                            client,
                            settings,
                            directory,
                            $"range_{++imageSnapshotIndex:D5}_y_{Math.Round(y):0}");
                    }

                    continue;
                }

                // Current known content has been captured. Move to the live bottom only to trigger
                // the site's own lazy/infinite-load mechanism; do not treat this scroll as a fixed
                // screenshot step or assume a page number exists.
                double beforeHeight = metrics.CssHeight;
                double triggerY = Math.Max(0, beforeHeight - metrics.ViewportHeight * 0.35);

                using (JsonDocument _ = await client.EvaluateAsync(
                    $"window.scrollTo({{top:{triggerY.ToString(System.Globalization.CultureInfo.InvariantCulture)},behavior:'instant'}}); true",
                    false))
                {
                }

                ShareXModBrowserStabilityResult bottomStable =
                    await ShareXModBrowserStabilityProbe.WaitForRegionAsync(
                        client,
                        settings,
                        Math.Max(0, beforeHeight - metrics.ViewportHeight * 1.5),
                        Math.Max(1, metrics.ViewportHeight * 1.5));

                Metrics after = await GetMetricsAsync(client);
                double afterHeight = after.CssHeight;
                bool grew = afterHeight >= beforeHeight + growthThreshold;

                growthPasses.Add(new GrowthPass(
                    growthPasses.Count + 1,
                    beforeHeight,
                    afterHeight,
                    grew,
                    bottomStable.ElapsedMs));

                steps++;

                ShareXModDynamicFeedGuardPassResult bottomGuardPass =
                    await rollingGuard.VerifyDueAsync(
                        y,
                        flush: false,
                        shouldStop);
                ApplyGuardReplacements(directory, parts, bottomGuardPass.Replacements);

                if (grew)
                {
                    knownEnd = Math.Min(Math.Max(knownEnd, afterHeight), maxCssHeight);
                    unchangedPasses = 0;
                    continue;
                }

                unchangedPasses++;
                if (unchangedPasses >= maxUnchanged)
                {
                    stopReason = "dynamic-feed-no-new-content";
                    break;
                }
            }

            if (steps >= maxSteps)
            {
                stopReason = "dynamic-feed-max-steps";
                truncated = true;
            }
            else if (stopwatch.Elapsed.TotalSeconds >= maxDurationSeconds)
            {
                stopReason = "dynamic-feed-max-duration";
                truncated = true;
            }
            else if (y >= maxCssHeight)
            {
                stopReason = "dynamic-feed-max-css-height";
                truncated = true;
            }

            // Before the DOM can be released, verify/repair every unsealed recent range and take one
            // last native-image snapshot. A manual Stop still performs this quality flush.
            ShareXModDynamicFeedGuardPassResult finalGuardPass =
                await rollingGuard.VerifyDueAsync(
                    y,
                    flush: true,
                    shouldStop: null);
            ApplyGuardReplacements(directory, parts, finalGuardPass.Replacements);

            if (settings.DynamicFeedCollectImages)
            {
                await ShareXModDynamicFeedImageCollector.CollectAsync(
                    client,
                    settings,
                    directory,
                    $"final_y_{Math.Round(y):0}");
            }

            if (finalGuardPass.UnresolvedCount > 0)
            {
                truncated = true;
                stopReason = stopReason == "manual-stop"
                    ? "manual-stop-with-unresolved-ranges"
                    : "dynamic-feed-guard-unresolved";
            }

            parts = parts
                .OrderBy(x => x.StartY)
                .ThenBy(x => x.EndY)
                .ToList();

            if (parts.Count == 0)
            {
                await WriteManifestAsync(
                    directory,
                    recipe,
                    target,
                    parts,
                    growthPasses,
                    startY,
                    y,
                    knownEnd,
                    steps,
                    unchangedPasses,
                    stoppedByUser,
                    truncated,
                    stopReason,
                    null,
                    0,
                    0,
                    stopwatch.ElapsedMilliseconds);
                return null;
            }

            int sourceWidth = parts[0].Width;
            bool sameWidth = parts.All(x => x.Width == sourceWidth);
            long sourceHeight = parts.Sum(x => (long)x.Height);
            string? finalPng = null;

            if (sameWidth)
            {
                try
                {
                    finalPng = Path.Combine(directory, "capture-full.png");
                    ShareXModSegmentedPngWriter.Write(
                        finalPng,
                        parts.Select(x => Path.Combine(directory, x.File)).ToArray(),
                        sourceWidth,
                        sourceHeight);
                }
                catch
                {
                    finalPng = null;
                }
            }

            string manifestPath = Path.Combine(directory, "dynamic-feed-run.json");
            await WriteManifestAsync(
                directory,
                recipe,
                target,
                parts,
                growthPasses,
                startY,
                y,
                knownEnd,
                steps,
                unchangedPasses,
                stoppedByUser,
                truncated,
                stopReason,
                finalPng,
                sourceWidth,
                sourceHeight,
                stopwatch.ElapsedMilliseconds);

            Bitmap preview = CreatePreview(
                directory,
                parts,
                sourceWidth,
                sourceHeight,
                settings.SegmentPreviewMaxHeight);

            ShareXModSegmentedOutputRegistry.Register(
                preview,
                manifestPath,
                finalPng ?? string.Empty,
                sourceWidth,
                sourceHeight);

            return new ShareXModChromeBackgroundCaptureResult
            {
                Preview = preview,
                DirectoryPath = directory,
                ManifestPath = manifestPath,
                PartCount = parts.Count,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                StoppedByUser = stoppedByUser,
                TruncatedBySafetyLimit = truncated
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyGuardReplacements(
        string directory,
        List<Part> parts,
        IReadOnlyList<ShareXModDynamicFeedGuardReplacement> replacements)
    {
        foreach (ShareXModDynamicFeedGuardReplacement replacement in replacements)
        {
            int index = parts.FindIndex(x =>
                string.Equals(x.File, replacement.OldFile, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            ShareXModDynamicFeedRollingGuard.ArchiveReplacedPart(
                directory,
                replacement.OldFile);

            parts.RemoveAt(index);
            foreach (ShareXModDynamicFeedGuardReplacementPart item in replacement.NewParts
                         .OrderBy(x => x.StartY)
                         .ThenBy(x => x.EndY))
            {
                parts.Insert(
                    index++,
                    new Part(
                        item.File,
                        item.StartY,
                        item.EndY,
                        item.Width,
                        item.Height));
            }
        }
    }

    private static async Task<Metrics> GetMetricsAsync(ShareXModChromeCdpClient client)
    {
        using JsonDocument layout = await client.SendCdpCommandAsync("Page.getLayoutMetrics");
        JsonElement result = layout.RootElement.GetProperty("result");
        JsonElement css = result.TryGetProperty("cssContentSize", out JsonElement cssSize)
            ? cssSize
            : result.GetProperty("contentSize");
        JsonElement dip = result.TryGetProperty("contentSize", out JsonElement dipSize)
            ? dipSize
            : css;

        double cssWidth = css.GetProperty("width").GetDouble();
        double cssHeight = css.GetProperty("height").GetDouble();
        double dipWidth = dip.GetProperty("width").GetDouble();
        double dipPerCss = cssWidth > 0 && dipWidth > 0 ? dipWidth / cssWidth : 1;

        using JsonDocument runtime = await client.EvaluateAsync(
            "window.innerHeight || document.documentElement.clientHeight || 1",
            false);
        double viewport = runtime.RootElement
            .GetProperty("result")
            .GetProperty("result")
            .GetProperty("value")
            .GetDouble();

        return new Metrics(cssWidth, cssHeight, Math.Max(1, viewport), dipPerCss);
    }

    private static Bitmap CreatePreview(
        string directory,
        IReadOnlyList<Part> parts,
        int sourceWidth,
        long sourceHeight,
        int configuredMaxHeight)
    {
        int maxHeight = Math.Clamp(configuredMaxHeight, 2000, 30000);
        double scale = Math.Min(1, maxHeight / (double)Math.Max(1, sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        Bitmap preview = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(preview);
        g.Clear(Color.White);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int y = 0;
        foreach (Part part in parts)
        {
            using Bitmap image = new(Path.Combine(directory, part.File));
            int drawHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            g.DrawImage(
                image,
                new Rectangle(0, y, width, drawHeight),
                new Rectangle(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            y += drawHeight;
        }

        return preview;
    }

    private static async Task WriteManifestAsync(
        string directory,
        ShareXModCaptureRecipe recipe,
        ShareXModChromeTarget target,
        List<Part> parts,
        List<GrowthPass> growthPasses,
        double startY,
        double capturedEndY,
        double lastKnownDocumentEndY,
        int steps,
        int unchangedPasses,
        bool stoppedByUser,
        bool truncated,
        string stopReason,
        string? finalPng,
        int sourceWidth,
        long sourceHeight,
        long elapsedMs)
    {
        string path = Path.Combine(directory, "dynamic-feed-run.json");
        string json = JsonSerializer.Serialize(new
        {
            format = "ShareX-Mod Dynamic Feed Capture Recipe Run",
            version = "0.6.2-dev",
            sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
            created = DateTimeOffset.Now,
            recipeVersion = recipe.Version,
            target = new { target.Title, target.Url },
            startY,
            capturedEndY,
            lastKnownDocumentEndY,
            elapsedMs,
            steps,
            unchangedPasses,
            stoppedByUser,
            truncated,
            stopReason,
            sourceWidth,
            sourceHeight,
            finalPng = finalPng == null ? null : Path.GetFileName(finalPng),
            partCount = parts.Count,
            growthPassCount = growthPasses.Count,
            growthPasses,
            parts
        }, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
    }

    private static string ResolveDirectory(ShareXModV04Settings settings)
    {
        string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Path.Combine(root, "capture-recipe-dynamic-feed");
        }

        string configured = string.IsNullOrWhiteSpace(settings.CaptureRecipeOutputDirectory)
            ? "ShareX-Mod\\CaptureRecipeRuns"
            : settings.CaptureRecipeOutputDirectory;

        string baseDirectory = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));

        Directory.CreateDirectory(baseDirectory);
        string directory = Path.Combine(
            baseDirectory,
            $"feed-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
