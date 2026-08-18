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
}

internal sealed class BrowserAgentSessionManifest
{
    public string ProtocolVersion { get; set; } = "0.1";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Status { get; set; } = "capturing";
    public string? FinalImage { get; set; }
    public string? StopReason { get; set; }
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
}
