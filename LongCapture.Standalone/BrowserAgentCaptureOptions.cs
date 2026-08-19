namespace LongCapture.Standalone;

internal sealed class BrowserAgentCaptureOptions
{
    public int StartDelayMs { get; init; } = 450;
    public int StableWindowMs { get; init; } = 1100;
    public double OverlapRatio { get; init; } = 0.32;

    // v0.1.4: global pre-scan is opt-in. Per-frame lazy-boundary warm-up remains
    // active during the real capture even when this option is disabled.
    public bool PreloadDynamicContent { get; init; } = false;
    public int PreloadMaxSeconds { get; init; } = 90;
    public bool RequireRegionSelection { get; init; } = true;

    // v0.1.5+: fixed strategies map GUI presets to constant parameters while
    // adaptive strategies can change gear using measured loading/layout/seam risk.
    public BrowserAgentSpeedStrategy SpeedStrategy { get; init; } = BrowserAgentSpeedStrategy.AdaptiveBalanced;
    public BrowserAgentRepairPrecision RepairPrecision { get; init; } = BrowserAgentRepairPrecision.Medium;

    // v0.1.6: local calibration is optional and bounded. It only reshapes the
    // existing safe gear ladder; it cannot bypass the Browser Agent safety guards.
    public bool UseLocalCalibration { get; init; } = true;
    public bool ShowLiveAdaptiveMonitor { get; init; } = true;

    public static BrowserAgentCaptureOptions Default => new();

    public BrowserAgentCaptureOptions Normalize()
    {
        return new BrowserAgentCaptureOptions
        {
            StartDelayMs = Math.Clamp(StartDelayMs, 0, 10000),
            StableWindowMs = Math.Clamp(StableWindowMs, 450, 3000),
            OverlapRatio = Math.Clamp(OverlapRatio, 0.20, 0.50),
            PreloadDynamicContent = PreloadDynamicContent,
            PreloadMaxSeconds = Math.Clamp(PreloadMaxSeconds, 10, 180),
            RequireRegionSelection = RequireRegionSelection,
            SpeedStrategy = Enum.IsDefined(SpeedStrategy) ? SpeedStrategy : BrowserAgentSpeedStrategy.AdaptiveBalanced,
            RepairPrecision = Enum.IsDefined(RepairPrecision) ? RepairPrecision : BrowserAgentRepairPrecision.Medium,
            UseLocalCalibration = UseLocalCalibration,
            ShowLiveAdaptiveMonitor = ShowLiveAdaptiveMonitor
        };
    }
}
