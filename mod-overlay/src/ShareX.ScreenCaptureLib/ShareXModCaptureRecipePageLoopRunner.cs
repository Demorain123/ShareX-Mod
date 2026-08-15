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

internal static class ShareXModCaptureRecipePageLoopRunner
{
    private sealed record LoopPart(
        string File,
        int PageNumber,
        int TemplateStep,
        string PageKey,
        double StartY,
        double EndY,
        int Width,
        int Height);

    private sealed record PageAudit(
        int PageNumber,
        string Url,
        string PageKey,
        string Fingerprint,
        int PartCount,
        string NextStatus,
        string[] Notes);

    public static async Task<ShareXModChromeBackgroundCaptureResult?> TryRunAsync(
        ShareXModChromeCdpClient client,
        ShareXModChromeTarget target,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        if (!settings.CaptureRecipePageLoopEnabled ||
            string.IsNullOrWhiteSpace(settings.CaptureRecipeReplayPath) ||
            !File.Exists(settings.CaptureRecipeReplayPath))
        {
            return null;
        }

        ShareXModCaptureRecipe? recipe = LoadRecipe(settings.CaptureRecipeReplayPath);
        if (recipe == null)
        {
            return null;
        }

        ShareXModCaptureRecipePageLoopPlan plan =
            ShareXModCaptureRecipePageLoopPlanner.Build(recipe, settings);

        if (!plan.Candidate || plan.NextPageStep?.Locator == null)
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
            Math.Clamp(approvedMaxPages, 1, 10000),
            Math.Clamp(settings.CaptureRecipeMaxNavigationCount + 1, 1, 10001));

        string directory = ResolveDirectory(settings);
        Directory.CreateDirectory(directory);
        ShareXModCaptureSessionContext.RegisterComponent("capture-recipe-page-loop", directory);

        List<LoopPart> parts = new();
        List<PageAudit> pages = new();
        HashSet<string> seenPageStates = new(StringComparer.Ordinal);
        ShareXModCaptureRecipePageGuard.Session pageGuard =
            ShareXModCaptureRecipePageGuard.Create(client, settings);

        bool stoppedByUser = false;
        bool truncated = false;
        bool failed = false;
        string stopReason = "page-loop-complete";
        bool overlayEmitted = false;
        int navigationCount = 0;

