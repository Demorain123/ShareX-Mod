namespace LongCapture.Standalone;

internal sealed class BrowserAgentCaptureOptions
{
    public int StartDelayMs { get; init; } = 450;
    public int StableWindowMs { get; init; } = 1100;
    public double OverlapRatio { get; init; } = 0.32;
    public bool PreloadDynamicContent { get; init; } = true;
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
