#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeReviewDecision(
    string Format,
    string Version,
    string RecipePath,
    string RecipeSha256,
    DateTimeOffset ReviewedAt,
    bool Approved,
    int[] DisabledSteps,
    string[] Notes);

internal sealed record ShareXModRecipeReviewStep(
    int Index,
    ShareXModCaptureRecipeStepKind Kind,
    string Title,
    string Detail,
    bool CanDisable,
    bool Enabled,
    string Risk);

internal sealed record ShareXModRecipeReviewSnapshot(
    string RecipePath,
    string RecipeSha256,
    bool HasValidApproval,
    bool Approved,
    bool DynamicFeed,
    IReadOnlyList<ShareXModRecipeReviewStep> Steps,
    int[] DisabledSteps,
    string Summary);

internal static class ShareXModCaptureRecipeReviewService
{
    public static ShareXModRecipeReviewSnapshot? Load(string? recipePath)
    {
        if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(recipePath);
            string sha = ComputeSha256(full);
            ShareXModCaptureRecipe? recipe = ReadRecipe(full);
            if (recipe == null) return null;

            ShareXModRecipeReviewDecision? decision = ReadDecision(full);
            bool valid = decision != null &&
                         string.Equals(decision.RecipeSha256, sha, StringComparison.OrdinalIgnoreCase);

            HashSet<int> disabled = valid
                ? decision!.DisabledSteps.ToHashSet()
                : new HashSet<int>();

            List<ShareXModRecipeReviewStep> steps = recipe.Steps
                .OrderBy(x => x.Index)
                .Select(step => Describe(step, disabled.Contains(step.Index)))
                .ToList();

            bool dynamicFeed = recipe.StopBoundary.Kind.Equals(
                "dynamic-feed",
                StringComparison.OrdinalIgnoreCase);

            int actions = steps.Count(x =>
                x.Kind is ShareXModCaptureRecipeStepKind.ExpandOrActivate or
                          ShareXModCaptureRecipeStepKind.NextPage or
                          ShareXModCaptureRecipeStepKind.HorizontalSweep);

            int pages = recipe.Pages
                .Select(ShareXModRecipePageIdentity.FromState)
                .Distinct(StringComparer.Ordinal)
                .Count();

            string summary =
                $"{pages} page state(s) · {steps.Count(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange)} capture range(s) · {actions} action/sweep step(s)" +
                (dynamicFeed ? " · dynamic feed" : string.Empty);

            return new ShareXModRecipeReviewSnapshot(
                full,
                sha,
                valid,
                valid && decision!.Approved,
                dynamicFeed,
                steps,
                disabled.OrderBy(x => x).ToArray(),
                summary);
        }
        catch
        {
            return null;
        }
    }

    public static ShareXModRecipeReviewSnapshot? Save(
        string recipePath,
        IEnumerable<int> disabledSteps,
        bool approved,
        string? note = null)
    {
        ShareXModRecipeReviewSnapshot? current = Load(recipePath);
        if (current == null) return null;

        HashSet<int> allowedDisabled = current.Steps
            .Where(x => x.CanDisable)
            .Select(x => x.Index)
            .ToHashSet();

        int[] disabled = disabledSteps
            .Where(allowedDisabled.Contains)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        ShareXModRecipeReviewDecision decision = new(
            "ShareX-Mod Capture Recipe Review",
            "0.7.5-dev",
            current.RecipePath,
            current.RecipeSha256,
            DateTimeOffset.Now,
            approved,
            disabled,
            string.IsNullOrWhiteSpace(note)
                ? Array.Empty<string>()
                : new[] { note.Trim() });

        string path = DecisionPath(current.RecipePath);
        string temp = path + ".tmp";
        string json = JsonSerializer.Serialize(
            decision,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });

        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, true);

        return Load(recipePath);
    }

    public static bool IsRunAllowed(
        ShareXModV04Settings settings,
        string? recipePath,
        out string reason)
    {
        reason = string.Empty;

        if (!settings.CaptureRecipeRequireReviewBeforeAutomation)
        {
            return true;
        }

        ShareXModRecipeReviewSnapshot? snapshot = Load(recipePath);
        if (snapshot == null)
        {
            reason = "Recipe could not be loaded for review.";
            return false;
        }

        if (!snapshot.HasValidApproval)
        {
            reason = "Recipe has not been reviewed, or it changed since the last review.";
            return false;
        }

        if (!snapshot.Approved)
        {
            reason = "Recipe review exists but is not approved for unattended run.";
            return false;
        }

        return true;
    }

    public static HashSet<int> GetDisabledSteps(string? recipePath)
    {
        ShareXModRecipeReviewSnapshot? snapshot = Load(recipePath);
        return snapshot != null && snapshot.HasValidApproval
            ? snapshot.DisabledSteps.ToHashSet()
            : new HashSet<int>();
    }

    public static bool ShouldStopInsteadOfSkip(
        ShareXModCaptureRecipeStep step,
        HashSet<int> disabledSteps)
    {
        return disabledSteps.Contains(step.Index) &&
               step.Kind == ShareXModCaptureRecipeStepKind.NextPage;
    }

    private static ShareXModRecipeReviewStep Describe(
        ShareXModCaptureRecipeStep step,
        bool disabled)
    {
        string locator = step.Locator == null
            ? string.Empty
            : FirstNonEmpty(
                step.Locator.AriaLabel,
                step.Locator.Text,
                step.Locator.Id,
                step.Locator.TestId,
                step.Locator.Role,
                step.Locator.Tag);

        string title;
        string detail;
        string risk;
        bool canDisable = step.Kind != ShareXModCaptureRecipeStepKind.PageCheckpoint;

        switch (step.Kind)
        {
            case ShareXModCaptureRecipeStepKind.PageCheckpoint:
                title = $"Page check · step {step.Index}";
                detail = "Verifies that replay is still on the expected page before continuing.";
                risk = "required";
                break;

            case ShareXModCaptureRecipeStepKind.CaptureVerticalRange:
                title = $"Capture vertical range · step {step.Index}";
                detail = $"Document Y {step.StartY:0} → {step.EndY:0}" +
                         (step.Locator != null ? " · semantic range anchor" : " · absolute-Y fallback");
                risk = "capture";
                break;

            case ShareXModCaptureRecipeStepKind.HorizontalSweep:
                title = $"Horizontal sweep · step {step.Index}";
                detail = string.IsNullOrWhiteSpace(locator)
                    ? "Captures a nested horizontal scroller into an appendix."
                    : $"Captures horizontal content near “{Truncate(locator, 90)}”.";
                risk = "low";
                break;

            case ShareXModCaptureRecipeStepKind.ExpandOrActivate:
                title = $"Expand / activate · step {step.Index}";
                detail = string.IsNullOrWhiteSpace(locator)
                    ? "Clicks a semantic element before continuing."
                    : $"Clicks “{Truncate(locator, 90)}”.";
                risk = "medium";
                break;

            case ShareXModCaptureRecipeStepKind.NextPage:
                title = $"Next page · step {step.Index}";
                detail = string.IsNullOrWhiteSpace(locator)
                    ? "Performs a semantic page transition. Disabling this safely stops before the transition."
                    : $"Transitions using “{Truncate(locator, 90)}”. Disabling safely stops here.";
                risk = "high";
                break;

            default:
                title = $"{step.Kind} · step {step.Index}";
                detail = string.Join(", ", step.Evidence.Take(3));
                risk = "low";
                break;
        }

        return new ShareXModRecipeReviewStep(
            step.Index,
            step.Kind,
            title,
            detail,
            canDisable,
            !disabled,
            risk);
    }

    private static ShareXModCaptureRecipe? ReadRecipe(string path)
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

    private static ShareXModRecipeReviewDecision? ReadDecision(string recipePath)
    {
        string path = DecisionPath(recipePath);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ShareXModRecipeReviewDecision>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string DecisionPath(string recipePath) =>
        Path.Combine(
            Path.GetDirectoryName(recipePath)!,
            "capture-recipe.review.json");

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
