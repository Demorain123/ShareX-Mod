#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureRecipeCompiler
{
    public static ShareXModCaptureRecipe Compile(
        IReadOnlyList<ShareXModRecipeRawEvent> raw,
        ShareXModV04Settings settings)
    {
        List<ShareXModRecipeRawEvent> ordered = raw.OrderBy(x => x.Sequence).ToList();
        List<List<ShareXModRecipeRawEvent>> pageSegments = BuildRecordedPageSegments(ordered);
        List<ShareXModRecipePageState> pages = pageSegments
            .Where(x => x.Count > 0)
            .Select(x => x[0].Page)
            .ToList();

        List<ShareXModCaptureRecipeStep> steps = new();
        int stepIndex = 0;

        foreach (List<ShareXModRecipeRawEvent> pageEvents in pageSegments)
        {
            if (pageEvents.Count == 0) continue;

            ShareXModRecipePageState page = pageEvents[0].Page;
            string runtimePageKey = PageKey(page);
            steps.Add(new ShareXModCaptureRecipeStep(
                ++stepIndex,
                ShareXModCaptureRecipeStepKind.PageCheckpoint,
                runtimePageKey,
                page.ScrollY,
                page.ScrollY,
                page.ScrollX,
                page.ScrollX,
                null,
                true,
                new[] { "page-state", page.Url, $"recorded-page-instance={pages.IndexOf(page) + 1}" },
                "re-resolve-page"));

            CompilePageEvents(pageEvents, runtimePageKey, settings, steps, ref stepIndex);
        }

        bool dynamicFeed = DetectDynamicFeed(ordered, settings);
        ShareXModRecipePageState? firstPage = pages.FirstOrDefault();
        double startY = firstPage?.ScrollY ?? 0;
        double stopY = ordered.LastOrDefault()?.ScrollY ?? pages.LastOrDefault()?.ScrollY ?? startY;

        ShareXModCaptureRecipeBoundary startBoundary = new(
            "recorded-start-position", startY, null, null, null, null,
            "Begin from the document position where recipe recording started; full-page capture is not implied.");

        int? maxSteps = settings.CaptureBoundaryMaxSteps > 0
            ? settings.CaptureBoundaryMaxSteps
            : dynamicFeed ? Math.Clamp(settings.CaptureRecipeDynamicFeedMaxSteps, 1, 100000) : null;
        int? maxDuration = settings.CaptureBoundaryMaxDurationSeconds > 0
            ? settings.CaptureBoundaryMaxDurationSeconds
            : dynamicFeed ? Math.Clamp(settings.CaptureRecipeDynamicFeedMaxDurationSeconds, 1, 86400) : null;
        int? maxUnchanged = settings.CaptureBoundaryMaxUnchangedPasses > 0
            ? settings.CaptureBoundaryMaxUnchangedPasses
            : dynamicFeed ? Math.Clamp(settings.CaptureRecipeDynamicFeedMaxUnchangedPasses, 1, 100) : null;

        ShareXModCaptureRecipeBoundary stopBoundary = new(
            dynamicFeed ? "dynamic-feed" : "manual-or-configured-condition",
            stopY,
            null,
            maxSteps,
            maxDuration,
            maxUnchanged,
            dynamicFeed
                ? "Continue the same growing feed until manual Stop or a bounded no-new-content/time/step condition is reached."
                : "Manual Start/Stop remains authoritative; optional configured limits are safety conditions.");

        List<string> notes = new()
        {
            "Recipe steps describe capture intent, not raw mouse/keyboard replay.",
            "Every page starts with at least the demonstrated visible viewport as a capture range.",
            "Repeated vertical scrolling is normalized into content coverage ranges.",
            "Vertical ranges can carry semantic top/bottom boundary anchors plus offsets, allowing them to follow later layout shifts like document-relative placeholders.",
            "Next Page actions form recorded page-instance boundaries even when an SPA keeps the same URL.",
            "Element actions keep semantic locators and geometry only as fallback evidence.",
            "Replay must re-resolve locators and verify post-action page state before continuing."
        };

        if (dynamicFeed)
        {
            notes.Add("The demonstration looked like a growing infinite feed; the recipe uses bounded dynamic-feed continuation rather than inventing page numbers.");
        }

        return new ShareXModCaptureRecipe(
            "ShareX-Mod Capture Recipe",
            "0.10.1-dev",
            ShareXModCaptureSessionContext.CurrentSessionId,
            DateTimeOffset.Now,
            "semantic-user-demonstration",
            startBoundary,
            stopBoundary,
            pages,
            steps,
            ordered.Count,
            notes.ToArray());
    }

    private static List<List<ShareXModRecipeRawEvent>> BuildRecordedPageSegments(
        List<ShareXModRecipeRawEvent> ordered)
    {
        List<List<ShareXModRecipeRawEvent>> segments = new();
        List<ShareXModRecipeRawEvent> current = new();

        foreach (ShareXModRecipeRawEvent item in ordered)
        {
            bool newDocumentPage = item.Kind.Equals("page", StringComparison.OrdinalIgnoreCase);
            if (newDocumentPage && current.Any(x => !x.Kind.Equals("page", StringComparison.OrdinalIgnoreCase)))
            {
                Flush();
            }

            current.Add(item);
            if (item.Kind.Equals("click", StringComparison.OrdinalIgnoreCase) &&
                item.Trusted && item.Target != null && LooksLikeNextPage(item.Target))
            {
                Flush();
            }
        }

        Flush();
        if (segments.Count == 0 && ordered.Count > 0) segments.Add(ordered.ToList());
        return segments;

        void Flush()
        {
            if (current.Count == 0) return;
            segments.Add(current.ToList());
            current.Clear();
        }
    }

    private static void CompilePageEvents(
        List<ShareXModRecipeRawEvent> events,
        string pageKey,
        ShareXModV04Settings settings,
        List<ShareXModCaptureRecipeStep> steps,
        ref int stepIndex)
    {
        int localStepIndex = stepIndex;
        ShareXModRecipeRawEvent initialEvent = events[0];
        ShareXModRecipePageState initial = initialEvent.Page;
        double? verticalMin = initial.ScrollY;
        double? verticalMax = initial.ScrollY + Math.Max(1, initial.ViewportHeight);
        ShareXModRecipeLocator? verticalStartAnchor = initialEvent.ViewportTopAnchor;
        ShareXModRecipeLocator? verticalEndAnchor = initialEvent.ViewportBottomAnchor;
        List<string> verticalEvidence = new() { "recorded-visible-viewport" };

        void CaptureStartAnchor(ShareXModRecipeRawEvent item)
        {
            if (item.ViewportTopAnchor != null) verticalStartAnchor = item.ViewportTopAnchor;
        }

        void CaptureEndAnchor(ShareXModRecipeRawEvent item)
        {
            if (item.ViewportBottomAnchor != null) verticalEndAnchor = item.ViewportBottomAnchor;
        }

        void FlushVertical()
        {
            if (verticalMin == null || verticalMax == null) return;

            double start = Math.Min(verticalMin.Value, verticalMax.Value);
            double end = Math.Max(verticalMin.Value, verticalMax.Value);

            if (end - start >= Math.Clamp(settings.CaptureRecipeMinimumVerticalRangeCss, 32, 2000))
            {
                List<string> evidence = verticalEvidence.Distinct(StringComparer.Ordinal).ToList();
                ShareXModRecipeLocator? startAnchor = verticalStartAnchor;

                if (startAnchor != null)
                {
                    evidence.Add("semantic-range-start-anchor");
                    evidence.Add(ShareXModRecipeAnchorEvidence.EncodeStartOffset(start - startAnchor.DocumentY));

                    if (verticalEndAnchor != null)
                    {
                        evidence.Add("semantic-range-end-anchor");
                        evidence.Add(ShareXModRecipeAnchorEvidence.EncodeEndAnchor(verticalEndAnchor));
                        evidence.Add(ShareXModRecipeAnchorEvidence.EncodeEndOffset(end - verticalEndAnchor.DocumentY));
                    }
                }

                steps.Add(new ShareXModCaptureRecipeStep(
                    ++localStepIndex,
                    ShareXModCaptureRecipeStepKind.CaptureVerticalRange,
                    pageKey,
                    start,
                    end,
                    0,
                    0,
                    startAnchor,
                    true,
                    evidence.Distinct(StringComparer.Ordinal).ToArray(),
                    "retry-range-or-mark-suspect"));
            }

            verticalMin = null;
            verticalMax = null;
            verticalStartAnchor = null;
            verticalEndAnchor = null;
            verticalEvidence.Clear();
        }

        foreach (ShareXModRecipeRawEvent item in events)
        {
            if (item.Kind.Equals("scroll", StringComparison.OrdinalIgnoreCase) && item.IsDocumentScroller)
            {
                double top = item.ScrollY;
                double viewport = item.ClientHeight > 0 ? item.ClientHeight : item.Page.ViewportHeight;
                double bottom = item.ScrollY + Math.Max(1, viewport);

                if (verticalMin == null || top < verticalMin.Value)
                {
                    verticalMin = top;
                    CaptureStartAnchor(item);
                }
                else if (verticalStartAnchor == null && Math.Abs(top - verticalMin.Value) <= 2)
                {
                    CaptureStartAnchor(item);
                }

                if (verticalMax == null || bottom > verticalMax.Value)
                {
                    verticalMax = bottom;
                    CaptureEndAnchor(item);
                }
                else if (verticalEndAnchor == null && Math.Abs(bottom - verticalMax.Value) <= 2)
                {
                    CaptureEndAnchor(item);
                }

                verticalEvidence.Add("document-scroll");
                continue;
            }

            if (item.Kind.Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                // A DOMContentLoaded page sample can fill anchors missing from the very early
                // addScriptToEvaluateOnNewDocument sample without creating another range.
                if (verticalStartAnchor == null) CaptureStartAnchor(item);
                if (verticalEndAnchor == null) CaptureEndAnchor(item);
                continue;
            }

            FlushVertical();

            if (item.Kind.Equals("scroll", StringComparison.OrdinalIgnoreCase) &&
                !item.IsDocumentScroller && item.Target != null)
            {
                bool horizontal = item.ScrollWidth > item.ClientWidth + 8 && item.ScrollX > 0;
                if (horizontal)
                {
                    steps.Add(new ShareXModCaptureRecipeStep(
                        ++localStepIndex,
                        ShareXModCaptureRecipeStepKind.HorizontalSweep,
                        pageKey,
                        item.Target.DocumentY,
                        item.Target.DocumentY + item.Target.Height,
                        0,
                        item.ScrollWidth,
                        item.Target,
                        true,
                        new[] { "nested-horizontal-scroll", $"observed-x={item.ScrollX:0.##}" },
                        "append-horizontal-panel-or-ask"));
                }
                continue;
            }

            if (item.Kind.Equals("click", StringComparison.OrdinalIgnoreCase) &&
                item.Target != null && item.Trusted)
            {
                ShareXModCaptureRecipeStepKind kind = LooksLikeNextPage(item.Target)
                    ? ShareXModCaptureRecipeStepKind.NextPage
                    : ShareXModCaptureRecipeStepKind.ExpandOrActivate;

                steps.Add(new ShareXModCaptureRecipeStep(
                    ++localStepIndex,
                    kind,
                    pageKey,
                    item.Target.DocumentY,
                    item.Target.DocumentY + item.Target.Height,
                    item.Target.DocumentX,
                    item.Target.DocumentX + item.Target.Width,
                    item.Target,
                    true,
                    new[] { "trusted-user-click", kind == ShareXModCaptureRecipeStepKind.NextPage ? "pagination-candidate" : "semantic-activation" },
                    kind == ShareXModCaptureRecipeStepKind.NextPage
                        ? "stop-and-ask-if-navigation-unverified"
                        : "skip-and-report-if-locator-missing"));

                if (kind != ShareXModCaptureRecipeStepKind.NextPage)
                {
                    verticalMin = item.Page.ScrollY;
                    verticalMax = item.Page.ScrollY + Math.Max(1, item.Page.ViewportHeight);
                    verticalStartAnchor = item.ViewportTopAnchor;
                    verticalEndAnchor = item.ViewportBottomAnchor;
                    verticalEvidence.Add("post-action-viewport");
                }
            }
        }

        FlushVertical();
        stepIndex = localStepIndex;
    }

    private static bool DetectDynamicFeed(List<ShareXModRecipeRawEvent> events, ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipeInferDynamicFeed) return false;
        if (events.Select(x => PageKey(x.Page)).Distinct(StringComparer.Ordinal).Count() != 1) return false;
        if (events.Any(x => x.Kind.Equals("click", StringComparison.OrdinalIgnoreCase) &&
                            x.Target != null && LooksLikeNextPage(x.Target))) return false;

        List<ShareXModRecipeRawEvent> scrolls = events
            .Where(x => x.Kind.Equals("scroll", StringComparison.OrdinalIgnoreCase) && x.IsDocumentScroller)
            .ToList();
        if (scrolls.Count < 3) return false;

        double minHeight = scrolls.Min(x => Math.Max(x.ScrollHeight, x.Page.DocumentHeight));
        double maxHeight = scrolls.Max(x => Math.Max(x.ScrollHeight, x.Page.DocumentHeight));
        double growth = maxHeight - minHeight;
        if (growth < Math.Clamp(settings.CaptureRecipeDynamicFeedGrowthThresholdCss, 32, 10000)) return false;

        ShareXModRecipeRawEvent last = scrolls[^1];
        double viewport = last.ClientHeight > 0 ? last.ClientHeight : last.Page.ViewportHeight;
        double documentHeight = Math.Max(last.ScrollHeight, last.Page.DocumentHeight);
        double remaining = documentHeight - (last.ScrollY + viewport);
        return remaining <= Math.Max(96, viewport * 0.35);
    }

    internal static bool LooksLikeNextPage(ShareXModRecipeLocator locator)
    {
        string text = string.Join(" ", new[]
        {
            locator.Text, locator.AriaLabel, locator.Rel, locator.Id, locator.Name
        }).ToLowerInvariant();

        return locator.Rel.Equals("next", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("next page", StringComparison.Ordinal) ||
               text.Contains("next", StringComparison.Ordinal) ||
               text.Contains("下一页", StringComparison.Ordinal) ||
               text.Contains("下页", StringComparison.Ordinal) ||
               text.Contains("下一個", StringComparison.Ordinal) ||
               text.Contains("下一個頁", StringComparison.Ordinal);
    }

    internal static string LocatorFingerprint(
        string tag, string id, string testId, string role, string aria,
        string name, string text, string href)
    {
        string input = string.Join("\u001f",
            new[] { tag, id, testId, role, aria, name, text, href }.Select(x => x?.Trim() ?? string.Empty));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    internal static string PageKey(ShareXModRecipePageState page)
    {
        string basis = page.Url.Split('#')[0];
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
