namespace LongCapture.Standalone;

internal sealed class BrowserAgentCaptureOptions
{
    public int StartDelayMs { get; init; } = 450;
    public int StableWindowMs { get; init; } = 1100;
    public double OverlapRatio { get; init; } = 0.32;

    // v0.1.4: global pre-scan is intentionally opt-in. The real v0.1.3 Linux.do
    // evidence showed that an unconditional bottom-seeking preload looked like a
    // runaway capture and could outlive a desktop F8 cancellation. Per-frame
    // lazy-boundary warm-up remains enabled independently in the Browser Agent.
    public bool PreloadDynamicContent { get; init; } = false;
    public int PreloadMaxSeconds { get; init; } = 90;
    public bool RequireRegionSelection { get; init; } = true;

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
            RequireRegionSelection = RequireRegionSelection
        };
    }
}
