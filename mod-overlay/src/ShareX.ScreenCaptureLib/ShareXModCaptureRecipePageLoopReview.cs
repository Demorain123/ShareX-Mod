#nullable enable

using System.IO;
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

            ShareXModCaptureRecipePageLoopPlan plan =
                ShareXModCaptureRecipePageLoopPlanner.Build(recipe, settings);

            bool approved = ShareXModCaptureRecipePageLoopApproval.IsApproved(
                recipePath,
                settings,
                out int maxPages);

            return new ShareXModPageLoopReviewInfo(
                plan.Candidate,
                plan.Reason,
                approved,
                maxPages,
                plan.CaptureRanges.Count);
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
