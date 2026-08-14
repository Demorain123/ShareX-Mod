#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModChromeRecipeCaptureEntry
{
    public static async Task<ShareXModChromeBackgroundCaptureResult?> TryCaptureAsync(
        IntPtr selectedWindowHandle,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        if (!settings.ChromeEnhancedEnabled ||
            !settings.CaptureRecipeAutomationEnabled ||
            string.IsNullOrWhiteSpace(settings.CaptureRecipeReplayPath) ||
            selectedWindowHandle == IntPtr.Zero)
        {
            return null;
        }

        string processName = GetProcessName(selectedWindowHandle);
        if (!IsChromiumProcess(processName))
        {
            return null;
        }

        string windowTitle = GetWindowTitle(selectedWindowHandle);
        ShareXModChromeCdpClient client = new();

        try
        {
            IReadOnlyList<ShareXModChromeTarget> targets =
                await client.ListTargetsAsync(settings.ChromeCdpEndpoint);

            ShareXModChromeTarget? target = SelectBestTarget(targets, windowTitle);
            if (target == null)
            {
                return null;
            }

            await client.ConnectAsync(target);

            return await ShareXModCaptureRecipeRunner.RunAsync(
                client,
                target,
                settings,
                shouldStop);
        }
        catch
        {
            return null;
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static ShareXModChromeTarget? SelectBestTarget(
        IReadOnlyList<ShareXModChromeTarget> targets,
        string windowTitle)
    {
        if (targets.Count == 0) return null;
        if (targets.Count == 1) return targets[0];

        string normalizedWindow = NormalizeTitle(windowTitle);
        ShareXModChromeTarget? exact = targets.FirstOrDefault(
            t => NormalizeTitle(t.Title) == normalizedWindow);
        if (exact != null) return exact;

        return targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => new
            {
                Target = t,
                Score = CommonTitleScore(normalizedWindow, NormalizeTitle(t.Title))
            })
            .Where(x => x.Score >= 0.45)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Target)
            .FirstOrDefault();
    }

    private static string NormalizeTitle(string value)
    {
        string result = value.Trim();
        string[] suffixes =
        {
            " - Google Chrome", " – Google Chrome",
            " - Microsoft Edge", " – Microsoft Edge",
            " - Brave", " – Brave",
            " - Vivaldi", " – Vivaldi"
        };

        foreach (string suffix in suffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^suffix.Length].Trim();
            }
        }

        return result;
    }

    private static double CommonTitleScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(a.Length, b.Length) /
                   (double)Math.Max(a.Length, b.Length);
        }

        string[] aw = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] bw = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int common = aw.Count(x => bw.Any(
            y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)));
        return common / (double)Math.Max(1, Math.Max(aw.Length, bw.Length));
    }

    private static bool IsChromiumProcess(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);

    private static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == 0
                ? string.Empty
                : Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        StringBuilder builder = new(length + 1);
        return GetWindowText(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr hWnd,
        StringBuilder lpString,
        int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
