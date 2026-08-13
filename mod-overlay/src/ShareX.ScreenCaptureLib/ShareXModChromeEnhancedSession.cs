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
        this.client = client; Target = target; PreparationSummary = preparationSummary;
    }

    public static async Task<ShareXModChromeEnhancedSession?> TryCreateAsync(IntPtr hwnd, ShareXModV04Settings settings)
    {
        if (!settings.ChromeEnhancedEnabled || hwnd == IntPtr.Zero) return null;
        string processName = GetProcessName(hwnd);
        if (!IsChromiumProcess(processName)) return null;
        string windowTitle = GetWindowTitle(hwnd);
        ShareXModChromeCdpClient client = new();
        try
        {
            IReadOnlyList<ShareXModChromeTarget> targets = await client.ListTargetsAsync(settings.ChromeCdpEndpoint);
            ShareXModChromeTarget? target = SelectBestTarget(targets, windowTitle);
            if (target == null) { await client.DisposeAsync(); return null; }
            await client.ConnectAsync(target);
            using JsonDocument prepared = await client.PreparePageAsync(settings);
            string summary;
            try { summary = prepared.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").ToString(); }
            catch { summary = string.Empty; }
            return new ShareXModChromeEnhancedSession(client, target, summary);
        }
        catch { await client.DisposeAsync(); return null; }
    }

    private static ShareXModChromeTarget? SelectBestTarget(IReadOnlyList<ShareXModChromeTarget> targets, string windowTitle)
    {
        if (targets.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            string nw = NormalizeTitle(windowTitle);
            ShareXModChromeTarget? exact = targets.FirstOrDefault(t => NormalizeTitle(t.Title) == nw);
            if (exact != null) return exact;
            ShareXModChromeTarget? best = targets.Where(t => !string.IsNullOrWhiteSpace(t.Title)).OrderByDescending(t => CommonTitleScore(nw, NormalizeTitle(t.Title))).FirstOrDefault();
            if (best != null && CommonTitleScore(nw, NormalizeTitle(best.Title)) >= 0.45) return best;
        }
        return targets.Count == 1 ? targets[0] : null;
    }

    private static string NormalizeTitle(string value)
    {
        string result = value.Trim();
        string[] suffixes = { " - Google Chrome", " – Google Chrome", " - Microsoft Edge", " – Microsoft Edge", " - Brave", " – Brave" };
        foreach (string suffix in suffixes) if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) result = result[..^suffix.Length].Trim();
        return result;
    }

    private static double CommonTitleScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return Math.Min(a.Length, b.Length) / (double)Math.Max(a.Length, b.Length);
        string[] aw = a.Split(' ', StringSplitOptions.RemoveEmptyEntries), bw = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int common = aw.Count(x => bw.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)));
        return common / (double)Math.Max(1, Math.Max(aw.Length, bw.Length));
    }

    private static bool IsChromiumProcess(string p) => p.Equals("chrome", StringComparison.OrdinalIgnoreCase) || p.Equals("msedge", StringComparison.OrdinalIgnoreCase) || p.Equals("brave", StringComparison.OrdinalIgnoreCase) || p.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);
    private static string GetProcessName(IntPtr hwnd) { try { GetWindowThreadProcessId(hwnd, out uint pid); return pid == 0 ? string.Empty : Process.GetProcessById((int)pid).ProcessName; } catch { return string.Empty; } }
    private static string GetWindowTitle(IntPtr hwnd) { int len=GetWindowTextLength(hwnd); if(len<=0)return string.Empty; StringBuilder b=new(len+1); return GetWindowText(hwnd,b,b.Capacity)>0?b.ToString():string.Empty; }

    public async ValueTask DisposeAsync() { try { await client.RestorePageAsync(); } catch { } await client.DisposeAsync(); }

    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", SetLastError=true)] private static extern int GetWindowTextLength(IntPtr hWnd);
}
