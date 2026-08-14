#nullable enable

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureBoundary
{
    public static ShareXModCaptureBoundarySession? TryCreate(
        ShareXModV04Settings settings,
        Rectangle captureRectangle)
    {
        if (!settings.CaptureBoundaryEnabled) return null;

        try
        {
            return new ShareXModCaptureBoundarySession(settings, captureRectangle);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class ShareXModCaptureBoundarySession : IDisposable
{
    private readonly ShareXModV04Settings settings;
    private readonly Rectangle captureRectangle;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly string directory;

    private int acceptedSteps;
    private int unchangedPasses;
    private long logicalHeight;
    private bool disposed;

    public string? StopReason { get; private set; }
    public bool StopRequested => !string.IsNullOrWhiteSpace(StopReason);

    internal ShareXModCaptureBoundarySession(
        ShareXModV04Settings settings,
        Rectangle captureRectangle)
    {
        this.settings = settings;
        this.captureRectangle = captureRectangle;
        logicalHeight = Math.Max(0, captureRectangle.Height);
        directory = CreateSessionDirectory();

        WriteSnapshot(final: false, status: null, result: null, endReason: null);
    }

    public void OnAppend(int actualGrowth)
    {
        acceptedSteps++;

        if (actualGrowth > 0)
        {
            logicalHeight += actualGrowth;
            unchangedPasses = 0;
        }

        Evaluate();
    }

    public void OnUnchangedFrame()
    {
        unchangedPasses++;
        Evaluate();
    }

    public void OnChangedFrame()
    {
        unchangedPasses = 0;
        EvaluateTimeOnly();
    }

    public void Complete(
        string endReason,
        ScrollingCaptureStatus status,
        Bitmap? result)
    {
        if (disposed) return;

        string resolved = StopReason ?? endReason;
        WriteSnapshot(final: true, status, result, resolved);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopwatch.Stop();
    }

    private void Evaluate()
    {
        if (StopRequested) return;

        if (settings.CaptureBoundaryMaxSteps > 0 &&
            acceptedSteps >= settings.CaptureBoundaryMaxSteps)
        {
            StopReason = "boundary-max-steps";
            return;
        }

        if (settings.CaptureBoundaryMaxLogicalHeight > 0 &&
            logicalHeight >= settings.CaptureBoundaryMaxLogicalHeight)
        {
            StopReason = "boundary-max-logical-height";
            return;
        }

        if (settings.CaptureBoundaryMaxUnchangedPasses > 0 &&
            unchangedPasses >= settings.CaptureBoundaryMaxUnchangedPasses)
        {
            StopReason = "boundary-no-new-content";
            return;
        }

        EvaluateTimeOnly();
    }

    private void EvaluateTimeOnly()
    {
        if (StopRequested) return;

        if (settings.CaptureBoundaryMaxDurationSeconds > 0 &&
            stopwatch.Elapsed.TotalSeconds >= settings.CaptureBoundaryMaxDurationSeconds)
        {
            StopReason = "boundary-max-duration";
        }
    }

    private void WriteSnapshot(
        bool final,
        ScrollingCaptureStatus? status,
        Bitmap? result,
        string? endReason)
    {
        try
        {
            string path = Path.Combine(directory, "capture-boundary.json");
            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Capture Boundary",
                version = "0.4.3-dev",
                final,
                created = DateTimeOffset.Now,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                endReason,
                status = status?.ToString(),
                captureRectangle = new
                {
                    captureRectangle.X,
                    captureRectangle.Y,
                    captureRectangle.Width,
                    captureRectangle.Height
                },
                configured = new
                {
                    settings.CaptureBoundaryMaxSteps,
                    settings.CaptureBoundaryMaxDurationSeconds,
                    settings.CaptureBoundaryMaxLogicalHeight,
                    settings.CaptureBoundaryMaxUnchangedPasses
                },
                observed = new
                {
                    acceptedSteps,
                    unchangedPasses,
                    logicalHeight,
                    stopRequested = StopRequested,
                    stopReason = StopReason
                },
                result = new
                {
                    width = result?.Width ?? 0,
                    height = result?.Height ?? 0
                }
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string CreateSessionDirectory()
    {
        string name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}";

        string preferred = Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "CaptureBoundarySessions",
            name);

        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX-Mod",
                "CaptureBoundarySessions",
                name);

            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
