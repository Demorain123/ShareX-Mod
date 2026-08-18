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

    public int StabilityWaitMs { get; set; }
    public bool StabilityTimedOut { get; set; }
    public int StabilityMutationCount { get; set; }
    public int StabilityResizeCount { get; set; }
    public int StabilityHeightChangeCount { get; set; }
    public int StabilityPendingImages { get; set; }
    public double StabilityHeightGrowthCss { get; set; }
    public bool LazyWarmupTriggered { get; set; }
    public double LazyWarmupGrowthCss { get; set; }
    public bool CaptureStateChanged { get; set; }

    public bool OverlapVerified { get; set; }
    public double OverlapMeanAbsoluteError { get; set; }
    public double OverlapStrongDiffRatio { get; set; }
    public int OverlapPixels { get; set; }
    public int RecoveryGeneration { get; set; }
    public string OverlapStatus { get; set; } = string.Empty;

    // v0.1.2: progress / true-end evidence. The DOM counter is only a hint;
    // geometry + repeated stable-bottom confirmation remains authoritative.
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
    public string ProtocolVersion { get; set; } = "0.1.2";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Status { get; set; } = "capturing";
    public string? FinalImage { get; set; }
    public string? StopReason { get; set; }
    public string? Error { get; set; }
    public int SafetyFrameLimit { get; set; }
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
