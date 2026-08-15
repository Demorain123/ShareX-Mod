#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModSemanticRepairAugmenter
{
    private sealed record Range(long StartY, long EndY, string[] Reasons);
    private sealed record Anchor(string Fingerprint, string Tag, double Y, double Height);

    public static async Task<string?> TryAugmentAsync(
        ShareXModV04Settings settings,
        string? startSemanticMapPath,
        string? endSemanticMapPath,
        string? layoutShiftPath,
        ShareXModBrowserCoordinateCalibration? calibration)
    {
        if (!settings.ChromeSemanticRepairAugmentEnabled || calibration == null)
        {
            return null;
        }

        try
        {
            string? repairDirectory =
                ShareXModCaptureSessionContext.TryGetComponentDirectory("repair");

            if (string.IsNullOrWhiteSpace(repairDirectory))
            {
                return null;
            }

            string repairPlanPath = Path.Combine(repairDirectory, "repair-plan.json");
            if (!File.Exists(repairPlanPath))
            {
                return null;
            }

            using JsonDocument plan = JsonDocument.Parse(await File.ReadAllTextAsync(repairPlanPath));
            JsonElement planRoot = plan.RootElement;

            long logicalHeight =
                planRoot.TryGetProperty("logicalCapturedHeight", out JsonElement logicalHeightElement) &&
                logicalHeightElement.TryGetInt64(out long parsedHeight)
                    ? parsedHeight
                    : 0;

            int margin =
                planRoot.TryGetProperty("marginPx", out JsonElement marginElement) &&
                marginElement.TryGetInt32(out int parsedMargin)
                    ? parsedMargin
                    : Math.Clamp(settings.RepairPlanMarginPx, 0, 20000);

            int mergeGap =
                planRoot.TryGetProperty("mergeGapPx", out JsonElement gapElement) &&
                gapElement.TryGetInt32(out int parsedGap)
                    ? parsedGap
                    : Math.Clamp(settings.RepairPlanMergeGapPx, 0, 10000);

            List<Range> existing = ReadPlanRanges(planRoot);
            List<Range> semanticRaw = new();

            if (settings.ChromeSemanticRepairIncludeLayoutShifts &&
                !string.IsNullOrWhiteSpace(layoutShiftPath) &&
                File.Exists(layoutShiftPath))
            {
                semanticRaw.AddRange(ReadLayoutShiftRanges(
                    layoutShiftPath,
                    calibration,
                    logicalHeight,
                    settings));
            }

            if (settings.ChromeSemanticRepairIncludeAnchorMovement &&
                !string.IsNullOrWhiteSpace(startSemanticMapPath) &&
                !string.IsNullOrWhiteSpace(endSemanticMapPath) &&
                File.Exists(startSemanticMapPath) &&
                File.Exists(endSemanticMapPath))
            {
                semanticRaw.AddRange(ReadSemanticMovementRanges(
                    startSemanticMapPath,
                    endSemanticMapPath,
                    calibration,
                    logicalHeight,
                    settings));
            }

            List<Range> semanticMerged = MergeRanges(
                semanticRaw
                    .Select(x => ExpandAndClamp(x, margin, logicalHeight))
                    .Where(x => x.EndY > x.StartY),
                mergeGap);

            int maxAdditional = Math.Clamp(
                settings.ChromeSemanticRepairMaxAdditionalRanges,
                1,
                512);

            if (semanticMerged.Count > maxAdditional)
            {
                semanticMerged = semanticMerged
                    .OrderByDescending(x => x.EndY - x.StartY)
                    .Take(maxAdditional)
                    .OrderBy(x => x.StartY)
                    .ToList();
            }

            List<Range> combined = MergeRanges(
                existing.Concat(semanticMerged),
                mergeGap);

            string evidenceDirectory = ResolveEvidenceDirectory();
            Directory.CreateDirectory(evidenceDirectory);
            ShareXModCaptureSessionContext.RegisterComponent("semantic-repair", evidenceDirectory);

            string evidencePath = Path.Combine(evidenceDirectory, "semantic-repair-evidence.json");
            string evidence = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Semantic Repair Evidence",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                configured = new
                {
                    settings.ChromeSemanticRepairIncludeLayoutShifts,
                    settings.ChromeSemanticRepairIncludeAnchorMovement,
                    settings.ChromeSemanticRepairMinShiftCss,
                    settings.ChromeSemanticRepairIgnoreRecentInputLayoutShifts,
                    settings.ChromeSemanticRepairMaxAdditionalRanges
                },
                calibration,
                originalPlanRangeCount = existing.Count,
                rawSemanticRangeCount = semanticRaw.Count,
                semanticRangeCount = semanticMerged.Count,
                finalPlanRangeCount = combined.Count,
                semanticRanges = semanticMerged
            }, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(evidencePath, evidence, new UTF8Encoding(false));

            string updatedPlan = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Local Repair Plan",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                status = combined.Count == 0 ? "clean" : "repair-recommended",
                logicalCapturedHeight = logicalHeight,
                marginPx = margin,
                mergeGapPx = mergeGap,
                originalPixelGuardRangeCount = existing.Count,
                semanticAdditionalRangeCount = semanticMerged.Count,
                repairRangeCount = combined.Count,
                preferredRepairMethod = "browser-exact-coordinate-recapture-with-alignment-gate",
                ranges = combined
            }, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(repairPlanPath, updatedPlan, new UTF8Encoding(false));
            return evidencePath;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Range> ReadLayoutShiftRanges(
        string path,
        ShareXModBrowserCoordinateCalibration calibration,
        long logicalHeight,
        ShareXModV04Settings settings)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("evidence", out JsonElement evidence) ||
            evidence.ValueKind != JsonValueKind.Object ||
            !evidence.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            bool hadRecentInput =
                entry.TryGetProperty("hadRecentInput", out JsonElement recent) &&
                recent.ValueKind == JsonValueKind.True;

            if (settings.ChromeSemanticRepairIgnoreRecentInputLayoutShifts && hadRecentInput)
            {
                continue;
            }

            if (!entry.TryGetProperty("sources", out JsonElement sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement source in sources.EnumerateArray())
            {
                if (!TryRect(source, "previousRect", out double py, out double ph) ||
                    !TryRect(source, "currentRect", out double cy, out double ch))
                {
                    continue;
                }

                double cssStart = Math.Min(py, cy);
                double cssEnd = Math.Max(py + ph, cy + ch);
                Range? mapped = MapDocumentCssToLogical(
                    cssStart,
                    cssEnd,
                    calibration,
                    logicalHeight,
                    "layout-shift");

                if (mapped != null)
                {
                    yield return mapped;
                }
            }
        }
    }

    private static IEnumerable<Range> ReadSemanticMovementRanges(
        string startPath,
        string endPath,
        ShareXModBrowserCoordinateCalibration calibration,
        long logicalHeight,
        ShareXModV04Settings settings)
    {
        Dictionary<string, Anchor> start = ReadAnchors(startPath);
        Dictionary<string, Anchor> end = ReadAnchors(endPath);
        double thresholdCss = Math.Clamp(settings.ChromeSemanticRepairMinShiftCss, 1, 1000);

        foreach ((string fingerprint, Anchor before) in start)
        {
            if (!end.TryGetValue(fingerprint, out Anchor? after))
            {
                continue;
            }

            double yShift = Math.Abs(after.Y - before.Y);
            double heightShift = Math.Abs(after.Height - before.Height);
            if (yShift < thresholdCss && heightShift < thresholdCss)
            {
                continue;
            }

            double cssStart = Math.Min(before.Y, after.Y);
            double cssEnd = Math.Max(before.Y + before.Height, after.Y + after.Height);
            Range? mapped = MapDocumentCssToLogical(
                cssStart,
                cssEnd,
                calibration,
                logicalHeight,
                "semantic-anchor-moved:" + before.Tag.ToLowerInvariant());

            if (mapped != null)
            {
                yield return mapped;
            }
        }
    }

    private static Dictionary<string, Anchor> ReadAnchors(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, Anchor> result = new(StringComparer.Ordinal);

        if (!document.RootElement.TryGetProperty("anchors", out JsonElement anchors) ||
            anchors.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in anchors.EnumerateArray())
        {
            string fingerprint = GetString(item, "Fingerprint", "fingerprint");
            string tag = GetString(item, "Tag", "tag");
            if (fingerprint.Length == 0 ||
                !TryDouble(item, "Y", "y", out double y) ||
                !TryDouble(item, "Height", "height", out double height))
            {
                continue;
            }

            if (!result.ContainsKey(fingerprint))
            {
                result[fingerprint] = new Anchor(fingerprint, tag, y, height);
            }
        }

        return result;
    }

    private static Range? MapDocumentCssToLogical(
        double cssStart,
        double cssEnd,
        ShareXModBrowserCoordinateCalibration calibration,
        long logicalHeight,
        string reason)
    {
        double dpr = Math.Max(0.1, calibration.DevicePixelRatio);
        long start = (long)Math.Floor((cssStart - calibration.SelectedTopDocumentCss) * dpr);
        long end = (long)Math.Ceiling((cssEnd - calibration.SelectedTopDocumentCss) * dpr);

        if (logicalHeight > 0)
        {
            start = Math.Clamp(start, 0, logicalHeight);
            end = Math.Clamp(end, 0, logicalHeight);
        }
        else
        {
            start = Math.Max(0, start);
            end = Math.Max(0, end);
        }

        return end > start ? new Range(start, end, new[] { reason }) : null;
    }

    private static List<Range> ReadPlanRanges(JsonElement root)
    {
        List<Range> result = new();
        if (!root.TryGetProperty("ranges", out JsonElement ranges) || ranges.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in ranges.EnumerateArray())
        {
            if (TryLong(item, "StartY", "startY", out long start) &&
                TryLong(item, "EndY", "endY", out long end) &&
                end > start)
            {
                result.Add(new Range(start, end, ReadReasons(item)));
            }
        }

        return result;
    }

    private static Range ExpandAndClamp(Range range, int margin, long logicalHeight)
    {
        long start = Math.Max(0, range.StartY - margin);
        long end = range.EndY + margin;
        if (logicalHeight > 0) end = Math.Min(logicalHeight, end);
        return new Range(start, end, range.Reasons);
    }

    private static List<Range> MergeRanges(IEnumerable<Range> source, int mergeGap)
    {
        List<Range> ordered = source
            .Where(x => x.EndY > x.StartY)
            .OrderBy(x => x.StartY)
            .ThenBy(x => x.EndY)
            .ToList();
        List<Range> merged = new();

        foreach (Range range in ordered)
        {
            if (merged.Count == 0 || range.StartY > merged[^1].EndY + mergeGap)
            {
                merged.Add(range);
                continue;
            }

            Range previous = merged[^1];
            merged[^1] = new Range(
                previous.StartY,
                Math.Max(previous.EndY, range.EndY),
                previous.Reasons.Concat(range.Reasons).Distinct(StringComparer.Ordinal).ToArray());
        }

        return merged;
    }

    private static bool TryRect(JsonElement source, string name, out double documentY, out double height)
    {
        documentY = 0;
        height = 0;
        if (!source.TryGetProperty(name, out JsonElement rect) || rect.ValueKind != JsonValueKind.Object)
            return false;

        return rect.TryGetProperty("documentY", out JsonElement dy) &&
               dy.TryGetDouble(out documentY) &&
               rect.TryGetProperty("height", out JsonElement h) &&
               h.TryGetDouble(out height);
    }

    private static bool TryLong(JsonElement item, string first, string second, out long value)
    {
        value = 0;
        return (item.TryGetProperty(first, out JsonElement a) && a.TryGetInt64(out value)) ||
               (item.TryGetProperty(second, out JsonElement b) && b.TryGetInt64(out value));
    }

    private static bool TryDouble(JsonElement item, string first, string second, out double value)
    {
        value = 0;
        return (item.TryGetProperty(first, out JsonElement a) && a.TryGetDouble(out value)) ||
               (item.TryGetProperty(second, out JsonElement b) && b.TryGetDouble(out value));
    }

    private static string GetString(JsonElement item, string first, string second)
    {
        if (item.TryGetProperty(first, out JsonElement a)) return a.GetString() ?? string.Empty;
        return item.TryGetProperty(second, out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
    }

    private static string[] ReadReasons(JsonElement item)
    {
        JsonElement value;
        if (!item.TryGetProperty("Reasons", out value) && !item.TryGetProperty("reasons", out value))
            return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveEvidenceDirectory()
    {
        string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (!string.IsNullOrWhiteSpace(root)) return Path.Combine(root, "semantic-repair");
        return Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "SemanticRepairEvidence",
            $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
    }
}
