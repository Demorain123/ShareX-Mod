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
        List<ShareXModRecipeRawEvent> ordered = raw
            .OrderBy(x => x.Sequence)
            .ToList();

        List<ShareXModRecipePageState> pages = ordered
            .Where(x => x.Page != null)
            .GroupBy(x => PageKey(x.Page), StringComparer.Ordinal)
            .Select(g => g.First().Page)
            .ToList();

        List<ShareXModCaptureRecipeStep> steps = new();
        int stepIndex = 0;

        foreach (IGrouping<string, ShareXModRecipeRawEvent> group in ordered
                     .GroupBy(x => PageKey(x.Page), StringComparer.Ordinal))
        {
            List<ShareXModRecipeRawEvent> pageEvents = group.ToList();
            ShareXModRecipePageState page = pageEvents[0].Page;

            steps.Add(new ShareXModCaptureRecipeStep(
                ++stepIndex,
                ShareXModCaptureRecipeStepKind.PageCheckpoint,
                group.Key,
                page.ScrollY,
                page.ScrollY,
                page.ScrollX,
                page.ScrollX,
                null,
                true,
                new[] { "page-state", page.Url },
                "re-resolve-page"));

            CompilePageEvents(
                pageEvents,
                group.Key,
                settings,
                steps,
                ref stepIndex);
        }

        ShareXModRecipePageState? firstPage = pages.FirstOrDefault();
        ShareXModRecipePageState? lastPage = pages.LastOrDefault();

        double startY = firstPage?.ScrollY ?? 0;
        double stopY = ordered.LastOrDefault()?.ScrollY ?? lastPage?.ScrollY ?? startY;

        ShareXModCaptureRecipeBoundary startBoundary = new(
            "recorded-start-position",
            startY,
            null,
            null,
            null,
            null,
            "Begin from the document position where recipe recording started.");

        ShareXModCaptureRecipeBoundary stopBoundary = new(
            "manual-or-configured-condition",
            stopY,
            null,
            settings.CaptureBoundaryMaxSteps > 0
                ? settings.CaptureBoundaryMaxSteps
                : null,
            settings.CaptureBoundaryMaxDurationSeconds > 0
                ? settings.CaptureBoundaryMaxDurationSeconds
                : null,
            settings.CaptureBoundaryMaxUnchangedPasses > 0
                ? settings.CaptureBoundaryMaxUnchangedPasses
                : null,
            "Manual Start/Stop remains authoritative; optional configured limits are preserved as safety conditions.");

        return new ShareXModCaptureRecipe(
            "ShareX-Mod Capture Recipe",
            "0.6.0-dev",
            ShareXModCaptureSessionContext.CurrentSessionId,
            DateTimeOffset.Now,
            "semantic-user-demonstration",
            startBoundary,
            stopBoundary,
            pages,
            steps,
            ordered.Count,
            new[]
            {
                "Recipe steps describe capture intent, not raw mouse/keyboard replay.",
                "Repeated vertical scrolling is normalized into content coverage ranges.",
                "Element actions keep semantic locators and geometry only as fallback evidence.",
                "Replay must re-resolve locators and verify post-action page state before continuing."
            });
    }

    private static void CompilePageEvents(
        List<ShareXModRecipeRawEvent> events,
        string pageKey,
        ShareXModV04Settings settings,
        List<ShareXModCaptureRecipeStep> steps,
        ref int stepIndex)
    {
        double? verticalMin = null;
        double? verticalMax = null;
        List<string> verticalEvidence = new();

        void FlushVertical()
        {
            if (verticalMin == null || verticalMax == null)
            {
                return;
            }

            double start = Math.Min(verticalMin.Value, verticalMax.Value);
            double end = Math.Max(verticalMin.Value, verticalMax.Value);

            if (end - start >= Math.Clamp(settings.CaptureRecipeMinimumVerticalRangeCss, 32, 2000))
            {
                steps.Add(new ShareXModCaptureRecipeStep(
                    ++stepIndex,
                    ShareXModCaptureRecipeStepKind.CaptureVerticalRange,
                    pageKey,
                    start,
                    end,
                    0,
                    0,
                    null,
                    true,
                    verticalEvidence.Distinct(StringComparer.Ordinal).ToArray(),
                    "retry-range-or-mark-suspect"));
            }

            verticalMin = null;
            verticalMax = null;
            verticalEvidence.Clear();
        }

        foreach (ShareXModRecipeRawEvent item in events)
        {
            if (item.Kind.Equals("scroll", StringComparison.OrdinalIgnoreCase) &&
                item.IsDocumentScroller)
            {
                double top = item.ScrollY;
                double bottom = item.ScrollY + Math.Max(1, item.ClientHeight);

                verticalMin = verticalMin == null ? top : Math.Min(verticalMin.Value, top);
                verticalMax = verticalMax == null ? bottom : Math.Max(verticalMax.Value, bottom);
                verticalEvidence.Add("document-scroll");
                continue;
            }

            if (item.Kind.Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FlushVertical();

            if (item.Kind.Equals("scroll", StringComparison.OrdinalIgnoreCase) &&
                !item.IsDocumentScroller &&
                item.Target != null)
            {
                bool horizontal = item.ScrollWidth > item.ClientWidth + 8 && item.ScrollX > 0;
                if (horizontal)
                {
                    steps.Add(new ShareXModCaptureRecipeStep(
                        ++stepIndex,
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
                item.Target != null &&
                item.Trusted)
            {
                ShareXModCaptureRecipeStepKind kind = LooksLikeNextPage(item.Target)
                    ? ShareXModCaptureRecipeStepKind.NextPage
                    : ShareXModCaptureRecipeStepKind.ExpandOrActivate;

                steps.Add(new ShareXModCaptureRecipeStep(
                    ++stepIndex,
                    kind,
                    pageKey,
                    item.Target.DocumentY,
                    item.Target.DocumentY + item.Target.Height,
                    item.Target.DocumentX,
                    item.Target.DocumentX + item.Target.Width,
                    item.Target,
                    true,
                    new[]
                    {
                        "trusted-user-click",
                        kind == ShareXModCaptureRecipeStepKind.NextPage
                            ? "pagination-candidate"
                            : "semantic-activation"
                    },
                    kind == ShareXModCaptureRecipeStepKind.NextPage
                        ? "stop-and-ask-if-navigation-unverified"
                        : "skip-and-report-if-locator-missing"));
            }
        }

        FlushVertical();
    }

    private static bool LooksLikeNextPage(ShareXModRecipeLocator locator)
    {
        string text = string.Join(
            " ",
            new[]
            {
                locator.Text,
                locator.AriaLabel,
                locator.Rel,
                locator.Id,
                locator.Name
            })
            .ToLowerInvariant();

        return locator.Rel.Equals("next", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("next page", StringComparison.Ordinal) ||
               text.Contains("next", StringComparison.Ordinal) ||
               text.Contains("下一页", StringComparison.Ordinal) ||
               text.Contains("下页", StringComparison.Ordinal) ||
               text.Contains("下一個", StringComparison.Ordinal) ||
               text.Contains("下一個頁", StringComparison.Ordinal);
    }

    internal static string LocatorFingerprint(
        string tag,
        string id,
        string testId,
        string role,
        string aria,
        string name,
        string text,
        string href)
    {
        string input = string.Join(
            "\u001f",
            new[] { tag, id, testId, role, aria, name, text, href }
                .Select(x => x?.Trim() ?? string.Empty));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string PageKey(ShareXModRecipePageState page)
    {
        string basis = page.Url.Split('#')[0];
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
