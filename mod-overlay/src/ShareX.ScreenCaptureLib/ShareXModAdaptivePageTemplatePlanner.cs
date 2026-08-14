#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModAdaptivePageTemplatePlan(
    bool Candidate,
    string Reason,
    string InitialPageKey,
    string TemplatePageKey,
    IReadOnlyList<ShareXModCaptureRecipeStep> InitialSteps,
    IReadOnlyList<ShareXModCaptureRecipeStep> TemplateSteps,
    double Similarity,
    int DemonstratedTemplatePages);

internal static class ShareXModAdaptivePageTemplatePlanner
{
    public static ShareXModAdaptivePageTemplatePlan Build(
        ShareXModCaptureRecipe recipe,
        ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipeAdaptiveTemplateEnabled)
        {
            return Reject("adaptive-template-disabled");
        }

        List<IGrouping<string, ShareXModCaptureRecipeStep>> pages = recipe.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.PageKey))
            .OrderBy(x => x.Index)
            .GroupBy(x => x.PageKey)
            .ToList();

        List<PagePattern> repeatable = pages
            .Select(group => BuildPattern(group.Key, group.OrderBy(x => x.Index).ToList()))
            .Where(x => x.NextPage != null && x.CaptureRanges.Count > 0)
            .ToList();

        if (repeatable.Count < 2)
        {
            return Reject("need-two-demonstrated-pages-with-next-page");
        }

        PagePattern first = repeatable[0];
        PagePattern second = repeatable[1];
        double similarity = PatternSimilarity(first, second);

        if (similarity < Math.Clamp(settings.CaptureRecipeAdaptiveTemplateMinSimilarity, 0.5, 1.0))
        {
            return Reject($"demonstrated-pages-differ:{similarity:0.00}");
        }

        // Additional demonstrated pages, when present, must also resemble the recurrent template.
        int consistent = 2;
        for (int i = 2; i < repeatable.Count; i++)
        {
            double score = PatternSimilarity(second, repeatable[i]);
            if (score < Math.Clamp(settings.CaptureRecipeAdaptiveTemplateMinSimilarity, 0.5, 1.0))
            {
                break;
            }
            consistent++;
        }

        return new ShareXModAdaptivePageTemplatePlan(
            true,
            "two-page-semantic-template",
            first.PageKey,
            second.PageKey,
            first.Steps,
            second.Steps,
            similarity,
            consistent);
    }

    private static PagePattern BuildPattern(
        string pageKey,
        List<ShareXModCaptureRecipeStep> steps)
    {
        List<ShareXModCaptureRecipeStep> meaningful = steps
            .Where(x => x.Kind != ShareXModCaptureRecipeStepKind.PageCheckpoint &&
                        x.Kind != ShareXModCaptureRecipeStepKind.WaitStable)
            .ToList();

        return new PagePattern(
            pageKey,
            meaningful,
            meaningful.Where(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange).ToList(),
            meaningful.FirstOrDefault(x => x.Kind == ShareXModCaptureRecipeStepKind.NextPage));
    }

    private static double PatternSimilarity(PagePattern a, PagePattern b)
    {
        if (a.Steps.Count == 0 || b.Steps.Count == 0)
        {
            return 0;
        }

        int max = Math.Max(a.Steps.Count, b.Steps.Count);
        int min = Math.Min(a.Steps.Count, b.Steps.Count);
        double countScore = min / (double)max;

        double sum = 0;
        for (int i = 0; i < min; i++)
        {
            ShareXModCaptureRecipeStep left = a.Steps[i];
            ShareXModCaptureRecipeStep right = b.Steps[i];

            if (left.Kind != right.Kind)
            {
                continue;
            }

            double kind = 0.55;
            double locator = LocatorSimilarity(left.Locator, right.Locator);
            double range = left.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange
                ? RangeShapeSimilarity(left, right)
                : 1;

            sum += kind + locator * 0.30 + range * 0.15;
        }

        double aligned = sum / max;
        return Math.Clamp(aligned * 0.85 + countScore * 0.15, 0, 1);
    }

    private static double LocatorSimilarity(
        ShareXModRecipeLocator? a,
        ShareXModRecipeLocator? b)
    {
        if (a == null && b == null) return 1;
        if (a == null || b == null) return 0;

        double score = 0;
        double weight = 0;

        Compare(a.Id, b.Id, 5, ref score, ref weight);
        Compare(a.TestId, b.TestId, 5, ref score, ref weight);
        Compare(a.AriaLabel, b.AriaLabel, 4, ref score, ref weight);
        Compare(a.Role, b.Role, 3, ref score, ref weight);
        Compare(a.Name, b.Name, 2, ref score, ref weight);
        Compare(a.Tag, b.Tag, 2, ref score, ref weight);
        Compare(a.Href, b.Href, 1, ref score, ref weight, allowPathDifference: true);
        Compare(a.Text, b.Text, 3, ref score, ref weight, fuzzy: true);

        return weight <= 0 ? 0.5 : Math.Clamp(score / weight, 0, 1);
    }

    private static double RangeShapeSimilarity(
        ShareXModCaptureRecipeStep a,
        ShareXModCaptureRecipeStep b)
    {
        double ah = Math.Max(1, a.EndY - a.StartY);
        double bh = Math.Max(1, b.EndY - b.StartY);
        double ratio = Math.Min(ah, bh) / Math.Max(ah, bh);

        // Semantic anchors make exact absolute positions unimportant; only reject wildly
        // different demonstrated range shapes.
        return Math.Clamp(ratio, 0, 1);
    }

    private static void Compare(
        string? a,
        string? b,
        double w,
        ref double score,
        ref double weight,
        bool fuzzy = false,
        bool allowPathDifference = false)
    {
        string left = Normalize(a);
        string right = Normalize(b);
        if (left.Length == 0 && right.Length == 0) return;

        weight += w;
        if (left.Length == 0 || right.Length == 0) return;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            score += w;
            return;
        }

        if (allowPathDifference && Uri.TryCreate(left, UriKind.Absolute, out Uri? lu) &&
            Uri.TryCreate(right, UriKind.Absolute, out Uri? ru) &&
            string.Equals(lu.Host, ru.Host, StringComparison.OrdinalIgnoreCase))
        {
            score += w * 0.45;
            return;
        }

        if (fuzzy &&
            (left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
             right.Contains(left, StringComparison.OrdinalIgnoreCase)))
        {
            score += w * 0.55;
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static ShareXModAdaptivePageTemplatePlan Reject(string reason) =>
        new(
            false,
            reason,
            string.Empty,
            string.Empty,
            Array.Empty<ShareXModCaptureRecipeStep>(),
            Array.Empty<ShareXModCaptureRecipeStep>(),
            0,
            0);

    private sealed record PagePattern(
        string PageKey,
        IReadOnlyList<ShareXModCaptureRecipeStep> Steps,
        IReadOnlyList<ShareXModCaptureRecipeStep> CaptureRanges,
        ShareXModCaptureRecipeStep? NextPage);
}
