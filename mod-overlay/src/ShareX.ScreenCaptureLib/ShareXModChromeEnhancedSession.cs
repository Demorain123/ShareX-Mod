#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModChromeEnhancedSession : IAsyncDisposable
{
    private readonly ShareXModChromeCdpClient client;
    public ShareXModChromeTarget Target { get; }
    public string PreparationSummary { get; }

    private ShareXModChromeEnhancedSession(ShareXModChromeCdpClient client, ShareXModChromeTarget target, string preparationSummary)
    {
        this.client = client;
        Target = target;
        PreparationSummary = preparationSummary;
    }

    public static async Task<ShareXModChromeEnhancedSession?> TryCreateAsync(IntPtr selectedWindowHandle, ShareXModV04Settings settings)
    {
        if (!settings.ChromeEnhancedEnabled || selectedWindowHandle == IntPtr.Zero) return null;

        string processName = GetProcessName(selectedWindowHandle);
        if (!IsChromiumProcess(processName)) return null;

        string windowTitle = GetWindowTitle(selectedWindowHandle);
        ShareXModChromeCdpClient client = new();
        try
        {
            IReadOnlyList<ShareXModChromeTarget> targets = await client.ListTargetsAsync(settings.ChromeCdpEndpoint);
            ShareXModChromeTarget? target = SelectBestTarget(targets, windowTitle);
            if (target == null)
            {
                await client.DisposeAsync();
                return null;
            }

            await client.ConnectAsync(target);
            using JsonDocument prepared = await client.PreparePageAsync(settings);
            string summary = ExtractEvaluationValue(prepared);
            return new ShareXModChromeEnhancedSession(client, target, summary);
        }
        catch
        {
            await client.DisposeAsync();
            return null;
        }
    }

    private static ShareXModChromeTarget? SelectBestTarget(IReadOnlyList<ShareXModChromeTarget> targets, string windowTitle)
    {
        if (targets.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            string normalizedWindow = NormalizeTitle(windowTitle);
            ShareXModChromeTarget? exact = targets.FirstOrDefault(t => NormalizeTitle(t.Title) == normalizedWindow);
            if (exact != null) return exact;

            ShareXModChromeTarget? contains = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.Title))
                .OrderByDescending(t => CommonTitleScore(normalizedWindow, NormalizeTitle(t.Title)))
                .FirstOrDefault();
            if (contains != null && CommonTitleScore(normalizedWindow, NormalizeTitle(contains.Title)) >= 0.45) return contains;
        }

        return targets.Count == 1 ? targets[0] : null;
    }

    private static string NormalizeTitle(string value)
    {
        string result = value.Trim();
        string[] suffixes = { " - Google Chrome", " – Google Chrome", " - Microsoft Edge", " – Microsoft Edge", " - Brave", " – Brave" };
        foreach (string suffix in suffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                result = result[..^suffix.Length].Trim();
        }
        return result;
    }

    private static double CommonTitleScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase))
            return Math.Min(a.Length, b.Length) / (double)Math.Max(a.Length, b.Length);

        string[] aw = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] bw = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int common = aw.Count(x => bw.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)));
        return common / (double)Math.Max(1, Math.Max(aw.Length, bw.Length));
    }

    private static string ExtractEvaluationValue(JsonDocument response)
    {
        try
        {
            return response.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").ToString();
        }
        catch { return string.Empty; }
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
            return pid == 0 ? string.Empty : Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return string.Empty; }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        StringBuilder builder = new(length + 1);
        return GetWindowText(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        try { await client.RestorePageAsync(); } catch { }
        await client.DisposeAsync();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
