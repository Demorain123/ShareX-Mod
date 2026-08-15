#nullable enable

using System;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipeAnchorSelfTests
{
    public static string RunOrThrow()
    {
        ShareXModRecipeLocator top = Locator("post-a", "Post A", 10);
        ShareXModRecipeLocator bottom = Locator("post-b", "Post B", 1390);
        ShareXModRecipePageState page = new(
            "https://example.test/thread",
            "Thread",
            0,
            0,
            1200,
            2400,
            1200,
            800,
            DateTimeOffset.UnixEpoch);

        ShareXModRecipeRawEvent pageEvent = new(
            1, "page", 0, page, null,
            0, 0, 1200, 2400, 1200, 800, true, true)
        {
            ViewportTopAnchor = top,
            ViewportBottomAnchor = Locator("post-a-bottom", "Post A bottom", 790)
        };

        ShareXModRecipeRawEvent scrollEvent = new(
            2, "scroll", 1, page with { ScrollY = 600 }, null,
            0, 600, 1200, 2400, 1200, 800, true, true)
        {
            ViewportTopAnchor = Locator("post-mid", "Post middle", 610),
            ViewportBottomAnchor = bottom
        };

        ShareXModCaptureRecipe recipe = ShareXModCaptureRecipeCompiler.Compile(
            new[] { pageEvent, scrollEvent },
            new ShareXModV04Settings());

        ShareXModCaptureRecipeStep range = recipe.Steps.Single(x =>
            x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange);

        Assert(range.Locator?.Fingerprint == top.Fingerprint,
            "compiled vertical range must retain the semantic top placeholder");
        Assert(ShareXModRecipeAnchorEvidence.TryGetStartOffset(range.Evidence, out double startOffset),
            "compiled vertical range must encode its start offset");
        Assert(Math.Abs(startOffset + 10) < 0.001,
            $"expected start offset -10, got {startOffset}");
        Assert(ShareXModRecipeAnchorEvidence.TryGetEndAnchor(range.Evidence, out ShareXModRecipeLocator? end) &&
               end?.Fingerprint == bottom.Fingerprint,
            "compiled vertical range must encode the semantic bottom placeholder");
        Assert(ShareXModRecipeAnchorEvidence.TryGetEndOffset(range.Evidence, out double endOffset),
            "compiled vertical range must encode its end offset");
        Assert(Math.Abs(endOffset - 10) < 0.001,
            $"expected end offset 10, got {endOffset}");

        return "ShareX-Mod semantic range-anchor self-tests passed: 1";
    }

    private static ShareXModRecipeLocator Locator(string id, string text, double y) =>
        new(
            "ARTICLE", id, id, "article", text, string.Empty, text,
            string.Empty, string.Empty, string.Empty,
            20, y, 900, 120,
            ShareXModCaptureRecipeCompiler.LocatorFingerprint(
                "ARTICLE", id, id, "article", text, string.Empty, text, string.Empty));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ShareX-Mod anchor self-test failed: " + message);
    }
}
