#nullable enable

using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModPageLoopReviewInfo(
    bool Candidate,
    string Reason,
    bool Approved,
    int MaxPages,
    int CaptureRangeCount);

internal static class ShareXModCaptureRecipePageLoopReview
{
    public static ShareXModPageLoopReviewInfo Inspect(
        string recipePath,
        ShareXModV04Settings settings)
    {
        if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
        {
            return new ShareXModPageLoopReviewInfo(
                false,
                "recipe-missing",
                false,
                settings.CaptureRecipePageLoopMaxPages,
                0);
        }

        try
        {
            ShareXModCaptureRecipe? recipe =
                JsonSerializer.Deserialize<ShareXModCaptureRecipe>(
                    File.ReadAllText(recipePath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new JsonStringEnumConverter() }
                    });

            if (recipe == null)
            {
                throw new InvalidDataException();
            }

            ShareXModCaptureRecipePageLoopPlan simple =
                ShareXModCaptureRecipePageLoopPlanner.Build(recipe, settings);

            ShareXModAdaptivePageTemplatePlan adaptive =
                ShareXModAdaptivePageTemplatePlanner.Build(recipe, settings);

            bool approved = ShareXModCaptureRecipePageLoopApproval.IsApproved(
                recipePath,
                settings,
                out int maxPages);

            if (simple.Candidate)
            {
                return new ShareXModPageLoopReviewInfo(
                    true,
                    "simple:" + simple.Reason,
                    approved,
                    maxPages,
                    simple.CaptureRanges.Count);
            }

            if (adaptive.Candidate)
            {
                return new ShareXModPageLoopReviewInfo(
                    true,
                    $"adaptive:{adaptive.Reason}; similarity={adaptive.Similarity:0.00}",
                    approved,
                    maxPages,
                    adaptive.TemplateSteps.Count(x =>
                        x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange));
            }

            return new ShareXModPageLoopReviewInfo(
                false,
                $"simple={simple.Reason}; adaptive={adaptive.Reason}",
                false,
                maxPages,
                0);
        }
        catch
        {
            return new ShareXModPageLoopReviewInfo(
                false,
                "recipe-read-failed",
                false,
                settings.CaptureRecipePageLoopMaxPages,
                0);
        }
    }
}
