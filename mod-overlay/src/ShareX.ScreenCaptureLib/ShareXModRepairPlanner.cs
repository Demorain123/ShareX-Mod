#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRepairPlanner
{
    private sealed record Range(long StartY, long EndY, string[] Reasons);

    public static void TryWriteLatestPlan(ShareXModV04Settings settings)
    {
        if (!settings.RepairPlanEnabled) return;

        try
        {
            string? captureMap = FindLatestCaptureMap();
            if (captureMap == null) return;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(captureMap));
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("final", out JsonElement finalElement) ||
                finalElement.ValueKind != JsonValueKind.True)
            {
                return;
            }

            long logicalHeight = root.TryGetProperty("logicalCapturedHeight", out JsonElement heightElement) &&
                                 heightElement.TryGetInt64(out long parsedHeight)
                ? parsedHeight
                : 0;

            List<Range> raw = new();

            if (root.TryGetProperty("suspectRanges", out JsonElement rangesElement) &&
                rangesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in rangesElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("StartY", out JsonElement startElement) ||
                        !item.TryGetProperty("EndY", out JsonElement endElement) ||
                        !startElement.TryGetInt64(out long start) ||
                        !endElement.TryGetInt64(out long end))
                    {
                        continue;
                    }

                    string[] reasons = Array.Empty<string>();
                    if (item.TryGetProperty("Reasons", out JsonElement reasonsElement) &&
                        reasonsElement.ValueKind == JsonValueKind.Array)
                    {
                        reasons = reasonsElement
                            .EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString() ?? string.Empty)
                            .Where(x => x.Length > 0)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                    }

                    raw.Add(new Range(start, end, reasons));
                }
            }

            int margin = Math.Clamp(settings.RepairPlanMarginPx, 0, 20000);
            int mergeGap = Math.Clamp(settings.RepairPlanMergeGapPx, 0, 10000);

            List<Range> expanded = raw
                .Select(x => new Range(
                    Math.Max(0, x.StartY - margin),
                    logicalHeight > 0
                        ? Math.Min(logicalHeight, x.EndY + margin)
                        : x.EndY + margin,
                    x.Reasons))
                .Where(x => x.EndY > x.StartY)
                .OrderBy(x => x.StartY)
                .ThenBy(x => x.EndY)
                .ToList();

            List<Range> merged = new();

            foreach (Range range in expanded)
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
                    previous.Reasons
                        .Concat(range.Reasons)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            }

            string output = Path.Combine(
                Path.GetDirectoryName(captureMap)!,
                "repair-plan.json");

            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Local Repair Plan",
                version = "0.4.3-dev",
                created = DateTimeOffset.Now,
                sourceCaptureMap = Path.GetFileName(captureMap),
                status = merged.Count == 0 ? "clean" : "repair-recommended",
                logicalCapturedHeight = logicalHeight,
                marginPx = margin,
                mergeGapPx = mergeGap,
                repairRangeCount = merged.Count,
                preferredFutureRepairMethod = "browser-exact-coordinate-recapture",
                fallbackRepairMethod = "manual-local-recapture",
                ranges = merged
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(output, json, new UTF8Encoding(false));
        }
        catch
        {
            // A repair plan is advisory and must never invalidate the main capture.
        }
    }

    private static string? FindLatestCaptureMap()
    {
        int pid = Environment.ProcessId;
        IEnumerable<string> roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ShareX-Mod", "CaptureQualitySessions"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX-Mod",
                "CaptureQualitySessions")
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root =>
            {
                try
                {
                    return Directory.GetDirectories(root, $"*-p{pid}");
                }
                catch
                {
                    return Array.Empty<string>();
                }
            })
            .Select(directory => new
            {
                Directory = directory,
                Map = Path.Combine(directory, "capture-map.json")
            })
            .Where(x => File.Exists(x.Map))
            .OrderByDescending(x =>
            {
                try { return File.GetLastWriteTimeUtc(x.Map); }
                catch { return DateTime.MinValue; }
            })
            .Select(x => x.Map)
            .FirstOrDefault();
    }
}
