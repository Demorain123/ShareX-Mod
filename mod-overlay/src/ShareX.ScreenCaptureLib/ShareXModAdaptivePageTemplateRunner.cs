#nullable enable

using System;
using System.Collections.Generic;
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

internal static class ShareXModAdaptivePageTemplateRunner
{
    private sealed record Part(
        string File,
        int PageNumber,
        int TemplateStep,
        string PageKey,
        double StartY,
        double EndY,
        int Width,
        int Height);

    private sealed record Audit(
        int PageNumber,
        int Step,
        string Kind,
        string Status,
        string Detail);

    private sealed record PageExecution(
        bool Success,
        bool Transitioned,
        bool EndReached,
        string StopReason,
        ShareXModRecipePageEvidence Before,
        ShareXModRecipePageEvidence? After);

    public static async Task<ShareXModChromeBackgroundCaptureResult?> TryRunAsync(
        ShareXModChromeCdpClient client,
        ShareXModChromeTarget target,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        if (!settings.CaptureRecipeAdaptiveTemplateEnabled ||
            string.IsNullOrWhiteSpace(settings.CaptureRecipeReplayPath) ||
            !File.Exists(settings.CaptureRecipeReplayPath))
        {
            return null;
        }

        ShareXModCaptureRecipe? recipe = LoadRecipe(settings.CaptureRecipeReplayPath);
        if (recipe == null) return null;

        ShareXModAdaptivePageTemplatePlan plan =
            ShareXModAdaptivePageTemplatePlanner.Build(recipe, settings);

        if (!plan.Candidate)
        {
            return null;
        }

        if (!ShareXModCaptureRecipePageLoopApproval.IsApproved(
                settings.CaptureRecipeReplayPath,
                settings,
                out int approvedMaxPages))
        {
            return null;
        }

        int maxPages = Math.Min(
            Math.Clamp(approvedMaxPages, 2, 10000),
            Math.Clamp(settings.CaptureRecipeMaxNavigationCount + 1, 2, 10001));

        string directory = ResolveDirectory(settings);
        Directory.CreateDirectory(directory);
        ShareXModCaptureSessionContext.RegisterComponent("adaptive-page-template", directory);

        List<Part> parts = new();
        List<Audit> audit = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        ShareXModCaptureRecipePageGuard.Session pageGuard =
            ShareXModCaptureRecipePageGuard.Create(client, settings);

        bool stoppedByUser = false;
        bool truncated = false;
        bool failed = false;
        bool overlayEmitted = false;
        string stopReason = "adaptive-template-complete";
        int pagesCompleted = 0;

        for (int pageNumber = 1; pageNumber <= maxPages; pageNumber++)
        {
            if (shouldStop?.Invoke() == true)
            {
                stoppedByUser = true;
                stopReason = "manual-stop";
                break;
            }

            ShareXModRecipePageEvidence current =
                await ShareXModRecipePageTransitionVerifier.CaptureAsync(client);

            string cycleKey = current.Url + "\u001f" + current.MainFingerprint;
            if (!seen.Add(cycleKey))
            {
                stopReason = "adaptive-template-cycle-detected";
                break;
            }

            IReadOnlyList<ShareXModCaptureRecipeStep> template =
                pageNumber == 1 ? plan.InitialSteps : plan.TemplateSteps;

            PageExecution execution = await ExecutePageAsync(
                client,
                settings,
                directory,
                template,
                pageNumber,
                current,
                parts,
                audit,
                pageGuard,
                includeFirstOverlay: !overlayEmitted,
                allowNextPage: pageNumber < maxPages,
                shouldStop);

            overlayEmitted = overlayEmitted ||
                             parts.Any(x => x.PageNumber == pageNumber);

            if (!execution.Success)
            {
                failed = true;
                stopReason = execution.StopReason;
                break;
            }

            pagesCompleted++;

            if (shouldStop?.Invoke() == true)
            {
                stoppedByUser = true;
                stopReason = "manual-stop";
                break;
            }

            if (execution.EndReached)
            {
                stopReason = execution.StopReason;
                break;
            }

            if (!execution.Transitioned)
            {
                stopReason = execution.StopReason;
                break;
            }

            if (pageNumber >= maxPages)
            {
                truncated = true;
                stopReason = "adaptive-template-max-pages";
                break;
            }
        }

        if (parts.Count == 0)
        {
            WriteManifest(
                directory,
                target,
                plan,
                parts,
                audit,
                pagesCompleted,
                stoppedByUser,
                truncated,
                failed,
                stopReason,
                null,
                0,
                0);
            return null;
        }

        parts = parts
            .OrderBy(x => x.PageNumber)
            .ThenBy(x => x.TemplateStep)
            .ThenBy(x => x.StartY)
            .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int sourceWidth = parts[0].Width;
        if (parts.Any(x => x.Width != sourceWidth))
        {
            failed = true;
            stopReason = "adaptive-template-part-width-mismatch";
        }

        long sourceHeight = parts.Sum(x => (long)x.Height);
        string? finalPng = null;

        if (!failed)
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
                failed = true;
                stopReason = "adaptive-template-final-png-failed";
                finalPng = null;
            }
        }

        string manifestPath = Path.Combine(directory, "adaptive-page-loop-run.json");
        WriteManifest(
            directory,
            target,
            plan,
            parts,
            audit,
            pagesCompleted,
            stoppedByUser,
            truncated,
            failed,
            stopReason,
            finalPng,
            sourceWidth,
            sourceHeight);

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
            TruncatedBySafetyLimit = truncated || failed
        };
    }

    private static async Task<PageExecution> ExecutePageAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string directory,
        IReadOnlyList<ShareXModCaptureRecipeStep> template,
        int pageNumber,
        ShareXModRecipePageEvidence before,
        List<Part> parts,
        List<Audit> audit,
        ShareXModCaptureRecipePageGuard.Session pageGuard,
        bool includeFirstOverlay,
        bool allowNextPage,
        Func<bool>? shouldStop)
    {
        string currentPageKey = ShareXModRecipePageIdentity.FromUrl(before.Url);
        await ShareXModChromeOverlayDeduplicator.PreparePageAsync(client, settings);
        bool firstRange = includeFirstOverlay;

        foreach (ShareXModCaptureRecipeStep templateStep in template.OrderBy(x => x.Index))
        {
            if (shouldStop?.Invoke() == true)
            {
                return new PageExecution(false, false, false, "manual-stop", before, null);
            }

            int syntheticStep = checked(pageNumber * 100000 + templateStep.Index);
            ShareXModCaptureRecipeStep step = templateStep with
            {
                Index = syntheticStep,
                PageKey = currentPageKey
            };

            switch (step.Kind)
            {
                case ShareXModCaptureRecipeStepKind.CaptureVerticalRange:
                {
                    ShareXModCaptureRecipeStep effective =
                        await ShareXModRecipeRangeAnchorResolver.ResolveStepAsync(client, step);

                    ShareXModRecipeExactRangeResult capture =
                        await ShareXModCaptureRecipeExactRange.CaptureAsync(
                            client,
                            settings,
                            effective,
                            directory,
                            $"adaptive_p{pageNumber:D5}_s{templateStep.Index:D4}",
                            includeOverlayOnFirstTile: firstRange,
                            shouldStop);

                    if (!capture.Success)
                    {
                        audit.Add(new Audit(pageNumber, templateStep.Index, "vertical-range", "failed", capture.Detail));
                        return new PageExecution(false, false, false, "adaptive-range-failed:" + capture.Detail, before, null);
                    }

                    foreach (ShareXModRecipeExactPart item in capture.Parts)
                    {
                        parts.Add(new Part(
                            item.File,
                            pageNumber,
                            templateStep.Index,
                            currentPageKey,
                            item.StartY,
                            item.EndY,
                            item.Width,
                            item.Height));
                    }

                    firstRange = false;
                    await pageGuard.RecordAsync(step, effective);
                    audit.Add(new Audit(pageNumber, templateStep.Index, "vertical-range", "captured", $"parts={capture.Parts.Count}"));
                    break;
                }

                case ShareXModCaptureRecipeStepKind.HorizontalSweep:
                {
                    if (step.Locator == null)
                    {
                        if (step.Required)
                        {
                            return new PageExecution(false, false, false, "adaptive-horizontal-missing-locator", before, null);
                        }
                        audit.Add(new Audit(pageNumber, templateStep.Index, "horizontal-sweep", "skipped", "no-locator"));
                        break;
                    }

                    ShareXModHorizontalSweepExecutionResult horizontal =
                        await ShareXModCaptureRecipeHorizontalSweepExecutor.CaptureAsync(
                            client,
                            settings,
                            step.Locator,
                            directory,
                            syntheticStep,
                            shouldStop);

                    audit.Add(new Audit(
                        pageNumber,
                        templateStep.Index,
                        "horizontal-sweep",
                        horizontal.Success ? "captured" : step.Required ? "failed" : "skipped",
                        $"{horizontal.Detail}; panels={horizontal.PanelCount}; score={horizontal.LocatorScore:0.0}; gap={horizontal.LocatorGap:0.0}"));

                    if (!horizontal.Success && step.Required)
                    {
                        return new PageExecution(false, false, false, "adaptive-horizontal-failed:" + horizontal.Detail, before, null);
                    }
                    break;
                }

                case ShareXModCaptureRecipeStepKind.ExpandOrActivate:
                {
                    if (step.Locator == null)
                    {
                        if (step.Required)
                        {
                            return new PageExecution(false, false, false, "adaptive-action-missing-locator", before, null);
                        }
                        audit.Add(new Audit(pageNumber, templateStep.Index, "expand", "skipped", "no-locator"));
                        break;
                    }

                    ShareXModRecipeSemanticActionResult action =
                        await ShareXModRecipeSemanticActionExecutor.ActivateAsync(
                            client,
                            step.Locator,
                            settings.CaptureRecipeActionTimeoutMs,
                            expectTransition: false);

                    audit.Add(new Audit(
                        pageNumber,
                        templateStep.Index,
                        "expand",
                        action.Activated ? "activated" : step.Required ? "failed" : "skipped",
                        $"{action.Detail}; score={action.Score:0.0}; gap={action.Gap:0.0}"));

                    if (!action.Activated && step.Required)
                    {
                        return new PageExecution(false, false, false, "adaptive-action-failed:" + action.Detail, before, null);
                    }
                    break;
                }

                case ShareXModCaptureRecipeStepKind.NextPage:
                {
                    if (!allowNextPage)
                    {
                        return new PageExecution(true, false, true, "adaptive-template-max-pages", before, null);
                    }

                    IReadOnlyList<ShareXModRecipeStaleRange> stale =
                        await pageGuard.RevalidateAsync(currentPageKey);

                    foreach (ShareXModRecipeStaleRange range in stale)
                    {
                        ShareXModRecipeGuardRepairResult repaired =
                            await ShareXModCaptureRecipePageGuardRepair.RecaptureAsync(
                                client,
                                settings,
                                range,
                                directory,
                                shouldStop);

                        if (!repaired.Success)
                        {
                            return new PageExecution(false, false, false, "adaptive-page-guard-failed:" + repaired.Detail, before, null);
                        }

                        int templateIndex = range.OriginalStep.Index % 100000;
                        List<Part> old = parts
                            .Where(x => x.PageNumber == pageNumber && x.TemplateStep == templateIndex)
                            .ToList();
                        foreach (Part item in old)
                        {
                            parts.Remove(item);
                            ShareXModCaptureRecipePageGuardRepair.ArchiveOldPart(directory, item.File);
                        }

                        foreach (ShareXModRecipeGuardReplacementPart item in repaired.Parts)
                        {
                            parts.Add(new Part(
                                item.File,
                                pageNumber,
                                templateIndex,
                                currentPageKey,
                                item.StartY,
                                item.EndY,
                                item.Width,
                                item.Height));
                        }

                        await pageGuard.RecordAsync(range.OriginalStep, range.EffectiveStep);
                        audit.Add(new Audit(pageNumber, templateIndex, "page-guard", "recaptured", string.Join(',', range.Reasons)));
                    }

                    if (stale.Count > 0)
                    {
                        IReadOnlyList<ShareXModRecipeStaleRange> remaining =
                            await pageGuard.RevalidateAsync(currentPageKey);
                        if (remaining.Count > 0)
                        {
                            return new PageExecution(false, false, false, "adaptive-page-still-stale", before, null);
                        }
                    }

                    await ShareXModCaptureRecipePageImageCollector.CollectAsync(
                        client,
                        settings,
                        directory,
                        pageNumber);

                    if (step.Locator == null)
                    {
                        return new PageExecution(false, false, false, "adaptive-next-missing-locator", before, null);
                    }

                    ShareXModRecipeSemanticActionResult next =
                        await ShareXModRecipeSemanticActionExecutor.ActivateAsync(
                            client,
                            step.Locator,
                            settings.CaptureRecipeActionTimeoutMs,
                            expectTransition: true);

                    audit.Add(new Audit(
                        pageNumber,
                        templateStep.Index,
                        "next-page",
                        next.Transitioned ? "verified" : next.Resolved ? "end" : "missing",
                        $"{next.Detail}; score={next.Score:0.0}; gap={next.Gap:0.0}"));

                    if (!next.Resolved)
                    {
                        return new PageExecution(true, false, true, "adaptive-next-not-found", before, null);
                    }

                    if (!next.Activated)
                    {
                        return new PageExecution(false, false, false, "adaptive-next-activation-failed", before, null);
                    }

                    if (!next.Transitioned)
                    {
                        return new PageExecution(true, false, true, "adaptive-next-no-transition", before, null);
                    }

                    ShareXModRecipePageEvidence after =
                        await ShareXModRecipePageTransitionVerifier.CaptureAsync(client);
                    return new PageExecution(true, true, false, "next-page-verified", before, after);
                }

                case ShareXModCaptureRecipeStepKind.PageCheckpoint:
                case ShareXModCaptureRecipeStepKind.WaitStable:
                    break;
            }
        }

        // A template without a NextPage step is not repeatable.
        await ShareXModCaptureRecipePageImageCollector.CollectAsync(
            client,
            settings,
            directory,
            pageNumber);
        return new PageExecution(true, false, true, "adaptive-template-ended-without-next", before, null);
    }

    private static void WriteManifest(
        string directory,
        ShareXModChromeTarget target,
        ShareXModAdaptivePageTemplatePlan plan,
        IReadOnlyList<Part> parts,
        IReadOnlyList<Audit> audit,
        int pagesCompleted,
        bool stoppedByUser,
        bool truncated,
        bool failed,
        string stopReason,
        string? finalPng,
        int sourceWidth,
        long sourceHeight)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "adaptive-page-loop-run.json"),
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Capture Recipe Page Loop Adaptive Template",
                    version = "0.9.0-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    target = new { target.Title, target.Url },
                    template = new
                    {
                        plan.InitialPageKey,
                        plan.TemplatePageKey,
                        plan.Similarity,
                        plan.DemonstratedTemplatePages,
                        initialStepCount = plan.InitialSteps.Count,
                        templateStepCount = plan.TemplateSteps.Count
                    },
                    pagesCompleted,
                    stoppedByUser,
                    truncated,
                    failed,
                    stopReason,
                    sourceWidth,
                    sourceHeight,
                    finalPng = finalPng == null ? null : Path.GetFileName(finalPng),
                    partCount = parts.Count,
                    parts,
                    audit
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static ShareXModCaptureRecipe? LoadRecipe(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ShareXModCaptureRecipe>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                });
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveDirectory(ShareXModV04Settings settings)
    {
        string relative = string.IsNullOrWhiteSpace(settings.CaptureRecipeOutputDirectory)
            ? "ShareX-Mod\\CaptureRecipeRuns"
            : settings.CaptureRecipeOutputDirectory;
        string root = Path.IsPathRooted(relative)
            ? relative
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));

        try { Directory.CreateDirectory(root); }
        catch
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX-Mod",
                "CaptureRecipeRuns");
            Directory.CreateDirectory(root);
        }

        string directory = Path.Combine(
            root,
            $"adaptive-loop-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        return directory;
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

        using Graphics graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.White);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int y = 0;
        foreach (Part part in parts)
        {
            using Bitmap image = new(Path.Combine(directory, part.File));
            int drawHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            graphics.DrawImage(
                image,
                new Rectangle(0, y, width, drawHeight),
                new Rectangle(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            y += drawHeight;
        }

        return preview;
    }
}
