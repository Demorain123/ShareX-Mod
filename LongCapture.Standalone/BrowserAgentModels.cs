namespace LongCapture.Standalone;

internal sealed class BrowserAgentFrameRecord
{
    public int Sequence { get; set; }
    public string FileName { get; set; } = string.Empty;
    public double ScrollYCss { get; set; }
    public double ScrollYAfterCss { get; set; }
    public double ScrollHeightCss { get; set; }
    public double ViewportWidthCss { get; set; }
    public double ViewportHeightCss { get; set; }
    public double DevicePixelRatio { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int FixedCandidateCount { get; set; }
    public int HiddenCount { get; set; }
    public bool AtBottom { get; set; }
    public string StateHash { get; set; } = string.Empty;
    public DateTime CapturedUtc { get; set; }

    public double CaptureRegionLeftCss { get; set; }
    public double CaptureRegionTopCss { get; set; }
    public double CaptureRegionWidthCss { get; set; }
    public double CaptureRegionHeightCss { get; set; }

    public int StabilityWaitMs { get; set; }
    public int StabilityQuietMs { get; set; }
    public int StabilityActivityMs { get; set; }
    public int StabilityMaxFalseQuietMs { get; set; }
    public bool StabilityTimedOut { get; set; }
    public int StabilityMutationCount { get; set; }
    public int StabilityResizeCount { get; set; }
    public int StabilityHeightChangeCount { get; set; }
    public int StabilityPendingImages { get; set; }
    public double StabilityHeightGrowthCss { get; set; }
    public int StabilityLayoutShiftCount { get; set; }
    public double StabilityLayoutShiftScore { get; set; }
    public bool LazyWarmupTriggered { get; set; }
    public double LazyWarmupGrowthCss { get; set; }
    public bool CaptureStateChanged { get; set; }
    public int CaptureVisibleTabMs { get; set; }
    public int CaptureThrottleWaitMs { get; set; }

    public bool OverlapVerified { get; set; }
    public double OverlapMeanAbsoluteError { get; set; }
    public double OverlapStrongDiffRatio { get; set; }
    public int OverlapPixels { get; set; }
    public int RecoveryGeneration { get; set; }
    public string OverlapStatus { get; set; } = string.Empty;

    // v0.1.3: DOM geometry predicts the movement, but the accepted compositor delta
    // is verified against the real PNG overlap and can correct small scroll-anchor drift.
    public int ExpectedDeltaPixels { get; set; }
    public int ResolvedDeltaPixels { get; set; }
    public int VisualDeltaOffsetPixels { get; set; }
    public double VisualAlignmentScore { get; set; }
    public double VisualAlignmentConfidence { get; set; }

    // v0.1.8: edge-band evidence catches sticky/fixed contamination that a center/
    // median overlap score can miss (for example Discourse sticky avatars).
    public double OverlapLeftBandMae { get; set; }
    public double OverlapCenterBandMae { get; set; }
    public double OverlapRightBandMae { get; set; }
    public double OverlapLeftBandStrongRatio { get; set; }
    public double OverlapCenterBandStrongRatio { get; set; }
    public double OverlapRightBandStrongRatio { get; set; }
    public bool OverlapLeftEdgeContamination { get; set; }
    public bool OverlapRightEdgeContamination { get; set; }

    // Background-window evidence. captureVisibleTab still requires this to be the
    // active tab in its own browser window; the browser window itself may be behind
    // another desktop application, but minimized capture is rejected fail-closed.
    public bool TargetWindowFocused { get; set; }
    public string TargetWindowState { get; set; } = string.Empty;
    public bool BackgroundWindowCapture { get; set; }

    // v0.1.5+: adaptive evidence is auditable in session.json.
    public int EffectiveStartDelayMs { get; set; }
    public int EffectiveStableWindowMs { get; set; }
    public int EffectiveMaxWaitMs { get; set; }
    public double EffectiveOverlapRatio { get; set; }
    public int AdaptiveRiskScore { get; set; }
    public string AdaptiveRiskReasons { get; set; } = string.Empty;
    public string AdaptiveSpeedBefore { get; set; } = string.Empty;
    public string AdaptiveSpeedAfter { get; set; } = string.Empty;
    public bool RepairCandidate { get; set; }
    public int RepairAttempts { get; set; }
    public string RepairStatus { get; set; } = string.Empty;
    public double CalibrationConfidence { get; set; }

    // v0.1.2+: progress / true-end evidence. The DOM counter is only a hint;
    // geometry + repeated stable-bottom confirmation remains authoritative for full-page mode.
    public int PageCounterCurrent { get; set; }
    public int PageCounterTotal { get; set; }
    public string PageCounterText { get; set; } = string.Empty;
    public int EstimatedFramesToLoadedEnd { get; set; }
    public bool EndConfirmed { get; set; }
    public int EndConfirmationRounds { get; set; }
    public double EndConfirmationGrowthCss { get; set; }
    public bool EndCounterIncomplete { get; set; }
    public string EndConfidence { get; set; } = string.Empty;
}

internal sealed class BrowserAgentSessionManifest
{
    public string ProtocolVersion { get; set; } = "0.1.8";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Status { get; set; } = "capturing";
    public string? FinalImage { get; set; }
    public string? StopReason { get; set; }
    public string? Error { get; set; }
    public int SafetyFrameLimit { get; set; }

    public int StartDelayMs { get; set; }
    public int StableWindowMs { get; set; }
    public double OverlapRatio { get; set; }
    public string SpeedStrategy { get; set; } = BrowserAgentSpeedStrategy.AdaptiveBalanced.ToString();
    public string RepairPrecision { get; set; } = BrowserAgentRepairPrecision.Medium.ToString();
    public bool CalibrationEnabled { get; set; }
    public double CalibrationConfidenceStart { get; set; }
    public double CalibrationConfidenceEnd { get; set; }
    public int CalibrationBenchmarkSamples { get; set; }
    public int CalibrationFrameSamplesStart { get; set; }
    public int CalibrationFrameSamplesEnd { get; set; }
    public int AdaptiveRepairCandidates { get; set; }
    public int AdaptiveRepairAttempts { get; set; }
    public int AdaptiveRepairSuccesses { get; set; }
    public int AdaptiveRepairFailures { get; set; }
    public bool RegionSelectionRequired { get; set; }
    public double CaptureRegionLeftCss { get; set; }
    public double CaptureRegionTopCss { get; set; }
    public double CaptureRegionWidthCss { get; set; }
    public double CaptureRegionHeightCss { get; set; }

    public string RequestedStopMode { get; set; } = BrowserAgentStopModeV018.AutoPageEnd.ToString();
    public int RequestedStopValue { get; set; }
    public bool BackgroundWindowAllowed { get; set; } = true;
    public int BackgroundWindowFrames { get; set; }
    public int EdgeContaminationFrames { get; set; }

    public bool PreloadEnabled { get; set; }
    public bool PreloadReachedEnd { get; set; }
    public int PreloadSteps { get; set; }
    public int PreloadDurationMs { get; set; }
    public double PreloadGrowthCss { get; set; }
    public int PreloadPageCounterCurrent { get; set; }
    public int PreloadPageCounterTotal { get; set; }

    public List<BrowserAgentFrameRecord> Frames { get; set; } = new();
}

internal sealed class BrowserAgentStitchResult
{
    public required string OutputPath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double ScaleX { get; init; }
    public required double ScaleY { get; init; }
    public required int FrameCount { get; init; }
    public bool IsComplete { get; init; }
    public string StopReason { get; init; } = string.Empty;
    public int PageCounterCurrent { get; init; }
    public int PageCounterTotal { get; init; }
}
