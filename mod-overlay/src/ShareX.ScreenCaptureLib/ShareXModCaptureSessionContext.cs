#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModCaptureSessionContext : IDisposable
{
    private static readonly AsyncLocal<ShareXModCaptureSessionContext?> CurrentSlot = new();

    private readonly object sync = new();
    private readonly Dictionary<string, string> components = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset created = DateTimeOffset.Now;
    private bool completed;
    private bool disposed;

    public string SessionId { get; }
    public string RootDirectory { get; }
    public Rectangle CaptureRectangle { get; }

    public static string? CurrentSessionId => CurrentSlot.Value?.SessionId;

    private ShareXModCaptureSessionContext(Rectangle captureRectangle)
    {
        CaptureRectangle = captureRectangle;
        SessionId = $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}-{Guid.NewGuid():N}";
        RootDirectory = CreateRootDirectory(SessionId);
        WriteManifest(final: false, endReason: null, status: null, result: null);
    }

    public static ShareXModCaptureSessionContext? Begin(Rectangle captureRectangle)
    {
        try
        {
            ShareXModCaptureSessionContext context = new(captureRectangle);
            CurrentSlot.Value = context;
            return context;
        }
        catch
        {
            return null;
        }
    }

    public static void RegisterComponent(string name, string directory)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        CurrentSlot.Value?.Register(name, directory);
    }

    public static string? TryGetComponentDirectory(string name)
    {
        ShareXModCaptureSessionContext? context = CurrentSlot.Value;
        if (context == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (context.sync)
        {
            return context.components.TryGetValue(name, out string? value) ? value : null;
        }
    }

    public void Complete(string endReason, ScrollingCaptureStatus status, Bitmap? result)
    {
        if (disposed || completed)
        {
            return;
        }

        completed = true;
        WriteManifest(final: true, endReason, status.ToString(), result);
    }

    private void Register(string name, string directory)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            directory = Path.GetFullPath(directory);
        }
        catch
        {
        }

        lock (sync)
        {
            components[name] = directory;
            WriteManifest(final: completed, endReason: null, status: null, result: null);
        }
    }

    private void WriteManifest(bool final, string? endReason, string? status, Bitmap? result)
    {
        try
        {
            Dictionary<string, string> snapshot;
            lock (sync)
            {
                snapshot = components
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            }

            string path = Path.Combine(RootDirectory, "session-manifest.json");
            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Capture Session",
                version = "0.4.4-dev",
                sessionId = SessionId,
                final,
                created,
                updated = DateTimeOffset.Now,
                endReason,
                status,
                processId = Environment.ProcessId,
                captureRectangle = new
                {
                    CaptureRectangle.X,
                    CaptureRectangle.Y,
                    CaptureRectangle.Width,
                    CaptureRectangle.Height
                },
                result = result == null ? null : new
                {
                    width = result.Width,
                    height = result.Height
                },
                components = snapshot
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
            // Session correlation is advisory and must never invalidate the main capture.
        }
    }

    private static string CreateRootDirectory(string sessionId)
    {
        string preferred = Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "CaptureSessions",
            sessionId);

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
                "CaptureSessions",
                sessionId);

            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (!completed)
        {
            WriteManifest(final: false, endReason: "disposed-before-complete", status: null, result: null);
        }

        if (ReferenceEquals(CurrentSlot.Value, this))
        {
            CurrentSlot.Value = null;
        }
    }
}
