#nullable enable

using System;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModPaginationIntentSelfTests
{
    public static string RunOrThrow()
    {
        AssertNext(Locator("A", "", "", "link", "", "", "Anything", "/2", "next"), "rel=next");
        AssertNext(Locator("BUTTON", "", "", "button", "Next", "", "Next…", "", ""), "exact Next accessible label");
        AssertNext(Locator("A", "", "", "link", "Go to next page", "", "2", "/2", ""), "go to next page aria label");
        AssertNext(Locator("A", "", "", "link", "", "", "下一页 ›", "/2", ""), "Chinese next page");
        AssertNext(Locator("BUTTON", "pagination-next", "", "button", "", "", "›", "", ""), "pagination-next id");

        AssertNotNext(Locator("A", "", "", "link", "", "", "Next.js", "/docs", ""), "Next.js");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Next comment", "", ""), "Next comment");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Next image", "", ""), "Next image");
        AssertNotNext(Locator("A", "whats-next", "", "link", "", "", "What's next?", "/future", ""), "What's next");
        AssertNotNext(Locator("DIV", "", "", "", "", "", "Next", "", ""), "non-interactive Next text");

        return "ShareX-Mod pagination intent self-tests passed: 10";
    }

    private static void AssertNext(ShareXModRecipeLocator locator, string name)
    {
        ShareXModPaginationIntentDecision decision =
            ShareXModPaginationIntentClassifier.Classify(locator);
        if (!decision.IsNextPage)
            throw new InvalidOperationException($"Pagination self-test failed: expected Next for {name}; {decision.Reason}");
    }

    private static void AssertNotNext(ShareXModRecipeLocator locator, string name)
    {
        ShareXModPaginationIntentDecision decision =
            ShareXModPaginationIntentClassifier.Classify(locator);
        if (decision.IsNextPage)
            throw new InvalidOperationException($"Pagination self-test failed: false positive for {name}; {decision.Reason}");
    }

    private static ShareXModRecipeLocator Locator(
        string tag,
        string id,
        string testId,
        string role,
        string aria,
        string name,
        string text,
        string href,
        string rel) =>
        new(tag, id, testId, role, aria, name, text, href, rel, string.Empty,
            0, 0, 100, 30,
            ShareXModCaptureRecipeCompiler.LocatorFingerprint(
                tag, id, testId, role, aria, name, text, href));
}
