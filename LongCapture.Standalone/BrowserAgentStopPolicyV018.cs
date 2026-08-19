namespace LongCapture.Standalone;

internal enum BrowserAgentStopModeV018
{
    AutoPageEnd = 0,
    DomCounter = 1,
    FrameCount = 2,
    ElapsedMinutes = 3
}

internal static class BrowserAgentStopPolicyV018
{
    public static bool TryMatch(
        BrowserAgentCaptureOptions options,
        BrowserAgentFrameRecord frame,
        int acceptedFrameCount,
        TimeSpan elapsed,
        out string stopReason)
    {
        stopReason = string.Empty;
        switch (options.StopMode)
        {
            case BrowserAgentStopModeV018.AutoPageEnd:
                return false;

            case BrowserAgentStopModeV018.DomCounter:
                if (frame.PageCounterCurrent > 0 && frame.PageCounterCurrent >= options.StopValue)
                {
                    stopReason = "requested-dom-counter";
                    return true;
                }
                return false;

            case BrowserAgentStopModeV018.FrameCount:
                if (acceptedFrameCount >= options.StopValue)
                {
                    stopReason = "requested-frame-count";
                    return true;
                }
                return false;

            case BrowserAgentStopModeV018.ElapsedMinutes:
                if (elapsed >= TimeSpan.FromMinutes(options.StopValue))
                {
                    stopReason = "requested-elapsed-minutes";
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    public static bool IsRequestedEnd(string? stopReason) =>
        string.Equals(stopReason, "requested-dom-counter", StringComparison.Ordinal) ||
        string.Equals(stopReason, "requested-frame-count", StringComparison.Ordinal) ||
        string.Equals(stopReason, "requested-elapsed-minutes", StringComparison.Ordinal);

    public static string Describe(BrowserAgentStopModeV018 mode, int value) => mode switch
    {
        BrowserAgentStopModeV018.AutoPageEnd => "Auto · confirmed page end / F8",
        BrowserAgentStopModeV018.DomCounter => $"DOM progress ≥ {value}",
        BrowserAgentStopModeV018.FrameCount => $"Frames ≥ {value}",
        BrowserAgentStopModeV018.ElapsedMinutes => $"Elapsed ≥ {value} minute(s)",
        _ => "Auto · confirmed page end / F8"
    };

    internal static bool SelfTest()
    {
        var frame = new BrowserAgentFrameRecord { PageCounterCurrent = 207, PageCounterTotal = 320 };
        var dom = new BrowserAgentCaptureOptions { StopMode = BrowserAgentStopModeV018.DomCounter, StopValue = 207 }.Normalize();
        if (!TryMatch(dom, frame, 20, TimeSpan.FromSeconds(20), out string domReason) || domReason != "requested-dom-counter") return false;

        var domFuture = new BrowserAgentCaptureOptions { StopMode = BrowserAgentStopModeV018.DomCounter, StopValue = 208 }.Normalize();
        if (TryMatch(domFuture, frame, 20, TimeSpan.FromSeconds(20), out _)) return false;

        var frames = new BrowserAgentCaptureOptions { StopMode = BrowserAgentStopModeV018.FrameCount, StopValue = 40 }.Normalize();
        if (!TryMatch(frames, frame, 40, TimeSpan.Zero, out string frameReason) || frameReason != "requested-frame-count") return false;

        var elapsed = new BrowserAgentCaptureOptions { StopMode = BrowserAgentStopModeV018.ElapsedMinutes, StopValue = 3 }.Normalize();
        if (!TryMatch(elapsed, frame, 1, TimeSpan.FromMinutes(3.1), out string timeReason) || timeReason != "requested-elapsed-minutes") return false;

        var auto = new BrowserAgentCaptureOptions { StopMode = BrowserAgentStopModeV018.AutoPageEnd }.Normalize();
        return !TryMatch(auto, frame, 1000, TimeSpan.FromHours(1), out _) &&
               IsRequestedEnd(domReason) && IsRequestedEnd(frameReason) && IsRequestedEnd(timeReason);
    }
}
