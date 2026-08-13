#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModRobustScrollingSession : IDisposable
{
    private readonly ShareXModRobustScrollingSettings _settings;
    private readonly Queue<FrameSnapshot> _history = new();
    private readonly HashSet<int> _savedFrames = new();
    private readonly string? _sessionDirectory;
    private readonly StreamWriter? _diagnostics;
    private int _frameIndex;
    private int _consecutiveUnchangedFrames;
    private int _consecutiveCombineFailures;
    private bool _rawCaptureActive;
    private bool _disposed;

    private ShareXModRobustScrollingSession(
        ShareXModRobustScrollingSettings settings,
        Rectangle captureRectangle,
        ScrollingCaptureOptions options)
    {
        _settings = settings;

        if (settings.DiagnosticsEnabled || settings.SaveRawFramesOnFailure)
        {
            _sessionDirectory = CreateSessionDirectory(settings.SessionDirectory);
        }

        if (settings.DiagnosticsEnabled && _sessionDirectory != null)
        {
            try
            {
                _diagnostics = new StreamWriter(
                    Path.Combine(_sessionDirectory, "events.jsonl"),
                    append: false,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                _diagnostics = null;
            }
        }

        Log("session-start", new
        {
            overlayVersion = "0.1.0-dev",
            captureRectangle = new { captureRectangle.X, captureRectangle.Y, captureRectangle.Width, captureRectangle.Height },
            scrollMethod = options.ScrollMethod.ToString(),
            options.ScrollAmount,
            options.ScrollDelay,
            settings.ManualStopOnly,
            settings.ContinueAfterCombineFailure,
            settings.MaxConsecutiveCombineFailures,
            settings.MaxConsecutiveUnchangedFrames,
            settings.FallbackMatcherEnabled
        });
    }

    public static ShareXModRobustScrollingSession? TryCreate(Rectangle captureRectangle, ScrollingCaptureOptions options)
    {
        ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();

        if (!settings.Enabled)
        {
            return null;
        }

        try
        {
            return new ShareXModRobustScrollingSession(settings, captureRectangle, options);
        }
        catch
        {
            return null;
        }
    }

    public void OnFrameCaptured(Bitmap frame)
    {
        _frameIndex++;
        Log("frame-captured", new { frame = _frameIndex, frame.Width, frame.Height });

        if (!_settings.SaveRawFramesOnFailure)
        {
            return;
        }

        if (_rawCaptureActive)
        {
            SaveFrame(_frameIndex, frame);
            return;
        }

        Bitmap clone = (Bitmap)frame.Clone();
        _history.Enqueue(new FrameSnapshot(_frameIndex, clone));

        int historyLimit = Math.Max(1, _settings.RawFrameHistory);
        while (_history.Count > historyLimit)
        {
            _history.Dequeue().Dispose();
        }
    }

    public bool ShouldStopOnUnchangedFrame()
    {
        _consecutiveUnchangedFrames++;
        Log("unchanged-frame", new
        {
            frame = _frameIndex,
            consecutive = _consecutiveUnchangedFrames,
            manualStopOnly = _settings.ManualStopOnly
        });

        if (_settings.ManualStopOnly)
        {
            return false;
        }

        int limit = _settings.MaxConsecutiveUnchangedFrames;
        return limit > 0 && _consecutiveUnchangedFrames >= limit;
    }

    public void OnChangedFrame()
    {
        if (_consecutiveUnchangedFrames > 0)
        {
            Log("scroll-resumed", new { frame = _frameIndex, previousUnchangedCount = _consecutiveUnchangedFrames });
        }

        _consecutiveUnchangedFrames = 0;
    }

    public void OnPrimaryCombineSuccess()
    {
        if (_consecutiveCombineFailures > 0)
        {
            Log("primary-combine-recovered", new { frame = _frameIndex, previousFailures = _consecutiveCombineFailures });
        }

        _consecutiveCombineFailures = 0;
    }

    public Bitmap? TryFallbackCombine(Bitmap? result, Bitmap? previousFrame, Bitmap currentFrame)
    {
        if (!_settings.FallbackMatcherEnabled || result == null || previousFrame == null)
        {
            return null;
        }

        bool matched = ShareXModVerticalFallbackMatcher.TryAppend(
            result,
            previousFrame,
            currentFrame,
            _settings,
            out Bitmap? combined,
            out int delta,
            out double score);

        Log(matched ? "fallback-combine-success" : "fallback-combine-failed", new
        {
            frame = _frameIndex,
            delta,
            score = Math.Round(score, 4),
            threshold = _settings.FallbackMaxMeanDifference
        });

        if (matched)
        {
            _consecutiveCombineFailures = 0;
            return combined;
        }

        combined?.Dispose();
        return null;
    }

    public bool ShouldContinueAfterCombineFailure()
    {
        _consecutiveCombineFailures++;
        ActivateRawCapture("combine-failure");

        int limit = _settings.MaxConsecutiveCombineFailures;
        bool withinLimit = limit <= 0 || _consecutiveCombineFailures <= limit;
        bool shouldContinue = _settings.ContinueAfterCombineFailure && withinLimit;

        Log("combine-failure", new
        {
            frame = _frameIndex,
            consecutive = _consecutiveCombineFailures,
            limit,
            shouldContinue
        });

        return shouldContinue;
    }

    public void Complete(string reason, ScrollingCaptureStatus status, Bitmap? result)
    {
        Log("session-end", new
        {
            reason,
            status = status.ToString(),
            frames = _frameIndex,
            resultWidth = result?.Width ?? 0,
            resultHeight = result?.Height ?? 0,
            rawCaptureActive = _rawCaptureActive,
            sessionDirectory = _sessionDirectory
        });

        if (_sessionDirectory != null)
        {
            try
            {
                string summaryPath = Path.Combine(_sessionDirectory, "summary.json");
                string summary = JsonSerializer.Serialize(new
                {
                    overlayVersion = "0.1.0-dev",
                    reason,
                    status = status.ToString(),
                    frames = _frameIndex,
                    result = new { width = result?.Width ?? 0, height = result?.Height ?? 0 },
                    rawCaptureActive = _rawCaptureActive
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(summaryPath, summary, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    private void ActivateRawCapture(string reason)
    {
        if (_rawCaptureActive || !_settings.SaveRawFramesOnFailure)
        {
            return;
        }

        _rawCaptureActive = true;
        Log("raw-frame-capture-enabled", new { frame = _frameIndex, reason });

        while (_history.Count > 0)
        {
            FrameSnapshot snapshot = _history.Dequeue();
            try
            {
                SaveFrame(snapshot.Index, snapshot.Image);
            }
            finally
            {
                snapshot.Dispose();
            }
        }
    }

    private void SaveFrame(int index, Bitmap frame)
    {
        if (_sessionDirectory == null || !_savedFrames.Add(index))
        {
            return;
        }

        try
        {
            string framesDirectory = Path.Combine(_sessionDirectory, "frames");
            Directory.CreateDirectory(framesDirectory);
            string path = Path.Combine(framesDirectory, $"frame_{index:D5}.png");
            frame.Save(path, ImageFormat.Png);
        }
        catch
        {
        }
    }

    private void Log(string eventName, object payload)
    {
        if (_diagnostics == null)
        {
            return;
        }

        try
        {
            string line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                eventName,
                payload
            });
            _diagnostics.WriteLine(line);
            _diagnostics.Flush();
        }
        catch
        {
        }
    }

    private static string? CreateSessionDirectory(string configuredDirectory)
    {
        string relative = string.IsNullOrWhiteSpace(configuredDirectory)
            ? "ShareX-Mod\\ScrollingCaptureSessions"
            : configuredDirectory;

        string preferredRoot = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(AppContext.BaseDirectory, relative);

        string sessionName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}";

        try
        {
            string preferred = Path.Combine(preferredRoot, sessionName);
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            try
            {
                string fallbackRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShareX-Mod",
                    "ScrollingCaptureSessions");
                string fallback = Path.Combine(fallbackRoot, sessionName);
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        while (_history.Count > 0)
        {
            _history.Dequeue().Dispose();
        }

        _diagnostics?.Dispose();
    }

    private sealed class FrameSnapshot : IDisposable
    {
        public int Index { get; }
        public Bitmap Image { get; }

        public FrameSnapshot(int index, Bitmap image)
        {
            Index = index;
            Image = image;
        }

        public void Dispose() => Image.Dispose();
    }
}
