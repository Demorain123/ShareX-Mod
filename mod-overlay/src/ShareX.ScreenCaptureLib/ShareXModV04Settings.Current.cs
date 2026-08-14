#nullable enable

namespace ShareX.ScreenCaptureLib;

internal sealed partial class ShareXModV04Settings
{
    // Capture Recipe review / resume. Automation still remains globally opt-in via
    // CaptureRecipeAutomationEnabled; these defaults make the safety layers active once enabled.
    public bool CaptureRecipeRequireReviewBeforeAutomation { get; set; } = true;
    public bool CaptureRecipeResumeEnabled { get; set; } = true;
    public string CaptureRecipeResumeJournalPath { get; set; } = string.Empty;

    // Revalidate a page before navigation so late-loading content is locally repaired while
    // the source DOM is still available.
    public bool CaptureRecipePageGuardEnabled { get; set; } = true;
    public double CaptureRecipePageGuardBoundaryShiftCss { get; set; } = 24;
    public int CaptureRecipePageGuardSemanticCountDelta { get; set; } = 2;
    public int CaptureRecipePageGuardImageCountDelta { get; set; } = 1;

    // A loop is only eligible after an observed transition and a separate SHA-bound approval.
    public bool CaptureRecipePageLoopEnabled { get; set; } = true;
    public bool CaptureRecipePageLoopRequireObservedTransition { get; set; } = true;
    public int CaptureRecipePageLoopMaxPages { get; set; } = 100;
    public bool CaptureRecipePageLoopCollectImages { get; set; } = true;
    public int CaptureRecipePageLoopImageMaxPerPage { get; set; } = 40;

    // Adaptive templates are candidates only; execution still passes review and semantic
    // re-resolution on every page.
    public bool CaptureRecipeAdaptiveTemplateEnabled { get; set; } = true;
    public double CaptureRecipeAdaptiveTemplateMinSimilarity { get; set; } = 0.78;

    // Multi-family router for demonstrations such as A/B/A/C. A family is never selected when
    // semantic evidence is ambiguous; the runner must stop instead of falling back to coordinates.
    public bool CaptureRecipeTemplateRouterEnabled { get; set; } = true;
    public int CaptureRecipeTemplateRouterMaxFamilies { get; set; } = 8;
    public double CaptureRecipeTemplateRouterMinSimilarity { get; set; } = 0.78;
    public double CaptureRecipeTemplateRouterMinSelectionScore { get; set; } = 0.55;
    public double CaptureRecipeTemplateRouterMinUniquenessGap { get; set; } = 0.08;

    public double CaptureRecipeHorizontalCaptureOverlapRatio { get; set; } = 0.15;
    public int CaptureRecipeHorizontalMaxPanels { get; set; } = 80;

    // Chrome connection broker. Non-loopback CDP is denied by default.
    public bool ChromeAllowNonLoopbackCdp { get; set; } = false;
    public int ChromeConnectionProbeTimeoutMs { get; set; } = 1200;
    public bool ChromeAutoDiscoverLocalCdp { get; set; } = true;
    public int ChromeAutoDiscoverPortStart { get; set; } = 9222;
    public int ChromeAutoDiscoverPortCount { get; set; } = 8;
    public string ChromeDedicatedProfileDirectory { get; set; } = "ShareX-Mod\\ChromeCaptureProfile";
    public string ChromeExecutablePath { get; set; } = string.Empty;
    public int ChromeDedicatedProfileLaunchTimeoutMs { get; set; } = 15000;

    // Fixed/sticky overlays are hidden only during repeated tiles and restored afterwards.
    public bool ChromeOverlayDeduplicateEnabled { get; set; } = true;
    public int ChromeOverlayMinimumWidthCss { get; set; } = 48;
    public int ChromeOverlayMinimumHeightCss { get; set; } = 24;
    public int ChromeOverlayMaximumViewportAreaPercent { get; set; } = 35;

    // Keep/delete/ask. "ask" is fail-safe: originals remain until the user explicitly deletes.
    public string ChromeImageAppendixOriginalRetention { get; set; } = "ask";

    // Infinite-feed preservation and local recapture guard.
    public bool DynamicFeedCollectImages { get; set; } = true;
    public int DynamicFeedImageMaxPerSnapshot { get; set; } = 40;
    public bool DynamicFeedRollingGuardEnabled { get; set; } = true;
    public int DynamicFeedGuardMaxPendingRanges { get; set; } = 8;
    public double DynamicFeedGuardDelayRangeRatio { get; set; } = 0.75;
    public double DynamicFeedGuardMaxAgeRangeRatio { get; set; } = 2.5;
    public int DynamicFeedGuardAuditHistoryRanges { get; set; } = 48;
    public int DynamicFeedGuardSemanticCountDelta { get; set; } = 2;
    public int DynamicFeedGuardImageCountDelta { get; set; } = 1;
}
