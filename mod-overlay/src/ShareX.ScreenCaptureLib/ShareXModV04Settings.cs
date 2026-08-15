#nullable enable

using System;
using System.IO;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed partial class ShareXModV04Settings
{
    public bool SmartSegmentationEnabled { get; set; } = true;
    public int SegmentTargetHeight { get; set; } = 32000;
    public int SegmentMaxWorkingHeight { get; set; } = 42000;
    public int SegmentSearchRadius { get; set; } = 2400;
    public int SegmentMinimumCutDistance { get; set; } = 12000;
    public int SegmentPreviewMaxHeight { get; set; } = 16000;
    public string SegmentedOutputDirectory { get; set; } = "ShareX-Mod\\SegmentedCaptures";

    public bool AdaptiveSettleEnabled { get; set; } = true;
    public int SettleMinimumDelayMs { get; set; } = 180;
    public int SettleProbeIntervalMs { get; set; } = 120;
    public int SettleRequiredStableProbes { get; set; } = 2;
    public int SettleMaximumWaitMs { get; set; } = 3000;
    public int SettleProbeWidth { get; set; } = 96;
    public int SettleProbeHeight { get; set; } = 54;
    public double SettleMeanDifferenceThreshold { get; set; } = 3.2;
    public double SettleBlankComplexityThreshold { get; set; } = 2.0;
    public int SettleBlankExtraWaitMs { get; set; } = 500;

    public bool CaptureBoundaryEnabled { get; set; } = true;
    public int CaptureBoundaryMaxSteps { get; set; } = 0;
    public int CaptureBoundaryMaxDurationSeconds { get; set; } = 0;
    public long CaptureBoundaryMaxLogicalHeight { get; set; } = 0;
    public int CaptureBoundaryMaxUnchangedPasses { get; set; } = 0;

    public bool CaptureRecipeRecordingEnabled { get; set; } = false;
    public int CaptureRecipePollIntervalMs { get; set; } = 400;
    public int CaptureRecipeScrollDebounceMs { get; set; } = 220;
    public int CaptureRecipeMaxRawEvents { get; set; } = 12000;
    public int CaptureRecipeMinimumVerticalRangeCss { get; set; } = 160;
    public bool CaptureRecipeAutomationEnabled { get; set; } = false;
    public string CaptureRecipeReplayPath { get; set; } = string.Empty;
    public string CaptureRecipeOutputDirectory { get; set; } = "ShareX-Mod\\CaptureRecipeRuns";
    public int CaptureRecipeReplayScrollStepCss { get; set; } = 900;
    public int CaptureRecipeActionTimeoutMs { get; set; } = 8000;
    public int CaptureRecipeMaxNavigationCount { get; set; } = 100;

    public bool CaptureRecipeInferDynamicFeed { get; set; } = true;
    public double CaptureRecipeDynamicFeedGrowthThresholdCss { get; set; } = 120;
    public int CaptureRecipeDynamicFeedMaxSteps { get; set; } = 400;
    public int CaptureRecipeDynamicFeedMaxDurationSeconds { get; set; } = 1200;
    public int CaptureRecipeDynamicFeedMaxUnchangedPasses { get; set; } = 4;

    public bool CaptureRecipeHorizontalAppendixEnabled { get; set; } = true;
    public double CaptureRecipeHorizontalAppendixMinOverlapRatio { get; set; } = 0.05;
    public double CaptureRecipeHorizontalAppendixMaxOverlapRatio { get; set; } = 0.45;
    public double CaptureRecipeHorizontalAppendixMaxScore { get; set; } = 18.0;
    public double CaptureRecipeHorizontalAppendixMinGap { get; set; } = 0.8;

    public bool RepairPlanEnabled { get; set; } = true;
    public int RepairPlanMarginPx { get; set; } = 256;
    public int RepairPlanMergeGapPx { get; set; } = 96;

    public bool ChromeEnhancedEnabled { get; set; } = false;
    public string ChromeCdpEndpoint { get; set; } = "http://127.0.0.1:9222";
    public bool ChromeExpandDetails { get; set; } = true;
    public bool ChromeRevealDiscourseSpoilers { get; set; } = true;
    public bool ChromeExpandNestedScrollContainers { get; set; } = true;
    public bool ChromeBackgroundCapture { get; set; } = false;
    public bool ChromeGenericAriaExpansion { get; set; } = false;

    public bool ChromeSemanticMapEnabled { get; set; } = true;
    public int ChromeSemanticMapMaxAnchors { get; set; } = 3500;
    public double ChromeSemanticMapMinWidthCss { get; set; } = 2;
    public double ChromeSemanticMapMinHeightCss { get; set; } = 2;
    public int ChromeSemanticMapTextPreviewChars { get; set; } = 180;

    public bool ChromeLayoutShiftTrackingEnabled { get; set; } = true;
    public int ChromeLayoutShiftMaxEntries { get; set; } = 512;

    public bool ChromeExactRepairCaptureEnabled { get; set; } = true;
    public int ChromeRepairMaxClipHeightCss { get; set; } = 6000;
    public int ChromeRepairSettleMs { get; set; } = 500;
    public int ChromeRepairImageWaitMs { get; set; } = 2200;
    public int ChromeRepairHorizontalMarginCss { get; set; } = 96;

    public int ChromeRepairStabilityQuietMs { get; set; } = 350;
    public int ChromeRepairStabilityMaxWaitMs { get; set; } = 6500;
    public int ChromeRepairStabilityProbeMs { get; set; } = 120;
    public int ChromeRepairImageDecodeBudgetMs { get; set; } = 1200;
    public bool ChromeRepairRequireStableRegion { get; set; } = true;

    public bool ChromeRepairAlignmentEnabled { get; set; } = true;
    public bool ChromeRepairAutoApplyEnabled { get; set; } = false;
    public int ChromeRepairAlignmentSearchRadiusPx { get; set; } = 128;
    public int ChromeRepairAlignmentHorizontalSearchPx { get; set; } = 160;
    public int ChromeRepairAlignmentBandHeightPx { get; set; } = 160;
    public int ChromeRepairAlignmentCoarseSampleStep { get; set; } = 12;
    public int ChromeRepairAlignmentFineSampleStep { get; set; } = 5;
    public double ChromeRepairAlignmentMaxScore { get; set; } = 18.0;
    public double ChromeRepairAlignmentMinUniquenessGap { get; set; } = 1.2;
    public int ChromeRepairMinimumReplaceHeightPx { get; set; } = 64;
    public int ChromeRepairSeamBlendPx { get; set; } = 12;

    public bool ChromeSemanticRepairAugmentEnabled { get; set; } = true;
    public bool ChromeSemanticRepairIncludeLayoutShifts { get; set; } = true;
    public bool ChromeSemanticRepairIncludeAnchorMovement { get; set; } = true;
    public bool ChromeSemanticRepairIgnoreRecentInputLayoutShifts { get; set; } = true;
    public double ChromeSemanticRepairMinShiftCss { get; set; } = 12.0;
    public int ChromeSemanticRepairMaxAdditionalRanges { get; set; } = 64;

    public bool FinalQualitySummaryEnabled { get; set; } = true;
    public double FinalQualityResolvedCoverageRatio { get; set; } = 0.60;

    public double ChromeBackgroundStartY { get; set; } = 0;
    public double ChromeBackgroundEndY { get; set; } = 0;
    public int ChromeBackgroundTileHeight { get; set; } = 2800;
    public int ChromeBackgroundMinimumTileHeight { get; set; } = 1400;
    public int ChromeBackgroundMaximumTileHeight { get; set; } = 3600;
    public bool ChromeBackgroundUseNaturalCuts { get; set; } = true;
    public int ChromeBackgroundCutSearchRadius { get; set; } = 520;
    public int ChromeBackgroundSettleMs { get; set; } = 420;
    public int ChromeBackgroundImageWaitMs { get; set; } = 1800;
    public int ChromeBackgroundStableBottomPasses { get; set; } = 3;
    public int ChromeBackgroundMaxParts { get; set; } = 300;
    public double ChromeBackgroundMaxCssHeight { get; set; } = 750000;
    public string ChromeBackgroundOutputDirectory { get; set; } = "ShareX-Mod\\ChromeBackgroundCaptures";

    public bool ChromeImageAppendixEnabled { get; set; } = true;
    public int ChromeImageAppendixMaxAssets { get; set; } = 40;
    public int ChromeImageAppendixMinRenderedWidth { get; set; } = 220;
    public int ChromeImageAppendixMinRenderedHeight { get; set; } = 120;
    public int ChromeImageAppendixMinNaturalWidth { get; set; } = 400;
    public int ChromeImageAppendixMinNaturalHeight { get; set; } = 240;
    public bool ChromeImageAppendixPreferLargestSrcset { get; set; } = true;
    public bool ChromeImageAppendixIncludeCssBackgrounds { get; set; } = true;

    public static ShareXModV04Settings Load()
    {
        ShareXModV04Settings settings = new();
        string path = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.v04.json");

        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<ShareXModV04Settings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) ?? new ShareXModV04Settings();
            }
            catch
            {
                settings = new ShareXModV04Settings();
            }
        }

        return ShareXModCaptureModeProfile.Apply(settings);
    }
}
