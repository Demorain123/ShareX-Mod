using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal readonly record struct CaptureExclusionResult(
    IntPtr Handle,
    bool Applied,
    int Win32Error,
    uint? VerifiedAffinity,
    string Role);

internal static class CaptureExclusion
{
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static CaptureExclusionResult Apply(Form form, string role)
    {
        _ = form.Handle;
        return Apply(form.Handle, role);
    }

    public static CaptureExclusionResult Apply(IntPtr hWnd, string role)
    {
        if (hWnd == IntPtr.Zero)
        {
            var missing = new CaptureExclusionResult(hWnd, false, 0, null, role);
            LongCaptureLog.Warn($"capture exclusion skipped role={LongCaptureLog.OneLine(role)} reason=no-window-handle");
            return missing;
        }

        Marshal.SetLastPInvokeError(0);
        bool applied = SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);
        int error = applied ? 0 : Marshal.GetLastPInvokeError();

        uint? verified = null;
        if (applied && GetWindowDisplayAffinity(hWnd, out uint affinity))
        {
            verified = affinity;
        }

        string verifiedText = verified.HasValue ? $"0x{verified.Value:X8}" : "unavailable";
        if (applied)
        {
            LongCaptureLog.Info(
                $"capture exclusion applied role={LongCaptureLog.OneLine(role)} hwnd=0x{hWnd.ToInt64():X} requested=0x{WDA_EXCLUDEFROMCAPTURE:X8} verified={verifiedText}");
        }
        else
        {
            LongCaptureLog.Warn(
                $"capture exclusion failed role={LongCaptureLog.OneLine(role)} hwnd=0x{hWnd.ToInt64():X} requested=0x{WDA_EXCLUDEFROMCAPTURE:X8} win32={error}");
        }

        return new CaptureExclusionResult(hWnd, applied, error, verified, role);
    }

    public static IReadOnlyList<CaptureExclusionResult> ApplyToCurrentProcessTopLevelWindows(string reason)
    {
        var results = new List<CaptureExclusionResult>();
        uint ownPid = unchecked((uint)Environment.ProcessId);

        EnumWindowsProc callback = (hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid)
            {
                results.Add(Apply(hWnd, $"process-window:{reason}"));
            }
            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        int applied = 0;
        foreach (CaptureExclusionResult result in results)
        {
            if (result.Applied) applied++;
        }

        LongCaptureLog.Info(
            $"capture exclusion sweep reason={LongCaptureLog.OneLine(reason)} windows={results.Count} applied={applied} failed={results.Count - applied}");
        return results;
    }
}