        for (int pageNumber = 1; pageNumber <= maxPages; pageNumber++)
        {
            if (shouldStop?.Invoke() == true)
            {
                stoppedByUser = true;
                stopReason = "manual-stop";
                break;
            }

            ShareXModRecipePageEvidence pageEvidence;
            try
            {
                pageEvidence = await ShareXModRecipePageTransitionVerifier.CaptureAsync(client);
            }
            catch
            {
                failed = true;
                stopReason = "page-state-unavailable";
                break;
            }

            string pageStateKey =
                pageEvidence.Url + "\u001f" + pageEvidence.MainFingerprint;

            if (!seenPageStates.Add(pageStateKey))
            {
                stopReason = "page-loop-cycle-detected";
                break;
            }

            string currentPageKey = PageKeyFromUrl(pageEvidence.Url);
            int pagePartStart = parts.Count;
            List<string> pageNotes = new();

            await ShareXModChromeOverlayDeduplicator.PreparePageAsync(client, settings);

            foreach (ShareXModCaptureRecipeStep templateRange in plan.CaptureRanges)
            {
                if (shouldStop?.Invoke() == true)
                {
                    stoppedByUser = true;
                    stopReason = "manual-stop";
                    break;
                }

                int syntheticStep = checked(pageNumber * 100000 + templateRange.Index);
                ShareXModCaptureRecipeStep pageStep = templateRange with
                {
                    Index = syntheticStep,
                    PageKey = currentPageKey
                };

                ShareXModCaptureRecipeStep effective =
                    await ShareXModRecipeRangeAnchorResolver.ResolveStepAsync(
                        client,
                        pageStep);

                ShareXModRecipeExactRangeResult capture =
                    await ShareXModCaptureRecipeExactRange.CaptureAsync(
                        client,
                        settings,
                        effective,
                        directory,
                        $"loop_p{pageNumber:D5}_s{templateRange.Index:D4}",
                        includeOverlayOnFirstTile: !overlayEmitted,
                        shouldStop);

                if (!capture.Success)
                {
                    failed = true;
                    stopReason = "page-loop-range-failed:" + capture.Detail;
                    pageNotes.Add(stopReason);
                    break;
                }

                foreach (ShareXModRecipeExactPart item in capture.Parts)
                {
                    parts.Add(new LoopPart(
                        item.File,
                        pageNumber,
                        templateRange.Index,
                        currentPageKey,
                        item.StartY,
                        item.EndY,
                        item.Width,
                        item.Height));
                }

                overlayEmitted = overlayEmitted || capture.Parts.Count > 0;
                await pageGuard.RecordAsync(pageStep, effective);
            }

            if (stoppedByUser || failed)
            {
                pages.Add(new PageAudit(
                    pageNumber,
                    pageEvidence.Url,
                    currentPageKey,
                    pageEvidence.MainFingerprint,
                    parts.Count - pagePartStart,
                    stopReason,
                    pageNotes.ToArray()));
                break;
            }

            IReadOnlyList<ShareXModRecipeStaleRange> staleRanges =
                await pageGuard.RevalidateAsync(currentPageKey);

            foreach (ShareXModRecipeStaleRange stale in staleRanges)
            {
                ShareXModRecipeGuardRepairResult repaired =
                    await ShareXModCaptureRecipePageGuardRepair.RecaptureAsync(
                        client,
                        settings,
                        stale,
                        directory,
                        shouldStop);

                if (!repaired.Success)
                {
                    failed = true;
                    stopReason = "page-loop-page-guard-failed:" + repaired.Detail;
                    pageNotes.Add(stopReason);
                    break;
                }

                List<LoopPart> old = parts
                    .Where(x => x.PageNumber == pageNumber &&
                                x.TemplateStep == stale.OriginalStep.Index % 100000)
                    .ToList();

                foreach (LoopPart oldPart in old)
                {
                    parts.Remove(oldPart);
                    ShareXModCaptureRecipePageGuardRepair.ArchiveOldPart(
                        directory,
                        oldPart.File);
                }

                int templateStep = stale.OriginalStep.Index % 100000;
                foreach (ShareXModRecipeGuardReplacementPart item in repaired.Parts)
                {
                    parts.Add(new LoopPart(
                        item.File,
                        pageNumber,
                        templateStep,
                        currentPageKey,
                        item.StartY,
                        item.EndY,
                        item.Width,
                        item.Height));
                }

                await pageGuard.RecordAsync(
                    stale.OriginalStep,
                    stale.EffectiveStep);

                pageNotes.Add(
                    $"page-guard-recaptured-template-step-{templateStep}:{string.Join(',', stale.Reasons)}");
            }

            if (!failed && staleRanges.Count > 0)
            {
                IReadOnlyList<ShareXModRecipeStaleRange> remaining =
                    await pageGuard.RevalidateAsync(currentPageKey);
                if (remaining.Count > 0)
                {
                    failed = true;
                    stopReason = "page-loop-page-still-stale";
                    pageNotes.Add($"remaining-stale-ranges={remaining.Count}");
                }
            }

            if (failed)
            {
                pages.Add(new PageAudit(
                    pageNumber,
                    pageEvidence.Url,
                    currentPageKey,
                    pageEvidence.MainFingerprint,
                    parts.Count - pagePartStart,
                    stopReason,
                    pageNotes.ToArray()));
                break;
            }

            if (pageNumber >= maxPages)
            {
                truncated = true;
                stopReason = "page-loop-max-pages";
                pages.Add(new PageAudit(
                    pageNumber,
                    pageEvidence.Url,
                    currentPageKey,
                    pageEvidence.MainFingerprint,
                    parts.Count - pagePartStart,
                    stopReason,
                    pageNotes.ToArray()));
                break;
            }

            ShareXModRecipeSemanticActionResult next =
                await ShareXModRecipeSemanticActionExecutor.ActivateAsync(
                    client,
                    plan.NextPageStep.Locator,
                    settings.CaptureRecipeActionTimeoutMs,
                    expectTransition: true);

            string nextStatus;
            if (!next.Resolved)
            {
                stopReason = "page-loop-next-not-found";
                nextStatus = stopReason;
            }
            else if (!next.Activated)
            {
                failed = true;
                stopReason = "page-loop-next-activation-failed";
                nextStatus = stopReason;
            }
            else if (!next.Transitioned)
            {
                stopReason = "page-loop-next-no-transition";
                nextStatus = stopReason;
            }
            else
            {
                navigationCount++;
                nextStatus = "next-page-verified";
            }

            pages.Add(new PageAudit(
                pageNumber,
                pageEvidence.Url,
                currentPageKey,
                pageEvidence.MainFingerprint,
                parts.Count - pagePartStart,
                nextStatus,
                pageNotes.ToArray()));

            if (!next.Transitioned || failed)
            {
                break;
            }

            await Task.Delay(100);
        }

        if (parts.Count == 0)
        {
            WriteManifest(
                directory,
                target,
                plan,
                parts,
                pages,
                stoppedByUser,
                truncated,
                failed,
                stopReason,
                null,
                0,
                0,
                navigationCount);
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
            stopReason = "page-loop-part-width-mismatch";
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
                finalPng = null;
                failed = true;
                stopReason = "page-loop-final-png-failed";
            }
        }

        string manifestPath = Path.Combine(directory, "page-loop-run.json");
        WriteManifest(
            directory,
            target,
            plan,
            parts,
            pages,
            stoppedByUser,
            truncated,
            failed,
            stopReason,
            finalPng,
            sourceWidth,
            sourceHeight,
            navigationCount);

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
        ShareXModCaptureRecipePageLoopPlan plan,
        IReadOnlyList<LoopPart> parts,
        IReadOnlyList<PageAudit> pages,
        bool stoppedByUser,
        bool truncated,
        bool failed,
        string stopReason,
        string? finalPng,
        int sourceWidth,
        long sourceHeight,
        int navigationCount)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "page-loop-run.json"),
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Capture Recipe Page Loop",
                    version = "0.8.0-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    target = new { target.Title, target.Url },
                    template = new
                    {
                        plan.TemplatePageKey,
                        captureRangeCount = plan.CaptureRanges.Count,
                        nextStep = plan.NextPageStep?.Index,
                        plan.Reason
                    },
                    stoppedByUser,
                    truncated,
                    failed,
                    stopReason,
                    navigationCount,
                    pageCount = pages.Count,
                    sourceWidth,
                    sourceHeight,
                    finalPng = finalPng == null ? null : Path.GetFileName(finalPng),
                    partCount = parts.Count,
                    parts,
                    pages
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

        try
        {
            Directory.CreateDirectory(root);
        }
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
            $"page-loop-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Bitmap CreatePreview(
        string directory,
        IReadOnlyList<LoopPart> parts,
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
        foreach (LoopPart part in parts)
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

    private static string PageKeyFromUrl(string url)
    {
        try
        {
            Uri uri = new(url);
            string canonical = uri.GetLeftPart(UriPartial.Path) + uri.Query;
            byte[] bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        }
        catch
        {
            byte[] bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty));
            return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        }
    }
}
