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

    // v0.1.5: a fixed strategy maps the GUI preset to constant parameters;
    // adaptive strategies start near their target speed and downshift on real
    // loading/layout/seam evidence before gradually recovering.
    public BrowserAgentSpeedStrategy SpeedStrategy { get; init; } = BrowserAgentSpeedStrategy.AdaptiveBalanced;
    public BrowserAgentRepairPrecision RepairPrecision { get; init; } = BrowserAgentRepairPrecision.Medium;

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
            RepairPrecision = Enum.IsDefined(RepairPrecision) ? RepairPrecision : BrowserAgentRepairPrecision.Medium
        };
    }
}
