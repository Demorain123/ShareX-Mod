using System.IO.Compression;
using System.Text.Json;

namespace LongCapture.Standalone;

internal static class BrowserAgentDiagnosticsExporter
{
    public static string Export(string sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || !Directory.Exists(sessionDirectory))
        {
            throw new DirectoryNotFoundException("Browser Agent session directory was not found.");
        }

        string capturesRoot = Directory.GetParent(sessionDirectory)?.FullName ?? AppContext.BaseDirectory;
        string diagnosticsDirectory = Path.Combine(capturesRoot, "Diagnostics");
        Directory.CreateDirectory(diagnosticsDirectory);

        string outputPath = Path.Combine(
            diagnosticsDirectory,
            $"BrowserAgentDiagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        DateTime startedUtc = Directory.GetCreationTimeUtc(sessionDirectory);
        DateTime completedUtc = DateTime.UtcNow;
        string sessionJson = Path.Combine(sessionDirectory, "session.json");
        if (File.Exists(sessionJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sessionJson));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("StartedUtc", out JsonElement started) &&
                    started.TryGetDateTime(out DateTime parsedStarted))
                {
                    startedUtc = parsedStarted.ToUniversalTime();
                }
                if (root.TryGetProperty("CompletedUtc", out JsonElement completed) &&
                    completed.ValueKind == JsonValueKind.String &&
                    completed.TryGetDateTime(out DateTime parsedCompleted))
                {
                    completedUtc = parsedCompleted.ToUniversalTime();
                }
            }
            catch (Exception ex)
            {
                LongCaptureLog.Warn($"Browser Agent diagnostics could not parse session timestamps: {LongCaptureLog.OneLine(ex.Message)}");
            }
        }

        using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(sessionDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sessionDirectory, file);
                archive.CreateEntryFromFile(file, Path.Combine("session", relative), CompressionLevel.Optimal);
            }

            DateTime logStart = startedUtc.AddMinutes(-5);
            DateTime logEnd = completedUtc.AddMinutes(5);
            if (Directory.Exists(LongCaptureLog.LogDirectory))
            {
                foreach (string log in Directory.EnumerateFiles(LongCaptureLog.LogDirectory, "LongCapture-*.log", SearchOption.TopDirectoryOnly))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(log);
                    if (modified >= logStart && modified <= logEnd)
                    {
                        archive.CreateEntryFromFile(log, Path.Combine("logs", Path.GetFileName(log)), CompressionLevel.Optimal);
                    }
                }
            }

            ZipArchiveEntry readme = archive.CreateEntry("README.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(readme.Open());
            writer.WriteLine("LongCapture Browser Agent v0.1.1 diagnostics");
            writer.WriteLine($"Session: {sessionDirectory}");
            writer.WriteLine($"Exported UTC: {DateTime.UtcNow:O}");
            writer.WriteLine("Contains the Browser Agent session evidence plus LongCapture logs overlapping the capture time.");
        }

        LongCaptureLog.Info($"Browser Agent diagnostics exported path={LongCaptureLog.OneLine(outputPath)}");
        return outputPath;
    }
}
