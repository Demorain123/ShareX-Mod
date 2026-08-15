using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Optional, dependency-free diagnostics emitted by the scrolling engine.
/// ShareX behavior is unchanged when no sink is supplied. LongCapture uses
/// these events to build a per-capture flight recorder and quality report.
/// </summary>
public enum ScrollingCaptureTelemetryKind
{
    CaptureStarted,
    FrameCaptured,
    ScrollIssued,
    SettleProbe,
    SettleCompleted,
    StitchComputed,
    StationaryOverlayDetected,
    Warning,
    CaptureCompleted
}

public sealed class ScrollingCaptureTelemetryEvent
{
    public ScrollingCaptureTelemetryKind Kind { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public int FrameIndex { get; init; }
    public int ResultHeightBefore { get; init; }
    public int ResultHeightAfter { get; init; }
    public int EstimatedScrollPixels { get; init; }
    public int MatchRows { get; init; }
    public double Confidence { get; init; }
    public int WaitedMilliseconds { get; init; }
    public double ChangedFraction { get; init; }
    public bool TimedOut { get; init; }
    public bool UsedBestGuess { get; init; }
    public int StationaryTileCount { get; init; }
    public int StationaryPixelArea { get; init; }
    public Rectangle CaptureRectangle { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// A captured frame borrowed from the scrolling engine. The bitmap is owned by
/// the engine and is valid only for the duration of the callback. A consumer
/// that needs to retain it must Clone() it synchronously.
/// </summary>
public sealed class ScrollingCaptureFrameEvent
{
    public int FrameIndex { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public Rectangle CaptureRectangle { get; init; }
    public Bitmap Frame { get; init; } = null!;
}
