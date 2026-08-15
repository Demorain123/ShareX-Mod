#nullable enable

using System;
using System.Collections.Generic;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipePlannerSelfTests
{
    public static string RunOrThrow()
    {
        ShareXModV04Settings settings = new();
        int passed = 0;

        TestSameUrlRecordedPages(settings); passed++;
        TestSimpleLoopNeedsTwoDemonstratedPages(settings); passed++;
        TestComplexPagesUseAdaptivePlanner(settings); passed++;
        TestMultiFamilyRouter(settings); passed++;
        TestRequiredness(); passed++;

        return $"ShareX-Mod Recipe planner self-tests passed: {passed}";
    }

    private static void TestSameUrlRecordedPages(ShareXModV04Settings settings)
    {
        ShareXModCaptureRecipe recipe = Recipe(
            Checkpoint(1, "same"),
            Range(2, "same", 0, 900),
            Next(3, "same", NextLocator("next-a")),
            Checkpoint(4, "same"),
            Range(5, "same", 0, 900),
            Next(6, "same", NextLocator("next-a")));

        IReadOnlyList<ShareXModRecordedRecipePage> pages =
            ShareXModRecipeRecordedPages.Split(recipe);

        Assert(pages.Count == 2, "same-URL checkpoints must remain two recorded page instances");
        Assert(pages[0].RuntimePageKey == pages[1].RuntimePageKey,
            "same-URL fixture must retain the same runtime page key");

        ShareXModCaptureRecipePageLoopPlan plan =
            ShareXModCaptureRecipePageLoopPlanner.Build(recipe, settings);
        Assert(plan.Candidate,
            "two demonstrated same-URL simple pages should form a verified simple loop");
    }

    private static void TestSimpleLoopNeedsTwoDemonstratedPages(ShareXModV04Settings settings)
    {
        ShareXModCaptureRecipe onePage = Recipe(
            Checkpoint(1, "p1"),
            Range(2, "p1", 0, 1000),
            Next(3, "p1", NextLocator("next")));

        ShareXModCaptureRecipePageLoopPlan plan =
            ShareXModCaptureRecipePageLoopPlanner.Build(onePage, settings);

        Assert(!plan.Candidate,
            "one demonstrated page must not be promoted to unattended simple page-loop automation");
    }

    private static void TestComplexPagesUseAdaptivePlanner(ShareXModV04Settings settings)
    {
        ShareXModRecipeLocator expand = Locator("details", "Show details", "button");
        ShareXModRecipeLocator next = NextLocator("next");

        ShareXModCaptureRecipe recipe = Recipe(
            Checkpoint(1, "a1"), Range(2, "a1", 0, 800),
            OptionalAction(3, "a1", expand), Next(4, "a1", next),
            Checkpoint(5, "a2"), Range(6, "a2", 0, 800),
            OptionalAction(7, "a2", expand), Next(8, "a2", next));

        ShareXModCaptureRecipePageLoopPlan simple =
            ShareXModCaptureRecipePageLoopPlanner.Build(recipe, settings);
        Assert(!simple.Candidate,
            "semantic expansion actions must not be flattened into the simple loop topology");

        ShareXModAdaptivePageTemplatePlan adaptive =
            ShareXModAdaptivePageTemplatePlanner.Build(recipe, settings);
        Assert(adaptive.Candidate,
            "two similar complex demonstrated pages should remain adaptive-template candidates");
    }

    private static void TestMultiFamilyRouter(ShareXModV04Settings settings)
    {
        ShareXModRecipeLocator nextA = NextLocator("next-a");
        ShareXModRecipeLocator nextB = NextLocator("next-b");
        ShareXModRecipeLocator horizontal = Locator("gallery", "Gallery", "region");

        ShareXModCaptureRecipe recipe = Recipe(
            Checkpoint(1, "p1"), Range(2, "p1", 0, 900), Next(3, "p1", nextA),
            Checkpoint(4, "p2"), Range(5, "p2", 0, 900), Next(6, "p2", nextA),
            Checkpoint(7, "p3"), Range(8, "p3", 0, 900),
            Horizontal(9, "p3", horizontal), Next(10, "p3", nextB),
            Checkpoint(11, "p4"), Range(12, "p4", 0, 900), Next(13, "p4", nextA));

        ShareXModRecipeTemplateRouterPlan router =
            ShareXModCaptureRecipeTemplateRouter.Build(recipe, settings);

        Assert(router.Candidate,
            "A/B/A demonstrated pages should produce a multi-family router candidate");
        Assert(router.Families.Count == 2,
            $"A/B/A fixture should cluster into two families, got {router.Families.Count}");
    }

    private static void TestRequiredness()
    {
        ShareXModCaptureRecipeStep optional = OptionalAction(
            1, "p", Locator("optional", "Optional", "button"));
        ShareXModCaptureRecipeStep next = Next(2, "p", NextLocator("next"));

        Assert(!optional.Required,
            "skip-and-report expansion must remain optional in unattended review semantics");
        Assert(next.Required,
            "Next Page must remain safety-critical once its reviewed transition is executed");
    }

    private static ShareXModCaptureRecipe Recipe(params ShareXModCaptureRecipeStep[] steps)
    {
        ShareXModCaptureRecipeBoundary start = new(
            "recorded-start-position", 0, null, null, null, null, "test");
        ShareXModCaptureRecipeBoundary stop = new(
            "manual-or-configured-condition", 0, null, null, null, null, "test");

        return new ShareXModCaptureRecipe(
            "ShareX-Mod Capture Recipe", "self-test", "self-test",
            DateTimeOffset.UnixEpoch, "self-test", start, stop,
            Array.Empty<ShareXModRecipePageState>(), steps, 0, Array.Empty<string>());
    }

    private static ShareXModCaptureRecipeStep Checkpoint(int index, string pageKey) =>
        new(index, ShareXModCaptureRecipeStepKind.PageCheckpoint, pageKey,
            0, 0, 0, 0, null, true, new[] { "self-test" }, "re-resolve-page");

    private static ShareXModCaptureRecipeStep Range(int index, string pageKey, double start, double end) =>
        new(index, ShareXModCaptureRecipeStepKind.CaptureVerticalRange, pageKey,
            start, end, 0, 0, null, true, new[] { "self-test" }, "retry-range-or-mark-suspect");

    private static ShareXModCaptureRecipeStep OptionalAction(
        int index, string pageKey, ShareXModRecipeLocator locator) =>
        new(index, ShareXModCaptureRecipeStepKind.ExpandOrActivate, pageKey,
            0, 40, 0, 120, locator, true, new[] { "self-test" }, "skip-and-report-if-locator-missing");

    private static ShareXModCaptureRecipeStep Horizontal(
        int index, string pageKey, ShareXModRecipeLocator locator) =>
        new(index, ShareXModCaptureRecipeStepKind.HorizontalSweep, pageKey,
            100, 300, 0, 1200, locator, true, new[] { "self-test" }, "append-horizontal-panel-or-ask");

    private static ShareXModCaptureRecipeStep Next(
        int index, string pageKey, ShareXModRecipeLocator locator) =>
        new(index, ShareXModCaptureRecipeStepKind.NextPage, pageKey,
            800, 840, 0, 120, locator, true, new[] { "self-test" }, "stop-and-ask-if-navigation-unverified");

    private static ShareXModRecipeLocator NextLocator(string id) =>
        new("A", id, id, "link", "Next page", string.Empty, "Next", "/next", "next", string.Empty,
            100, 800, 120, 40, id);

    private static ShareXModRecipeLocator Locator(string id, string text, string role) =>
        new("BUTTON", id, id, role, text, string.Empty, text, string.Empty, string.Empty, "button",
            100, 100, 180, 40, id);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ShareX-Mod planner self-test failed: " + message);
    }
}
