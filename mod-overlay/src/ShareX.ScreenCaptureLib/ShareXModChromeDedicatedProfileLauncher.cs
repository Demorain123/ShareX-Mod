#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModChromeDedicatedProfileResult(
    bool Started,
    string Endpoint,
    string ProfileDirectory,
    string Executable,
    string Detail);

internal static class ShareXModChromeDedicatedProfileLauncher
{
    public static string ResolveProfileDirectory(ShareXModV04Settings settings)
    {
        string configured = settings.ChromeDedicatedProfileDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "ShareX-Mod\\ChromeCaptureProfile";
        }

        try
        {
            string path = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));

            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX-Mod",
                "ChromeCaptureProfile");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string? TryGetActiveEndpoint(ShareXModV04Settings settings)
    {
        try
        {
            string profile = ResolveProfileDirectory(settings);
            string activePort = Path.Combine(profile, "DevToolsActivePort");
            if (!File.Exists(activePort)) return null;

            string? first = File.ReadLines(activePort).FirstOrDefault();
            if (!int.TryParse(first, out int port) || port <= 0 || port > 65535)
            {
                return null;
            }

            return $"http://127.0.0.1:{port}";
        }
        catch
        {
            return null;
        }
    }

    public static async Task<ShareXModChromeDedicatedProfileResult> LaunchAsync(
        ShareXModV04Settings settings)
    {
        string profile = ResolveProfileDirectory(settings);
        string? existing = TryGetActiveEndpoint(settings);

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return new ShareXModChromeDedicatedProfileResult(
                true,
                existing,
                profile,
                string.Empty,
                "Dedicated Capture Browser already exposes a DevTools endpoint.");
        }

        string? executable = FindBrowserExecutable(settings.ChromeExecutablePath);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new ShareXModChromeDedicatedProfileResult(
                false,
                string.Empty,
                profile,
                string.Empty,
                "Chrome/Edge executable was not found. Set chromeExecutablePath in advanced settings.");
        }

        try
        {
            string activePort = Path.Combine(profile, "DevToolsActivePort");
            try
            {
                if (File.Exists(activePort)) File.Delete(activePort);
            }
            catch
            {
            }

            ProcessStartInfo start = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            };

            start.ArgumentList.Add("--remote-debugging-port=0");
            start.ArgumentList.Add($"--user-data-dir={profile}");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("about:blank");

            Process? process = Process.Start(start);
            if (process == null)
            {
                return new ShareXModChromeDedicatedProfileResult(
                    false,
                    string.Empty,
                    profile,
                    executable,
                    "Browser process could not be started.");
            }

            int timeoutMs = Math.Clamp(
                settings.ChromeDedicatedProfileLaunchTimeoutMs,
                2000,
                30000);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(120);
                string? endpoint = TryGetActiveEndpoint(settings);
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    return new ShareXModChromeDedicatedProfileResult(
                        true,
                        endpoint,
                        profile,
                        executable,
                        "Dedicated Capture Browser started. This is a separate persistent profile, not the default Chrome profile.");
                }

                if (process.HasExited)
                {
                    break;
                }
            }

            return new ShareXModChromeDedicatedProfileResult(
                false,
                string.Empty,
                profile,
                executable,
                "Browser launched but DevToolsActivePort was not available before timeout.");
        }
        catch (Exception ex)
        {
            return new ShareXModChromeDedicatedProfileResult(
                false,
                string.Empty,
                profile,
                executable,
                ex.Message);
        }
    }

    private static string? FindBrowserExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                string full = Path.GetFullPath(configured.Trim().Trim('"'));
                if (File.Exists(full)) return full;
            }
            catch
            {
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
