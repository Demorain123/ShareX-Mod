#nullable enable

namespace ShareX.ScreenCaptureLib;

internal sealed partial class ShareXModV04Settings
{
    public bool CaptureRecipeRequireReviewBeforeAutomation { get; set; } = true;
    public bool CaptureRecipeResumeEnabled { get; set; } = true;
    public string CaptureRecipeResumeJournalPath { get; set; } = string.Empty;

    public bool CaptureRecipePageGuardEnabled { get; set; } = true;
    public double CaptureRecipePageGuardBoundaryShiftCss { get; set; } = 24;
    public int CaptureRecipePageGuardSemanticCountDelta { get; set; } = 2;
    public int CaptureRecipePageGuardImageCountDelta { get; set; } = 1;

    public bool CaptureRecipePageLoopEnabled { get; set; } = true;
    public bool CaptureRecipePageLoopRequireObservedTransition { get; set; } = true;
    public int CaptureRecipePageLoopMaxPages { get; set; } = 100;
    public bool CaptureRecipePageLoopCollectImages { get; set; } = true;
    public int CaptureRecipePageLoopImageMaxPerPage { get; set; } = 40;

    public bool CaptureRecipeAdaptiveTemplateEnabled { get; set; } = true;
    public double CaptureRecipeAdaptiveTemplateMinSimilarity { get; set; } = 0.78;

    // Multi-family selection remains fail-closed. v0.10.3 lets semantic range placeholders
    // contribute weak evidence without ever making them safety-critical like Next Page.
    public bool CaptureRecipeTemplateRouterEnabled { get; set; } = true;
    public int CaptureRecipeTemplateRouterMaxFamilies { get; set; } = 8;
    public double CaptureRecipeTemplateRouterMinSimilarity { get; set; } = 0.78;
    public double CaptureRecipeTemplateRouterMinSelectionScore { get; set; } = 0.55;
    public double CaptureRecipeTemplateRouterMinUniquenessGap { get; set; } = 0.08;
    public bool CaptureRecipeTemplateRouterUseRangeAnchors { get; set; } = true;

    public double CaptureRecipeHorizontalCaptureOverlapRatio { get; set; } = 0.15;
    public int CaptureRecipeHorizontalMaxPanels { get; set; } = 80;

    public bool CaptureIntegrityManifestEnabled { get; set; } = true;
    public bool CaptureIntegrityIncludeNativeAssets { get; set; } = false;
    public int CaptureIntegrityMaxFiles { get; set; } = 10000;

    public bool ChromeAllowNonLoopbackCdp { get; set; } = false;
    public int ChromeConnectionProbeTimeoutMs { get; set; } = 1200;
    public bool ChromeAutoDiscoverLocalCdp { get; set; } = true;
    public int ChromeAutoDiscoverPortStart { get; set; } = 9222;
    public int ChromeAutoDiscoverPortCount { get; set; } = 8;
    public string ChromeDedicatedProfileDirectory { get; set; } = "ShareX-Mod\\ChromeCaptureProfile";
    public string ChromeExecutablePath { get; set; } = string.Empty;
    public int ChromeDedicatedProfileLaunchTimeoutMs { get; set; } = 15000;

    public bool ChromeOverlayDeduplicateEnabled { get; set; } = true;
    public int ChromeOverlayMinimumWidthCss { get; set; } = 48;
    public int ChromeOverlayMinimumHeightCss { get; set; } = 24;
    public int ChromeOverlayMaximumViewportAreaPercent { get; set; } = 35;

    public string ChromeImageAppendixOriginalRetention { get; set; } = "ask";

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
