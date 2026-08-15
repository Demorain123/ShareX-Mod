#nullable enable

using System;
using System.IO;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModRobustScrollingSettings
{
    public bool Enabled { get; set; }
    public bool ManualStopOnly { get; set; } = true;
    public bool ContinueAfterCombineFailure { get; set; } = true;
    public int MaxConsecutiveCombineFailures { get; set; } = 0;
    public int MaxConsecutiveUnchangedFrames { get; set; } = 3;
    public bool SaveRawFramesOnFailure { get; set; } = true;
    public int RawFrameHistory { get; set; } = 4;
    public bool DiagnosticsEnabled { get; set; } = true;
    public bool FallbackMatcherEnabled { get; set; } = true;
    public double FallbackMaxMeanDifference { get; set; } = 20.0;
    public int FallbackMinScrollDelta { get; set; } = 12;
    public double FallbackMaxScrollDeltaRatio { get; set; } = 0.90;
    public int FallbackCoarseStep { get; set; } = 8;
    public string SessionDirectory { get; set; } = "ShareX-Mod\\ScrollingCaptureSessions";

    // GDI+ cannot decode PNGs with a single dimension greater than 65,535 pixels.
    // Keep a safety margin so the normal ShareX preview/save path is never asked to handle one.
    public bool OversizedCaptureEnabled { get; set; } = true;
    public int OversizedDimensionThreshold { get; set; } = 60000;
    public int OversizedPreviewMaxWidth { get; set; } = 1600;
    public int OversizedPreviewMaxHeight { get; set; } = 16000;
    public bool OversizedPreferSinglePng { get; set; } = true;
    public int OversizedFallbackPartHeight { get; set; } = 30000;
    public string OversizedOutputDirectory { get; set; } = "ShareX-Mod\\OversizedCaptures";

    public static ShareXModRobustScrollingSettings Load()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("SHAREX_MOD_CONFIG");
        string path = !string.IsNullOrWhiteSpace(explicitPath)
            ? explicitPath
            : Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.json");

        if (!File.Exists(path))
        {
            return new ShareXModRobustScrollingSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            ShareXModRobustScrollingSettings? settings = JsonSerializer.Deserialize<ShareXModRobustScrollingSettings>(json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            return settings ?? new ShareXModRobustScrollingSettings();
        }
        catch
        {
            return new ShareXModRobustScrollingSettings();
        }
    }
}
