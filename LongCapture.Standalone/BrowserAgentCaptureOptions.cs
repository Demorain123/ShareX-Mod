namespace LongCapture.Standalone;

internal sealed class BrowserAgentCaptureOptions
{
    public int StartDelayMs { get; init; } = 450;
    public int StableWindowMs { get; init; } = 1100;
    public double OverlapRatio { get; init; } = 0.32;

    // v0.1.4: the global pre-scan is intentionally opt-in. The real v0.1.3
    // Linux.do evidence showed that an unconditional bottom-seeking preload looked
    // like runaway capture and could outlive a desktop-only F8 cancellation. The
    // optional v0.1.4 pre-scan is paced/cancellable; per-frame lazy-boundary warm-up
    // remains enabled independently during the real capture.
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
