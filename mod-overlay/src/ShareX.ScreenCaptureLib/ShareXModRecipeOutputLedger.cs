#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipeOutputLedger
{
    private sealed record OutputRange(
        int Index,
        string Kind,
        string File,
        long OutputStartY,
        long OutputEndY,
        int Width,
        int Height,
        int? RecipeStep,
        int? PageNumber,
        string PageKey,
        double? DocumentStartCss,
        double? DocumentEndCss,
        string[] Evidence);

    private sealed record CoverageCheck(
        int Step,
        int? PageNumber,
        string PageKey,
        double RequestedStartCss,
        double RequestedEndCss,
        double CapturedStartCss,
        double CapturedEndCss,
        double MissingCss,
        bool Passed,
        string[] Reasons);

    public static string? TryWrite(
        ShareXModChromeBackgroundCaptureResult result,
        ShareXModV04Settings settings)
    {
        if (result == null || !Directory.Exists(result.DirectoryPath))
        {
            return null;
        }

        try
        {
            ShareXModCaptureRecipe? recipe = LoadRecipe(settings.CaptureRecipeReplayPath);
            List<OutputRange> ranges = new();
            List<CoverageCheck> coverage = new();
            long outputY = 0;
            bool runFailed = false;
            bool runTruncated = result.TruncatedBySafetyLimit;
            string stopReason = result.StoppedByUser ? "manual-stop" : "unknown";
            string manifestFormat = string.Empty;

            if (File.Exists(result.ManifestPath))
            {
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
                JsonElement root = manifest.RootElement;

                manifestFormat = root.TryGetProperty("format", out JsonElement formatElement) &&
                                 formatElement.ValueKind == JsonValueKind.String
                    ? formatElement.GetString() ?? string.Empty
                    : string.Empty;

                if (root.TryGetProperty("failed", out JsonElement failed) &&
                    failed.ValueKind == JsonValueKind.True)
                {
                    runFailed = true;
                }

                if (root.TryGetProperty("truncated", out JsonElement truncated) &&
                    truncated.ValueKind == JsonValueKind.True)
                {
                    runTruncated = true;
                }

                if (root.TryGetProperty("stopReason", out JsonElement reason) &&
                    reason.ValueKind == JsonValueKind.String)
                {
                    stopReason = reason.GetString() ?? stopReason;
                }

                if (root.TryGetProperty("parts", out JsonElement parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    int partIndex = 0;
                    foreach (JsonElement item in parts.EnumerateArray())
                    {
                        string file = GetString(item, "File", "file");
                        int width = GetInt(item, "Width", "width");
                        int height = GetInt(item, "Height", "height");
                        if (file.Length == 0 || width <= 0 || height <= 0)
                        {
                            continue;
                        }

                        int? step =
                            TryGetInt(item, "Step", "step") ??
                            TryGetInt(item, "TemplateStep", "templateStep");
                        int? pageNumber = TryGetInt(item, "PageNumber", "pageNumber");
                        string pageKey = GetString(item, "PageKey", "pageKey");
                        double? startCss = TryGetDouble(item, "StartY", "startY");
                        double? endCss = TryGetDouble(item, "EndY", "endY");

                        ranges.Add(new OutputRange(
                            ++partIndex,
                            "vertical-content",
                            file,
                            outputY,
                            outputY + height,
                            width,
                            height,
                            step,
                            pageNumber,
                            pageKey,
                            startCss,
                            endCss,
                            new[] { "recipe-captured-content" }));

                        outputY += height;
                    }
                }
            }

            bool pageLoopManifest =
                manifestFormat.Contains("Page Loop", StringComparison.OrdinalIgnoreCase) ||
                manifestFormat.Contains("Adaptive Template", StringComparison.OrdinalIgnoreCase);

            if (pageLoopManifest)
            {
                BuildPageLoopCoverage(ranges, coverage);
            }
            else if (recipe != null)
            {
                BuildFiniteRecipeCoverage(recipe, ranges, coverage);
            }

            AppendTailPages(
                Path.Combine(result.DirectoryPath, "horizontal-appendix"),
                "horizontal_tail_*.png",
                "horizontal-appendix",
                ranges,
                ref outputY);

            AppendTailPages(
                Path.Combine(result.DirectoryPath, "image-appendix-tail"),
                "image_tail_*.png",
                "image-appendix",
                ranges,
                ref outputY);

            string finalPng = Path.Combine(result.DirectoryPath, "capture-full.png");
            (int pngWidth, int pngHeight) = TryReadPngDimensions(finalPng);

            bool finalExists = File.Exists(finalPng);
            bool dimensionsPass =
                finalExists &&
                pngWidth == result.SourceWidth &&
                pngHeight > 0 &&
                Math.Abs((long)pngHeight - outputY) <= 2;

            int failedCoverage = coverage.Count(x => !x.Passed);
            bool coveragePass = failedCoverage == 0;

            string status =
                runFailed ? "failed" :
                !coveragePass ? "coverage-gap" :
                !dimensionsPass ? "output-dimension-mismatch" :
                runTruncated ? "bounded-stop" :
                "complete";

            string confidence = status switch
            {
                "complete" => "high",
                "bounded-stop" when result.StoppedByUser ||
                                    stopReason.Contains("no-new-content", StringComparison.OrdinalIgnoreCase) => "high",
                "bounded-stop" => "medium",
                _ => "low"
            };

            string directory = Path.Combine(result.DirectoryPath, "recipe-quality");
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("recipe-position-ledger", directory);

            string ledgerPath = Path.Combine(directory, "recipe-position-ledger.json");
            File.WriteAllText(
                ledgerPath,
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Recipe Position Ledger",
                    version = "current-integration",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    sourceRecipe = settings.CaptureRecipeReplayPath,
                    sourceManifestFormat = manifestFormat,
                    coverageModel = pageLoopManifest ? "page-template" : "finite-recipe",
                    finalOutput = new
                    {
                        expectedWidth = result.SourceWidth,
                        reportedHeight = result.SourceHeight,
                        ledgerHeight = outputY,
                        pngWidth,
                        pngHeight,
                        finalPngExists = finalExists,
                        dimensionsPass
                    },
                    rangeCount = ranges.Count,
                    ranges,
                    coverage
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            string summaryPath = Path.Combine(directory, "recipe-quality-summary.json");
            File.WriteAllText(
                summaryPath,
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Recipe Capture Quality Summary",
                    version = "current-integration",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    status,
                    confidence,
                    stopReason,
                    stoppedByUser = result.StoppedByUser,
                    truncatedBySafetyLimit = runTruncated,
                    runFailed,
                    coverageModel = pageLoopManifest ? "page-template" : "finite-recipe",
                    requestedVerticalRangeCount = coverage.Count,
                    failedCoverageRangeCount = failedCoverage,
                    outputRangeCount = ranges.Count,
                    finalPngExists = finalExists,
                    dimensionsPass,
                    ledgerPath = Path.GetFileName(ledgerPath)
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(directory, "recipe-quality-summary.txt"),
                $"ShareX-Mod Recipe Quality\nStatus: {status}\nConfidence: {confidence}\nStop: {stopReason}\nCoverage model: {(pageLoopManifest ? "page-template" : "finite-recipe")}\nRequested ranges: {coverage.Count}\nCoverage failures: {failedCoverage}\nOutput ranges: {ranges.Count}\nFinal PNG dimensions: {pngWidth}x{pngHeight}\nLedger height: {outputY}\n",
                new UTF8Encoding(false));

            return ledgerPath;
        }
        catch
        {
            return null;
        }
    }

    private static void BuildFiniteRecipeCoverage(
        ShareXModCaptureRecipe recipe,
        IReadOnlyList<OutputRange> ranges,
        List<CoverageCheck> coverage)
    {
        foreach (ShareXModCaptureRecipeStep step in recipe.Steps.Where(
                     x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange))
        {
            List<OutputRange> captured = ranges
                .Where(x => x.Kind == "vertical-content" &&
                            x.RecipeStep == step.Index &&
                            string.Equals(x.PageKey, step.PageKey, StringComparison.Ordinal))
                .Where(x => x.DocumentStartCss.HasValue && x.DocumentEndCss.HasValue)
                .OrderBy(x => x.DocumentStartCss)
                .ToList();

            if (captured.Count == 0)
            {
                coverage.Add(new CoverageCheck(
                    step.Index,
                    null,
                    step.PageKey,
                    step.StartY,
                    step.EndY,
                    0,
                    0,
                    Math.Max(0, step.EndY - step.StartY),
                    false,
                    new[] { "requested-range-has-no-output-parts" }));
                continue;
            }

            CoverageCheck check = BuildCoverageCheck(
                step.Index,
                null,
                step.PageKey,
                step.StartY,
                step.EndY,
                captured,
                checkRequestedEdges: true);
            coverage.Add(check);
        }
    }

    private static void BuildPageLoopCoverage(
        IReadOnlyList<OutputRange> ranges,
        List<CoverageCheck> coverage)
    {
        IEnumerable<IGrouping<(int PageNumber, int Step, string PageKey), OutputRange>> groups =
            ranges
                .Where(x => x.Kind == "vertical-content" &&
                            x.PageNumber.HasValue &&
                            x.RecipeStep.HasValue &&
                            x.DocumentStartCss.HasValue &&
                            x.DocumentEndCss.HasValue)
                .GroupBy(x => (
                    x.PageNumber!.Value,
                    x.RecipeStep!.Value,
                    x.PageKey));

        foreach (IGrouping<(int PageNumber, int Step, string PageKey), OutputRange> group in groups)
        {
            List<OutputRange> captured = group
                .OrderBy(x => x.DocumentStartCss)
                .ThenBy(x => x.DocumentEndCss)
                .ToList();

            if (captured.Count == 0)
            {
                continue;
            }

            double start = captured.Min(x => x.DocumentStartCss!.Value);
            double end = captured.Max(x => x.DocumentEndCss!.Value);

            coverage.Add(BuildCoverageCheck(
                group.Key.Step,
                group.Key.PageNumber,
                group.Key.PageKey,
                start,
                end,
                captured,
                checkRequestedEdges: false));
        }
    }

    private static CoverageCheck BuildCoverageCheck(
        int step,
        int? pageNumber,
        string pageKey,
        double requestedStart,
        double requestedEnd,
        IReadOnlyList<OutputRange> captured,
        bool checkRequestedEdges)
    {
        double coveredStart = captured.Min(x => x.DocumentStartCss!.Value);
        double coveredEnd = captured.Max(x => x.DocumentEndCss!.Value);
        double cursor = coveredStart;
        double gap = 0;

        foreach (OutputRange part in captured.OrderBy(x => x.DocumentStartCss))
        {
            double start = part.DocumentStartCss!.Value;
            double end = part.DocumentEndCss!.Value;
            if (start > cursor + 2)
            {
                gap += start - cursor;
            }
            cursor = Math.Max(cursor, end);
        }

        double edgeMissing = checkRequestedEdges
            ? Math.Max(0, coveredStart - requestedStart) +
              Math.Max(0, requestedEnd - coveredEnd)
            : 0;
        double missing = gap + edgeMissing;

        List<string> reasons = new();
        if (edgeMissing > 2) reasons.Add("requested-range-edge-missing");
        if (gap > 2) reasons.Add("gap-between-output-parts");

        return new CoverageCheck(
            step,
            pageNumber,
            pageKey,
            requestedStart,
            requestedEnd,
            coveredStart,
            coveredEnd,
            missing,
            missing <= 2,
            reasons.ToArray());
    }

    private static ShareXModCaptureRecipe? LoadRecipe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
            return JsonSerializer.Deserialize<ShareXModCaptureRecipe>(File.ReadAllText(path), options);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendTailPages(
        string directory,
        string pattern,
        string kind,
        List<OutputRange> ranges,
        ref long outputY)
    {
        if (!Directory.Exists(directory)) return;

        foreach (string path in Directory.GetFiles(directory, pattern)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                (int width, int height) = TryReadPngDimensions(path);
                if (width <= 0 || height <= 0) continue;

                ranges.Add(new OutputRange(
                    ranges.Count + 1,
                    kind,
                    Path.GetRelativePath(Path.GetDirectoryName(directory)!, path),
                    outputY,
                    outputY + height,
                    width,
                    height,
                    null,
                    null,
                    string.Empty,
                    null,
                    null,
                    new[] { "appendix-tail-page" }));
                outputY += height;
            }
            catch
            {
            }
        }
    }

    private static (int width, int height) TryReadPngDimensions(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != header.Length) return (0, 0);

            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++)
            {
                if (header[i] != signature[i]) return (0, 0);
            }

            int width =
                (header[16] << 24) |
                (header[17] << 16) |
                (header[18] << 8) |
                header[19];
            int height =
                (header[20] << 24) |
                (header[21] << 16) |
                (header[22] << 8) |
                header[23];
            return (width, height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string GetString(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a) && a.ValueKind == JsonValueKind.String)
            return a.GetString() ?? string.Empty;
        return item.TryGetProperty(second, out JsonElement b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a) && a.TryGetInt32(out int value)) return value;
        if (item.TryGetProperty(second, out JsonElement b) && b.TryGetInt32(out value)) return value;
        return 0;
    }

    private static int? TryGetInt(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a) && a.TryGetInt32(out int value)) return value;
        if (item.TryGetProperty(second, out JsonElement b) && b.TryGetInt32(out value)) return value;
        return null;
    }

    private static double? TryGetDouble(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a) && a.TryGetDouble(out double value)) return value;
        if (item.TryGetProperty(second, out JsonElement b) && b.TryGetDouble(out value)) return value;
        return null;
    }
}
