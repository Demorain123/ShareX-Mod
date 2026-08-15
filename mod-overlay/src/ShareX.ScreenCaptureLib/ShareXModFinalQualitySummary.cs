#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModFinalQualitySummary
{
    private sealed record Range(long StartY, long EndY);
    private sealed record AppliedRange(long StartY, long EndY, bool Verified, bool Applied);

    public static void TryWrite(
        ShareXModV04Settings settings,
        Bitmap? result)
    {
        if (!settings.FinalQualitySummaryEnabled)
        {
            return;
        }

        try
        {
            string? sessionRoot =
                ShareXModCaptureSessionContext.CurrentRootDirectory;

            if (string.IsNullOrWhiteSpace(sessionRoot))
            {
                return;
            }

            string directory =
                Path.Combine(sessionRoot, "quality-summary");

            Directory.CreateDirectory(directory);

            ShareXModCaptureSessionContext.RegisterComponent(
                "quality-summary",
                directory);

            int baseIntegrityScore = 100;
            string baseRating = "unknown";
            int pixelGuardSuspectCount = 0;

            string? qualityDirectory =
                ShareXModCaptureSessionContext.TryGetComponentDirectory("quality");

            if (!string.IsNullOrWhiteSpace(qualityDirectory))
            {
                string map =
                    Path.Combine(qualityDirectory, "capture-map.json");

                if (File.Exists(map))
                {
                    using JsonDocument document =
                        JsonDocument.Parse(File.ReadAllText(map));

                    JsonElement root =
                        document.RootElement;

                    if (root.TryGetProperty("integrityScore", out JsonElement score) &&
                        score.TryGetInt32(out int parsedScore))
                    {
                        baseIntegrityScore = parsedScore;
                    }

                    if (root.TryGetProperty("rating", out JsonElement rating))
                    {
                        baseRating =
                            rating.GetString() ?? "unknown";
                    }

                    if (root.TryGetProperty("suspectRangeCount", out JsonElement count) &&
                        count.TryGetInt32(out int parsedCount))
                    {
                        pixelGuardSuspectCount = parsedCount;
                    }
                }
            }

            List<Range> planRanges =
                ReadRepairPlanRanges();

            List<AppliedRange> aligned =
                ReadAlignmentRanges();

            int resolved = 0;
            int verifiedButNotApplied = 0;

            foreach (Range range in planRanges)
            {
                double appliedCoverage =
                    Coverage(
                        range,
                        aligned.Where(x => x.Applied));

                double verifiedCoverage =
                    Coverage(
                        range,
                        aligned.Where(x => x.Verified));

                if (appliedCoverage >=
                    Math.Clamp(settings.FinalQualityResolvedCoverageRatio, 0.25, 1))
                {
                    resolved++;
                }
                else if (verifiedCoverage >=
                         Math.Clamp(settings.FinalQualityResolvedCoverageRatio, 0.25, 1))
                {
                    verifiedButNotApplied++;
                }
            }

            int unresolved =
                Math.Max(
                    0,
                    planRanges.Count - resolved);

            string status;
            string confidence;

            if (planRanges.Count == 0)
            {
                status = "clean";
                confidence =
                    baseIntegrityScore >= 90
                        ? "high"
                        : baseIntegrityScore >= 75
                            ? "medium"
                            : "low";
            }
            else if (unresolved == 0)
            {
                status = "repaired";
                confidence = "high";
            }
            else if (resolved > 0 ||
                     verifiedButNotApplied > 0)
            {
                status = "partially-repaired";
                confidence = "medium";
            }
            else
            {
                status = "unresolved";
                confidence = "low";
            }

            int semanticRangeCount = 0;
            string? semanticDirectory =
                ShareXModCaptureSessionContext.TryGetComponentDirectory("semantic-repair");

            if (!string.IsNullOrWhiteSpace(semanticDirectory))
            {
                string evidence =
                    Path.Combine(
                        semanticDirectory,
                        "semantic-repair-evidence.json");

                if (File.Exists(evidence))
                {
                    using JsonDocument document =
                        JsonDocument.Parse(File.ReadAllText(evidence));

                    if (document.RootElement.TryGetProperty(
                            "semanticRangeCount",
                            out JsonElement semanticCount) &&
                        semanticCount.TryGetInt32(out int parsed))
                    {
                        semanticRangeCount = parsed;
                    }
                }
            }

            int verifiedCandidates =
                aligned.Count(x => x.Verified);

            int appliedCandidates =
                aligned.Count(x => x.Applied);

            string jsonPath =
                Path.Combine(
                    directory,
                    "quality-summary.json");

            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Final Capture Quality Summary",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                status,
                confidence,
                originalCaptureEvidence = new
                {
                    baseIntegrityScore,
                    baseRating,
                    pixelGuardSuspectCount
                },
                semanticEvidence = new
                {
                    semanticAdditionalRangeCount = semanticRangeCount
                },
                repair = new
                {
                    plannedRangeCount = planRanges.Count,
                    resolvedRangeCount = resolved,
                    unresolvedRangeCount = unresolved,
                    verifiedButNotAppliedRangeCount = verifiedButNotApplied,
                    verifiedCandidateCount = verifiedCandidates,
                    appliedCandidateCount = appliedCandidates
                },
                result = new
                {
                    width = result?.Width ?? 0,
                    height = result?.Height ?? 0,
                    segmented =
                        ShareXModSegmentedOutputRegistry.TryGet(
                            result,
                            out _,
                            out _,
                            out _,
                            out _)
                },
                interpretation = status switch
                {
                    "clean" =>
                        "No repair range remained after pixel and semantic checks.",
                    "repaired" =>
                        "Every planned repair range received sufficient verified automatic repair coverage.",
                    "partially-repaired" =>
                        "Some ranges were repaired or verified, but at least one range remains unresolved.",
                    _ =>
                        "One or more quality-guard ranges remain unresolved; the original pixels were preserved where verification failed."
                }
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(
                jsonPath,
                json,
                new UTF8Encoding(false));

            string textPath =
                Path.Combine(
                    directory,
                    "quality-summary.txt");

            File.WriteAllText(
                textPath,
                BuildText(
                    status,
                    confidence,
                    baseIntegrityScore,
                    planRanges.Count,
                    resolved,
                    unresolved,
                    semanticRangeCount,
                    verifiedCandidates,
                    appliedCandidates),
                new UTF8Encoding(false));
        }
        catch
        {
            // Summary is advisory and must never invalidate the capture.
        }
    }

    private static List<Range> ReadRepairPlanRanges()
    {
        string? repairDirectory =
            ShareXModCaptureSessionContext.TryGetComponentDirectory("repair");

        if (string.IsNullOrWhiteSpace(repairDirectory))
        {
            return new List<Range>();
        }

        string path =
            Path.Combine(
                repairDirectory,
                "repair-plan.json");

        if (!File.Exists(path))
        {
            return new List<Range>();
        }

        using JsonDocument document =
            JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty(
                "ranges",
                out JsonElement ranges) ||
            ranges.ValueKind != JsonValueKind.Array)
        {
            return new List<Range>();
        }

        List<Range> result = new();

        foreach (JsonElement item in ranges.EnumerateArray())
        {
            if (TryLong(item, "StartY", "startY", out long start) &&
                TryLong(item, "EndY", "endY", out long end) &&
                end > start)
            {
                result.Add(new Range(start, end));
            }
        }

        return result;
    }

    private static List<AppliedRange> ReadAlignmentRanges()
    {
        string? directory =
            ShareXModCaptureSessionContext.TryGetComponentDirectory("repair-alignment");

        if (string.IsNullOrWhiteSpace(directory))
        {
            return new List<AppliedRange>();
        }

        string path =
            Path.Combine(
                directory,
                "repair-alignment.json");

        if (!File.Exists(path))
        {
            return new List<AppliedRange>();
        }

        using JsonDocument document =
            JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty(
                "audit",
                out JsonElement audit) ||
            audit.ValueKind != JsonValueKind.Array)
        {
            return new List<AppliedRange>();
        }

        List<AppliedRange> result = new();

        foreach (JsonElement item in audit.EnumerateArray())
        {
            if (!TryLong(item, "LogicalStartY", "logicalStartY", out long start) ||
                !TryLong(item, "LogicalEndY", "logicalEndY", out long end) ||
                end <= start)
            {
                continue;
            }

            bool verified =
                GetBool(item, "Verified", "verified");

            bool applied =
                GetBool(item, "Applied", "applied");

            result.Add(new AppliedRange(start, end, verified, applied));
        }

        return result;
    }

    private static double Coverage(
        Range target,
        IEnumerable<AppliedRange> candidates)
    {
        List<Range> intersections = candidates
            .Select(x => new Range(
                Math.Max(target.StartY, x.StartY),
                Math.Min(target.EndY, x.EndY)))
            .Where(x => x.EndY > x.StartY)
            .OrderBy(x => x.StartY)
            .ToList();

        if (intersections.Count == 0)
        {
            return 0;
        }

        long covered = 0;
        long cursorStart = intersections[0].StartY;
        long cursorEnd = intersections[0].EndY;

        foreach (Range range in intersections.Skip(1))
        {
            if (range.StartY <= cursorEnd)
            {
                cursorEnd = Math.Max(cursorEnd, range.EndY);
            }
            else
            {
                covered += cursorEnd - cursorStart;
                cursorStart = range.StartY;
                cursorEnd = range.EndY;
            }
        }

        covered += cursorEnd - cursorStart;
        long total = target.EndY - target.StartY;
        return total > 0 ? covered / (double)total : 0;
    }

    private static bool TryLong(
        JsonElement item,
        string first,
        string second,
        out long value)
    {
        value = 0;

        return (item.TryGetProperty(first, out JsonElement a) &&
                a.TryGetInt64(out value)) ||
               (item.TryGetProperty(second, out JsonElement b) &&
                b.TryGetInt64(out value));
    }

    private static bool GetBool(
        JsonElement item,
        string first,
        string second)
    {
        JsonElement value;

        if (!item.TryGetProperty(first, out value) &&
            !item.TryGetProperty(second, out value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static string BuildText(
        string status,
        string confidence,
        int baseScore,
        int planned,
        int resolved,
        int unresolved,
        int semantic,
        int verifiedCandidates,
        int appliedCandidates)
    {
        StringBuilder text = new();

        text.AppendLine("ShareX-Mod Capture Quality");
        text.AppendLine("========================");
        text.AppendLine($"Status: {status}");
        text.AppendLine($"Confidence: {confidence}");
        text.AppendLine($"Original integrity score: {baseScore}");
        text.AppendLine($"Planned repair ranges: {planned}");
        text.AppendLine($"Resolved ranges: {resolved}");
        text.AppendLine($"Unresolved ranges: {unresolved}");
        text.AppendLine($"Semantic/layout additional ranges: {semantic}");
        text.AppendLine($"Verified repair candidates: {verifiedCandidates}");
        text.AppendLine($"Applied repair candidates: {appliedCandidates}");

        return text.ToString();
    }
}
