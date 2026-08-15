#nullable enable

using System;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModPaginationIntentSelfTests
{
    public static string RunOrThrow()
    {
        AssertNext(Locator("A", "", "", "link", "", "", "Anything", "/2", "next"), "rel=next");
        AssertNext(Locator("BUTTON", "", "", "button", "Next page", "", "Next page ›", "", ""), "explicit Next page label");
        AssertNext(Locator("A", "", "", "link", "Go to next page", "", "2", "/2", ""), "go to next page aria label");
        AssertNext(Locator("A", "", "", "link", "", "", "下一页 ›", "/2", ""), "Chinese next page");
        AssertNext(Locator("A", "", "", "link", "", "", "次のページ", "/2", ""), "Japanese next page");
        AssertNext(Locator("A", "", "", "link", "", "", "다음 페이지", "/2", ""), "Korean next page");
        AssertNext(Locator("A", "", "", "link", "", "", "Page suivante", "/2", ""), "French next page");
        AssertNext(Locator("BUTTON", "pagination-next", "", "button", "", "", "›", "", ""), "pagination-next id");

        AssertNotNext(Locator("BUTTON", "", "", "button", "Next", "", "Next", "", ""), "bare Next button");
        AssertNotNext(Locator("BUTTON", "", "", "button", "Next…", "", "Next…", "", ""), "bare Next punctuation");
        AssertNotNext(Locator("BUTTON", "next", "", "button", "", "", "Next image", "", ""), "generic id=next on image control");
        AssertNotNext(Locator("BUTTON", "btn-next", "", "button", "", "", "Next image", "", ""), "generic btn-next on image control");
        AssertNotNext(Locator("BUTTON", "button-next", "", "button", "", "", "Next step", "", ""), "generic button-next wizard control");
        AssertNotNext(Locator("A", "", "", "link", "", "", "Next.js", "/docs", ""), "Next.js");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Next comment", "", ""), "Next comment");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Next image", "", ""), "Next image");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Next slide", "", ""), "Next slide");
        AssertNotNext(Locator("A", "whats-next", "", "link", "", "", "What's next?", "/future", ""), "What's next");
        AssertNotNext(Locator("DIV", "", "", "", "", "", "Next page", "", ""), "non-interactive Next page text");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Go next", "", ""), "ambiguous Go next");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "次へ", "", ""), "ambiguous Japanese Next");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "다음", "", ""), "ambiguous Korean Next");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Suivant", "", ""), "ambiguous French Next");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Weiter", "", ""), "ambiguous German Next");
        AssertNotNext(Locator("BUTTON", "", "", "button", "", "", "Siguiente", "", ""), "ambiguous Spanish Next");

        return "ShareX-Mod pagination intent self-tests passed: 25";
    }

    private static void AssertNext(ShareXModRecipeLocator locator, string name)
    {
        ShareXModPaginationIntentDecision decision =
            ShareXModPaginationIntentClassifier.Classify(locator);
        if (!decision.IsNextPage)
            throw new InvalidOperationException($"Pagination self-test failed: expected Next Page for {name}; {decision.Reason}");
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
