using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LongCapture.Standalone;

internal static class LongCaptureLog
{
    private const long MaxLogBytes = 8L * 1024L * 1024L;
    private const int RetainedLogFiles = 20;
    private static readonly object Sync = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static bool initialized;
    private static string sessionId = "pending";
    private static string? currentLogPath;
    private static int segment;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LongCapture",
        "Logs");

    public static string CurrentLogPath
    {
        get
        {
            lock (Sync)
            {
                EnsureInitializedNoThrow();
                return currentLogPath ?? LogDirectory;
            }
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            EnsureInitializedNoThrow();
        }

        Info($"logging initialized path={CurrentLogPath}");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<empty>";
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static void Write(string level, string message, Exception? exception)
    {
        string line = $"{DateTimeOffset.Now:O} [{level}] [session:{sessionId}] [pid:{Environment.ProcessId} tid:{Environment.CurrentManagedThreadId}] {OneLine(message)}";
        string payload = exception is null
            ? line + Environment.NewLine
            : line + Environment.NewLine + exception + Environment.NewLine;

        try
        {
            lock (Sync)
            {
                EnsureInitializedNoThrow();
                RotateIfNeededNoThrow();
                if (!string.IsNullOrWhiteSpace(currentLogPath))
                {
                    File.AppendAllText(currentLogPath, payload, Utf8);
                }
            }
        }
        catch
        {
            // Logging must never become a second failure path while handling a capture error.
        }

        Debug.WriteLine(payload);
    }

    private static void EnsureInitializedNoThrow()
    {
        if (initialized) return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            sessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
            currentLogPath = BuildLogPath(segment: 0);
            initialized = true;
            PruneOldLogsNoThrow();
        }
        catch
        {
            initialized = true;
            currentLogPath = null;
        }
    }

    private static string BuildLogPath(int segment) => Path.Combine(
        LogDirectory,
        segment == 0
            ? $"LongCapture-{sessionId}.log"
            : $"LongCapture-{sessionId}-part{segment:00}.log");

    private static void RotateIfNeededNoThrow()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentLogPath) || !File.Exists(currentLogPath)) return;
            if (new FileInfo(currentLogPath).Length < MaxLogBytes) return;

            segment++;
            currentLogPath = BuildLogPath(segment);
            PruneOldLogsNoThrow();
        }
        catch
        {
            // Best effort only. Keep using the current path when rotation metadata cannot be read.
        }
    }

    private static void PruneOldLogsNoThrow()
    {
        try
        {
            DirectoryInfo directory = new(LogDirectory);
            FileInfo[] stale = directory
                .EnumerateFiles("LongCapture-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(RetainedLogFiles)
                .ToArray();

            foreach (FileInfo file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // A log can be in use by another LongCapture instance; leave it for a later prune pass.
                }
            }
        }
        catch
        {
            // Retention failure must not affect capture functionality.
        }
    }
}
