#nullable enable

using System;
using System.IO;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModV04Settings
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

    // Capture Boundary keeps the existing manual Start/Stop workflow as the default.
    // Any numeric limit <= 0 is disabled.
    public bool CaptureBoundaryEnabled { get; set; } = true;
    public int CaptureBoundaryMaxSteps { get; set; } = 0;
    public int CaptureBoundaryMaxDurationSeconds { get; set; } = 0;
    public long CaptureBoundaryMaxLogicalHeight { get; set; } = 0;
    public int CaptureBoundaryMaxUnchangedPasses { get; set; } = 0;
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
        string path = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.v04.json");
        if (!File.Exists(path))
        {
            return new ShareXModV04Settings();
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ShareXModV04Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new ShareXModV04Settings();
        }
        catch
        {
            return new ShareXModV04Settings();
        }
    }
}
