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

internal static class ShareXModCaptureRecipeTemplateRouterRunner
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
        string Family,
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
        if (!settings.CaptureRecipeTemplateRouterEnabled ||
            string.IsNullOrWhiteSpace(settings.CaptureRecipeReplayPath) ||
            !File.Exists(settings.CaptureRecipeReplayPath))
        {
            return null;
        }

        ShareXModCaptureRecipe? recipe = LoadRecipe(settings.CaptureRecipeReplayPath);
        if (recipe == null) return null;

        ShareXModRecipeTemplateRouterPlan plan =
            ShareXModCaptureRecipeTemplateRouter.Build(recipe, settings);
        if (!plan.Candidate) return null;

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

        string suggestedDirectory = ResolveDirectory(settings);
        ShareXModPageLoopResumeJournal journal =
            ShareXModPageLoopResumeJournal.Open(
                settings,
                settings.CaptureRecipeReplayPath,
                suggestedDirectory);

        string directory = journal.OutputDirectory;
        CleanupUnusedSuggestedDirectory(suggestedDirectory, directory, journal.IsResumed);
        Directory.CreateDirectory(directory);
        ShareXModCaptureSessionContext.RegisterComponent("capture-recipe-template-router", directory);
        ShareXModCaptureSessionContext.RegisterComponent("template-router-resume", directory);

        List<Part> parts = journal.Parts
            .Select(x => new Part(
                x.File,
                x.PageNumber,
                x.TemplateStep,
                x.PageKey,
                x.StartY,
                x.EndY,
                x.Width,
                x.Height))
            .ToList();
        List<Audit> audit = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        ShareXModCaptureRecipePageGuard.Session pageGuard =
            ShareXModCaptureRecipePageGuard.Create(client, settings);

        bool stoppedByUser = false;
        bool truncated = false;
        bool failed = false;
        bool overlayEmitted = parts.Count > 0;
        string stopReason = "template-router-complete";
        int pagesCompleted = 0;
        int startPage = journal.IsResumed ? Math.Max(1, journal.NextPageNumber) : 1;

        if (journal.IsResumed)
        {
            (bool blocked, int resolvedStart, string reason) =
                await PrepareResumeAsync(client, journal, parts, startPage);
            startPage = resolvedStart;
            if (blocked)
            {
                truncated = true;
                stopReason = reason;
                return FinalizeResult(
                    directory, target, plan, parts, audit, pagesCompleted,
                    stoppedByUser, truncated, failed, stopReason, settings);
            }
        }

        for (int pageNumber = startPage; pageNumber <= maxPages; pageNumber++)
        {
            if (shouldStop?.Invoke() == true)
            {
                stoppedByUser = true;
                stopReason = "manual-stop";
                break;
            }

            ShareXModRecipePageEvidence current;
            try
            {
                current = await ShareXModRecipePageTransitionVerifier.CaptureAsync(client);
            }
            catch
            {
                failed = true;
                stopReason = "template-router-page-state-unavailable";
                journal.Complete("failed");
                break;
            }

            string cycleKey = current.Url + "\u001f" + current.MainFingerprint;
            if (!seen.Add(cycleKey))
            {
                stopReason = "template-router-cycle-detected";
                journal.Complete(stopReason);
                break;
            }

            IReadOnlyList<ShareXModCaptureRecipeStep> template;
            string familyId;

            if (pageNumber == 1 && !journal.IsResumed)
            {
                template = plan.InitialSteps;
                familyId = "initial";
            }
            else
            {
                ShareXModRecipeTemplateSelection selection =
                    await ShareXModCaptureRecipeTemplateRouter.SelectAsync(
                        client,
                        plan,
                        settings);

                audit.Add(new Audit(
                    pageNumber,
                    selection.Family?.Id ?? string.Empty,
                    0,
                    "template-router",
                    selection.Selected ? "selected" : selection.Status,
                    $"score={selection.Score:0.000}; gap={selection.Gap:0.000}; {string.Join(" | ", selection.Evidence.Take(8))}"));

                if (!selection.Selected || selection.Family == null)
                {
                    truncated = true;
                    stopReason = "template-router-" + selection.Status;
                    journal.NeedsReview(
                        $"Template Router could not choose one unique family on page {pageNumber}: {selection.Status}; score={selection.Score:0.000}; gap={selection.Gap:0.000}.");
                    break;
                }

                template = selection.Family.Steps;
                familyId = selection.Family.Id;
            }

            journal.BeginPage(pageNumber, current);

            PageExecution execution = await ExecutePageAsync(
                client,
                settings,
                directory,
                template,
                familyId,
                pageNumber,
                current,
                parts,
                audit,
                pageGuard,
                includeFirstOverlay: !overlayEmitted,
                allowNextPage: pageNumber < maxPages,
                journal,
                shouldStop);

            overlayEmitted = overlayEmitted || parts.Any(x => x.PageNumber == pageNumber);

            if (!execution.Success)
            {
                if (execution.StopReason == "manual-stop")
                {
                    stoppedByUser = true;
                }
                else
                {
                    failed = true;
                    journal.Complete("failed");
                }
                stopReason = execution.StopReason;
                break;
            }

            pagesCompleted++;

            if (execution.EndReached)
            {
                stopReason = execution.StopReason;
                journal.Complete(stopReason);
                break;
            }

            if (!execution.Transitioned)
            {
                stopReason = execution.StopReason;
                journal.Complete(stopReason);
                break;
            }

            if (execution.After != null)
            {
                journal.CompleteTransition(pageNumber, execution.After);
            }

            if (pageNumber >= maxPages)
            {
                truncated = true;
                stopReason = "template-router-max-pages";
                journal.Complete(stopReason);
                break;
            }

            await Task.Delay(100);
        }

        return FinalizeResult(
            directory, target, plan, parts, audit, pagesCompleted,
            stoppedByUser, truncated, failed, stopReason, settings);
    }

    private static async Task<PageExecution> ExecutePageAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string directory,
        IReadOnlyList<ShareXModCaptureRecipeStep> template,
        string familyId,
        int pageNumber,
        ShareXModRecipePageEvidence before,
        List<Part> parts,
        List<Audit> audit,
        ShareXModCaptureRecipePageGuard.Session pageGuard,
        bool includeFirstOverlay,
        bool allowNextPage,
        ShareXModPageLoopResumeJournal journal,
        Func<bool>? shouldStop)
    {
        string currentPageKey = ShareXModRecipePageIdentity.FromUrl(before.Url);
        await ShareXModChromeOverlayDeduplicator.PreparePageAsync(client, settings);
        bool firstRange = includeFirstOverlay;

        foreach (ShareXModCaptureRecipeStep templateStep in template.OrderBy(x => x.Index))
        {
            if (shouldStop?.Invoke() == true)
                return new PageExecution(false, false, false, "manual-stop", before, null);

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
                            $"router_{Sanitize(familyId)}_p{pageNumber:D5}_s{templateStep.Index:D4}",
                            includeOverlayOnFirstTile: firstRange,
                            shouldStop);

                    if (!capture.Success)
                    {
                        audit.Add(new Audit(pageNumber, familyId, templateStep.Index, "vertical-range", "failed", capture.Detail));
                        return new PageExecution(false, false, false, "template-router-range-failed:" + capture.Detail, before, null);
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
                    audit.Add(new Audit(pageNumber, familyId, templateStep.Index, "vertical-range", "captured", $"parts={capture.Parts.Count}"));
                    break;
                }

                case ShareXModCaptureRecipeStepKind.HorizontalSweep:
                {
                    if (step.Locator == null)
                    {
                        if (step.Required)
                            return new PageExecution(false, false, false, "template-router-horizontal-missing-locator", before, null);
                        audit.Add(new Audit(pageNumber, familyId, templateStep.Index, "horizontal-sweep", "skipped", "no-locator"));
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
                        pageNumber, familyId, templateStep.Index, "horizontal-sweep",
                        horizontal.Success ? "captured" : step.Required ? "failed" : "skipped",
                        $"{horizontal.Detail}; panels={horizontal.PanelCount}; score={horizontal.LocatorScore:0.0}; gap={horizontal.LocatorGap:0.0}"));

                    if (!horizontal.Success && step.Required)
                        return new PageExecution(false, false, false, "template-router-horizontal-failed:" + horizontal.Detail, before, null);
                    break;
                }

                case ShareXModCaptureRecipeStepKind.ExpandOrActivate:
                {
                    if (step.Locator == null)
                    {
                        if (step.Required)
                            return new PageExecution(false, false, false, "template-router-action-missing-locator", before, null);
                        audit.Add(new Audit(pageNumber, familyId, templateStep.Index, "expand", "skipped", "no-locator"));
                        break;
                    }

                    ShareXModRecipeSemanticActionResult action =
                        await ShareXModRecipeSemanticActionExecutor.ActivateAsync(
                            client,
                            step.Locator,
                            settings.CaptureRecipeActionTimeoutMs,
                            expectTransition: false);

                    audit.Add(new Audit(
                        pageNumber, familyId, templateStep.Index, "expand",
                        action.Activated ? "activated" : step.Required ? "failed" : "skipped",
                        $"{action.Detail}; score={action.Score:0.0}; gap={action.Gap:0.0}"));

                    if (!action.Activated && step.Required)
                        return new PageExecution(false, false, false, "template-router-action-failed:" + action.Detail, before, null);
                    break;
                }

                case ShareXModCaptureRecipeStepKind.NextPage:
                {
                    if (!allowNextPage)
                    {
                        await CommitPageAsync(client, settings, directory, pageNumber, parts, journal);
                        return new PageExecution(true, false, true, "template-router-max-pages", before, null);
                    }

                    if (!await RepairStaleRangesAsync(
                            client,
                            settings,
                            directory,
                            pageNumber,
                            currentPageKey,
                            parts,
                            audit,
                            familyId,
                            pageGuard,
                            shouldStop))
                    {
                        return new PageExecution(false, false, false, "template-router-page-guard-failed", before, null);
                    }

                    await CommitPageAsync(client, settings, directory, pageNumber, parts, journal);

                    if (step.Locator == null)
                        return new PageExecution(false, false, false, "template-router-next-missing-locator", before, null);

                    journal.BeforeNext(pageNumber, before);

                    ShareXModRecipeSemanticActionResult next =
                        await ShareXModRecipeSemanticActionExecutor.ActivateAsync(
                            client,
                            step.Locator,
                            settings.CaptureRecipeActionTimeoutMs,
                            expectTransition: true);

                    audit.Add(new Audit(
                        pageNumber, familyId, templateStep.Index, "next-page",
                        next.Transitioned ? "verified" : next.Resolved ? "end" : "missing",
                        $"{next.Detail}; score={next.Score:0.0}; gap={next.Gap:0.0}"));

                    if (!next.Resolved)
                        return new PageExecution(true, false, true, "template-router-next-not-found", before, null);
                    if (!next.Activated)
                        return new PageExecution(false, false, false, "template-router-next-activation-failed", before, null);
                    if (!next.Transitioned)
                        return new PageExecution(true, false, true, "template-router-next-no-transition", before, null);

                    ShareXModRecipePageEvidence after =
                        await ShareXModRecipePageTransitionVerifier.CaptureAsync(client);
                    return new PageExecution(true, true, false, "next-page-verified", before, after);
                }

                case ShareXModCaptureRecipeStepKind.PageCheckpoint:
                case ShareXModCaptureRecipeStepKind.WaitStable:
                    break;
            }
        }

        await CommitPageAsync(client, settings, directory, pageNumber, parts, journal);
        return new PageExecution(true, false, true, "template-router-page-ended-without-next", before, null);
    }

    private static async Task CommitPageAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string directory,
        int pageNumber,
        List<Part> parts,
        ShareXModPageLoopResumeJournal journal)
    {
        List<ShareXModPageLoopResumePart> committed = parts
            .Where(x => x.PageNumber == pageNumber)
            .OrderBy(x => x.TemplateStep)
            .ThenBy(x => x.StartY)
            .Select(x => new ShareXModPageLoopResumePart(
                x.File, x.PageNumber, x.TemplateStep, x.PageKey,
                x.StartY, x.EndY, x.Width, x.Height))
            .ToList();
        journal.RecordPageParts(pageNumber, committed);

        await ShareXModCaptureRecipePageImageCollector.CollectAsync(
            client,
            settings,
            directory,
            pageNumber);
    }

    private static async Task<bool> RepairStaleRangesAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string directory,
        int pageNumber,
        string currentPageKey,
        List<Part> parts,
        List<Audit> audit,
        string familyId,
        ShareXModCaptureRecipePageGuard.Session pageGuard,
        Func<bool>? shouldStop)
    {
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

            if (!repaired.Success) return false;

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
            audit.Add(new Audit(
                pageNumber,
                familyId,
                templateIndex,
                "page-guard",
                "recaptured",
                string.Join(',', range.Reasons)));
        }

        if (stale.Count == 0) return true;
        return (await pageGuard.RevalidateAsync(currentPageKey)).Count == 0;
    }

    private static async Task<(bool Blocked, int StartPage, string Reason)> PrepareResumeAsync(
        ShareXModChromeCdpClient client,
        ShareXModPageLoopResumeJournal journal,
        List<Part> parts,
        int startPage)
    {
        if (journal.Phase.Equals("before-next", StringComparison.OrdinalIgnoreCase) ||
            journal.Phase.Equals("needs-review", StringComparison.OrdinalIgnoreCase))
        {
            journal.NeedsReview("Template Router resume stopped at an ambiguous in-flight Next Page boundary.");
            return (true, startPage, "template-router-resume-needs-review");
        }

        if (journal.Phase.Equals("complete", StringComparison.OrdinalIgnoreCase))
        {
            journal.NeedsReview("Previous Template Router journal is complete; automatic restart is not guessed.");
            return (true, startPage, "template-router-resume-complete-boundary-review");
        }

        ShareXModRecipePageEvidence evidence =
            await ShareXModRecipePageTransitionVerifier.CaptureAsync(client);

        if (journal.Phase.Equals("ready", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(journal.CurrentFingerprint) &&
            (!string.Equals(evidence.MainFingerprint, journal.CurrentFingerprint, StringComparison.Ordinal) ||
             !string.Equals(evidence.Url, journal.CurrentUrl, StringComparison.Ordinal)))
        {
            journal.NeedsReview("Current browser page does not match the last committed Template Router boundary.");
            return (true, startPage, "template-router-resume-page-mismatch");
        }

        if (journal.Phase.Equals("capturing", StringComparison.OrdinalIgnoreCase))
        {
            int redoPage = Math.Max(1, journal.CurrentPageNumber);
            journal.RemovePageParts(redoPage, archive: true);
            parts.RemoveAll(x => x.PageNumber == redoPage);
            startPage = redoPage;
        }

        return (false, startPage, "resume-ready");
    }

    private static ShareXModChromeBackgroundCaptureResult? FinalizeResult(
        string directory,
        ShareXModChromeTarget target,
        ShareXModRecipeTemplateRouterPlan plan,
        List<Part> parts,
        List<Audit> audit,
        int pagesCompleted,
        bool stoppedByUser,
        bool truncated,
        bool failed,
        string stopReason,
        ShareXModV04Settings settings)
    {
        if (parts.Count == 0)
        {
            WriteManifest(directory, target, plan, parts, audit, pagesCompleted,
                stoppedByUser, truncated, failed, stopReason, null, 0, 0);
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
            stopReason = "template-router-part-width-mismatch";
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
                stopReason = "template-router-final-png-failed";
                finalPng = null;
            }
        }

        string manifestPath = Path.Combine(directory, "template-router-run.json");
        WriteManifest(directory, target, plan, parts, audit, pagesCompleted,
            stoppedByUser, truncated, failed, stopReason,
            finalPng, sourceWidth, sourceHeight);

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

    private static void WriteManifest(
        string directory,
        ShareXModChromeTarget target,
        ShareXModRecipeTemplateRouterPlan plan,
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
                Path.Combine(directory, "template-router-run.json"),
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Capture Recipe Page Loop Template Router",
                    version = "current-integration",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    target = new { target.Title, target.Url },
                    router = new
                    {
                        plan.Reason,
                        plan.InitialPageKey,
                        plan.DemonstratedRepeatablePages,
                        familyCount = plan.Families.Count,
                        families = plan.Families.Select(x => new
                        {
                            x.Id,
                            x.RepresentativePageKey,
                            x.DemonstratedPages,
                            x.InternalSimilarity,
                            stepCount = x.Steps.Count
                        })
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
        catch { }
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
        catch { return null; }
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
            $"template-router-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
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

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static void CleanupUnusedSuggestedDirectory(
        string suggested,
        string selected,
        bool resumed)
    {
        if (!resumed || string.Equals(suggested, selected, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (Directory.Exists(suggested) && !Directory.EnumerateFileSystemEntries(suggested).Any())
                Directory.Delete(suggested);
        }
        catch { }
    }
}
