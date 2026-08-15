#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecordedRecipePage(
    int InstanceNumber,
    string RuntimePageKey,
    IReadOnlyList<ShareXModCaptureRecipeStep> Steps);

/// <summary>
/// Splits an already compiled Recipe by the ordered PageCheckpoint boundaries instead of grouping
/// by URL. This preserves multiple demonstrated page instances even when an SPA keeps the same URL.
/// Runtime PageKey remains the ordinary URL-derived key and is therefore still safe for replay.
/// </summary>
internal static class ShareXModRecipeRecordedPages
{
    public static IReadOnlyList<ShareXModRecordedRecipePage> Split(
        ShareXModCaptureRecipe recipe)
    {
        List<ShareXModRecordedRecipePage> pages = new();
        List<ShareXModCaptureRecipeStep> current = new();
        string currentRuntimeKey = string.Empty;
        int instance = 0;

        foreach (ShareXModCaptureRecipeStep step in recipe.Steps.OrderBy(x => x.Index))
        {
            if (step.Kind == ShareXModCaptureRecipeStepKind.PageCheckpoint)
            {
                Flush();
                currentRuntimeKey = step.PageKey;
                current.Add(step);
                continue;
            }

            if (current.Count == 0)
            {
                currentRuntimeKey = step.PageKey;
            }

            current.Add(step);
        }

        Flush();

        // Backward compatibility for very old hand-authored Recipes without checkpoints.
        if (pages.Count == 0)
        {
            foreach (IGrouping<string, ShareXModCaptureRecipeStep> group in recipe.Steps
                         .Where(x => !string.IsNullOrWhiteSpace(x.PageKey))
                         .OrderBy(x => x.Index)
                         .GroupBy(x => x.PageKey))
            {
                pages.Add(new ShareXModRecordedRecipePage(
                    ++instance,
                    group.Key,
                    group.OrderBy(x => x.Index).ToList()));
            }
        }

        return pages;

        void Flush()
        {
            if (current.Count == 0) return;

            string runtimeKey = !string.IsNullOrWhiteSpace(currentRuntimeKey)
                ? currentRuntimeKey
                : current.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PageKey))?.PageKey ?? string.Empty;

            pages.Add(new ShareXModRecordedRecipePage(
                ++instance,
                runtimeKey,
                current.ToList()));

            current.Clear();
            currentRuntimeKey = string.Empty;
        }
    }
}
