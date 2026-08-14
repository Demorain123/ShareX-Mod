#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModCaptureRecipePageLoopPlan(
    bool Candidate,
    string Reason,
    string TemplatePageKey,
    IReadOnlyList<ShareXModCaptureRecipeStep> CaptureRanges,
    ShareXModCaptureRecipeStep? NextPageStep,
    int RecordedDistinctPages);

internal static class ShareXModCaptureRecipePageLoopPlanner
{
    public static ShareXModCaptureRecipePageLoopPlan Build(
        ShareXModCaptureRecipe recipe,
        ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipePageLoopEnabled)
        {
            return Reject("page-loop-disabled");
        }

        List<ShareXModCaptureRecipeStep> ordered = recipe.Steps
            .OrderBy(x => x.Index)
            .ToList();

        ShareXModCaptureRecipeStep? next = ordered
            .FirstOrDefault(x => x.Kind == ShareXModCaptureRecipeStepKind.NextPage);

        if (next == null || next.Locator == null)
        {
            return Reject("no-semantic-next-page-step");
        }

        string templatePage = next.PageKey;
        List<ShareXModCaptureRecipeStep> beforeNext = ordered
            .Where(x => x.Index < next.Index &&
                        string.Equals(x.PageKey, templatePage, StringComparison.Ordinal))
            .ToList();

        List<ShareXModCaptureRecipeStep> ranges = beforeNext
            .Where(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange)
            .ToList();

        if (ranges.Count == 0)
        {
            return Reject("template-has-no-vertical-range");
        }

        bool unsupportedAction = beforeNext.Any(x =>
            x.Kind is ShareXModCaptureRecipeStepKind.ExpandOrActivate or
                      ShareXModCaptureRecipeStepKind.HorizontalSweep);

        if (unsupportedAction)
        {
            return Reject("template-has-complex-actions-v0.8-does-not-loop-these-yet");
        }

        int distinctPages = recipe.Pages
            .Select(ShareXModCaptureRecipeCompiler.PageKey)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (settings.CaptureRecipePageLoopRequireObservedTransition && distinctPages < 2)
        {
            return Reject("no-observed-page-transition");
        }

        return new ShareXModCaptureRecipePageLoopPlan(
            true,
            "simple-paginated-template",
            templatePage,
            ranges,
            next,
            distinctPages);
    }

    private static ShareXModCaptureRecipePageLoopPlan Reject(string reason) =>
        new(
            false,
            reason,
            string.Empty,
            Array.Empty<ShareXModCaptureRecipeStep>(),
            null,
            0);
}
