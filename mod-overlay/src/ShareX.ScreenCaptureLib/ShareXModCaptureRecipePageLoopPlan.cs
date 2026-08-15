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
    private sealed record SimplePattern(
        ShareXModRecordedRecipePage Page,
        IReadOnlyList<ShareXModCaptureRecipeStep> Ranges,
        ShareXModCaptureRecipeStep Next);

    public static ShareXModCaptureRecipePageLoopPlan Build(
        ShareXModCaptureRecipe recipe,
        ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipePageLoopEnabled)
        {
            return Reject("page-loop-disabled");
        }

        IReadOnlyList<ShareXModRecordedRecipePage> recordedPages =
            ShareXModRecipeRecordedPages.Split(recipe);

        List<SimplePattern> patterns = recordedPages
            .Select(BuildSimplePattern)
            .Where(x => x != null)
            .Cast<SimplePattern>()
            .ToList();

        if (patterns.Count == 0)
        {
            return Reject("no-simple-semantic-page-template");
        }

        if (settings.CaptureRecipePageLoopRequireObservedTransition &&
            recordedPages.Count < 2)
        {
            return Reject("no-observed-page-transition");
        }

        if (patterns.Count < 2)
        {
            // One demonstrated simple page plus a transition is not enough evidence that later
            // pages share the same topology. Let Adaptive/Router handle the richer cases.
            return Reject("need-two-demonstrated-simple-pages");
        }

        SimplePattern template = patterns[0];
        double minSimilarity = Math.Clamp(
            settings.CaptureRecipeAdaptiveTemplateMinSimilarity,
            0.5,
            1.0);

        for (int i = 1; i < patterns.Count; i++)
        {
            double similarity = SimplePatternSimilarity(template, patterns[i]);
            if (similarity < minSimilarity)
            {
                return Reject($"demonstrated-pages-not-simple-template:{similarity:0.00}");
            }
        }

        // If the demonstration contains a recorded page that cannot be represented by the simple
        // topology, do not silently ignore it. This is exactly where Adaptive/Router should win.
        int meaningfulRecordedPages = recordedPages.Count(page =>
            page.Steps.Any(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange));
        if (meaningfulRecordedPages > patterns.Count)
        {
            return Reject("demonstration-contains-non-simple-page");
        }

        return new ShareXModCaptureRecipePageLoopPlan(
            true,
            $"verified-simple-paginated-template:{patterns.Count}-page-demonstration",
            template.Page.RuntimePageKey,
            template.Ranges,
            template.Next,
            recordedPages.Count);
    }

    private static SimplePattern? BuildSimplePattern(ShareXModRecordedRecipePage page)
    {
        List<ShareXModCaptureRecipeStep> meaningful = page.Steps
            .Where(x => x.Kind != ShareXModCaptureRecipeStepKind.PageCheckpoint &&
                        x.Kind != ShareXModCaptureRecipeStepKind.WaitStable)
            .OrderBy(x => x.Index)
            .ToList();

        List<ShareXModCaptureRecipeStep> ranges = meaningful
            .Where(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange)
            .ToList();

        ShareXModCaptureRecipeStep? next = meaningful
            .LastOrDefault(x => x.Kind == ShareXModCaptureRecipeStepKind.NextPage && x.Locator != null);

        if (ranges.Count == 0 || next?.Locator == null)
        {
            return null;
        }

        bool complex = meaningful.Any(x =>
            x.Kind is ShareXModCaptureRecipeStepKind.ExpandOrActivate or
                      ShareXModCaptureRecipeStepKind.HorizontalSweep);
        if (complex) return null;

        // Next Page must be the final meaningful user action of this recorded page. Otherwise the
        // sequence is not a simple page loop and replaying it as one could reorder user intent.
        ShareXModCaptureRecipeStep? last = meaningful.LastOrDefault();
        if (last == null || last.Index != next.Index)
        {
            return null;
        }

        return new SimplePattern(page, ranges, next);
    }

    private static double SimplePatternSimilarity(SimplePattern a, SimplePattern b)
    {
        if (a.Ranges.Count != b.Ranges.Count || a.Ranges.Count == 0) return 0;

        double rangeScore = 0;
        for (int i = 0; i < a.Ranges.Count; i++)
        {
            double ah = Math.Max(1, a.Ranges[i].EndY - a.Ranges[i].StartY);
            double bh = Math.Max(1, b.Ranges[i].EndY - b.Ranges[i].StartY);
            rangeScore += Math.Min(ah, bh) / Math.Max(ah, bh);
        }
        rangeScore /= a.Ranges.Count;

        double nextScore = LocatorSimilarity(a.Next.Locator!, b.Next.Locator!);
        return Math.Clamp(rangeScore * 0.60 + nextScore * 0.40, 0, 1);
    }

    private static double LocatorSimilarity(
        ShareXModRecipeLocator a,
        ShareXModRecipeLocator b)
    {
        double score = 0;
        double weight = 0;

        Compare(a.Id, b.Id, 5, ref score, ref weight);
        Compare(a.TestId, b.TestId, 5, ref score, ref weight);
        Compare(a.AriaLabel, b.AriaLabel, 4, ref score, ref weight);
        Compare(a.Role, b.Role, 3, ref score, ref weight);
        Compare(a.Name, b.Name, 2, ref score, ref weight);
        Compare(a.Tag, b.Tag, 2, ref score, ref weight);
        Compare(a.Rel, b.Rel, 4, ref score, ref weight);
        Compare(a.Text, b.Text, 3, ref score, ref weight, fuzzy: true);
        Compare(a.Href, b.Href, 1, ref score, ref weight, sameHost: true);

        return weight <= 0 ? 0.5 : Math.Clamp(score / weight, 0, 1);
    }

    private static void Compare(
        string? leftValue,
        string? rightValue,
        double w,
        ref double score,
        ref double weight,
        bool fuzzy = false,
        bool sameHost = false)
    {
        string left = Normalize(leftValue);
        string right = Normalize(rightValue);
        if (left.Length == 0 && right.Length == 0) return;
        weight += w;
        if (left.Length == 0 || right.Length == 0) return;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            score += w;
            return;
        }

        if (sameHost &&
            Uri.TryCreate(left, UriKind.Absolute, out Uri? lu) &&
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

    private static ShareXModCaptureRecipePageLoopPlan Reject(string reason) =>
        new(
            false,
            reason,
            string.Empty,
            Array.Empty<ShareXModCaptureRecipeStep>(),
            null,
            0);
}
