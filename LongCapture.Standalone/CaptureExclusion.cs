using System;
using System.Collections.Concurrent;
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

internal enum CaptureWindowRole
{
    AlwaysExcluded,
    DebugVisible
}

internal static class CaptureExclusion
{
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private static readonly ConcurrentDictionary<IntPtr, CaptureWindowRole> Roles = new();
    private static volatile bool debugCaptureUi;
    private static volatile bool includeInternalDebugWindows;

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

    public static bool DebugCaptureUi => debugCaptureUi;
    public static bool IncludeInternalDebugWindows => includeInternalDebugWindows;
    public static uint DesiredAffinity => WDA_EXCLUDEFROMCAPTURE;

    public static void SetDebugCaptureUi(bool enabled, string reason)
    {
        debugCaptureUi = enabled;
        LongCaptureLog.Info($"debug capture mode changed enabled={enabled} reason={LongCaptureLog.OneLine(reason)} policy=role-aware includeInternal={includeInternalDebugWindows}");
        ApplyToCurrentProcessTopLevelWindows(enabled ? "debug-enabled" : "debug-disabled");
    }

    public static void SetIncludeInternalDebugWindows(bool enabled, string reason)
    {
        includeInternalDebugWindows = enabled;
        LongCaptureLog.Info($"debug internal-window capture changed enabled={enabled} reason={LongCaptureLog.OneLine(reason)} debug={debugCaptureUi}");
        ApplyToCurrentProcessTopLevelWindows(enabled ? "internal-debug-enabled" : "internal-debug-disabled");
    }

    public static CaptureExclusionResult Apply(Form form, string role)
    {
        _ = form.Handle;
        RegisterExplicitRole(form.Handle, role);
        return ApplyKnownPolicy(form.Handle, role);
    }

    /// <summary>
    /// Used by WinEvent watcher/sweeps. Unknown/transient windows default to AlwaysExcluded so
    /// temporary Avalonia hosts, selector helpers and blank owner windows cannot enter the long
    /// screenshot merely because normal Debug is enabled. The user can explicitly opt in to
    /// capturing them with the separate include-internal-windows debug option.
    /// </summary>
    public static CaptureExclusionResult Apply(IntPtr hWnd, string role)
    {
        if (!IsTransientReason(role))
        {
            RegisterExplicitRole(hWnd, role);
        }
        return ApplyKnownPolicy(hWnd, role);
    }

    public static void RegisterDebugVisible(Form form, string role)
    {
        _ = form.Handle;
        Roles[form.Handle] = CaptureWindowRole.DebugVisible;
        ApplyKnownPolicy(form.Handle, role);
    }

    public static void RegisterAlwaysExcluded(IntPtr hWnd, string role)
    {
        if (hWnd == IntPtr.Zero) return;
        Roles[hWnd] = CaptureWindowRole.AlwaysExcluded;
        ApplyKnownPolicy(hWnd, role);
    }

    private static void RegisterExplicitRole(IntPtr hWnd, string role)
    {
        if (hWnd == IntPtr.Zero) return;
        Roles[hWnd] = ClassifyRole(role);
    }

    private static CaptureWindowRole ClassifyRole(string role)
    {
        if (string.Equals(role, "main-form", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "recipe-review-form", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "capture-hud", StringComparison.OrdinalIgnoreCase) ||
            role.Contains("debug-visible", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureWindowRole.DebugVisible;
        }

        return CaptureWindowRole.AlwaysExcluded;
    }

    private static bool IsTransientReason(string role) =>
        role.StartsWith("win-event-", StringComparison.OrdinalIgnoreCase) ||
        role.StartsWith("process-window:", StringComparison.OrdinalIgnoreCase);

    private static CaptureExclusionResult ApplyKnownPolicy(IntPtr hWnd, string role)
    {
        if (hWnd == IntPtr.Zero)
        {
            var missing = new CaptureExclusionResult(hWnd, false, 0, null, role);
            LongCaptureLog.Warn($"capture affinity skipped role={LongCaptureLog.OneLine(role)} reason=no-window-handle");
            return missing;
        }

        CaptureWindowRole windowRole = Roles.TryGetValue(hWnd, out CaptureWindowRole registered)
            ? registered
            : CaptureWindowRole.AlwaysExcluded;
        bool allowCapture = debugCaptureUi &&
            (windowRole == CaptureWindowRole.DebugVisible || includeInternalDebugWindows);
        uint requested = allowCapture ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE;

        Marshal.SetLastPInvokeError(0);
        bool applied = SetWindowDisplayAffinity(hWnd, requested);
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
                $"capture affinity applied role={LongCaptureLog.OneLine(role)} windowRole={windowRole} hwnd=0x{hWnd.ToInt64():X} requested=0x{requested:X8} debug={debugCaptureUi} includeInternal={includeInternalDebugWindows} verified={verifiedText}");
        }
        else
        {
            LongCaptureLog.Warn(
                $"capture affinity failed role={LongCaptureLog.OneLine(role)} windowRole={windowRole} hwnd=0x{hWnd.ToInt64():X} requested=0x{requested:X8} debug={debugCaptureUi} includeInternal={includeInternalDebugWindows} win32={error}");
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
                results.Add(ApplyKnownPolicy(hWnd, $"process-window:{reason}"));
            }
            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        int applied = 0;
        int debugVisible = 0;
        foreach (CaptureExclusionResult result in results)
        {
            if (result.Applied) applied++;
            if (Roles.TryGetValue(result.Handle, out CaptureWindowRole role) && role == CaptureWindowRole.DebugVisible) debugVisible++;
        }

        LongCaptureLog.Info(
            $"capture affinity sweep reason={LongCaptureLog.OneLine(reason)} windows={results.Count} applied={applied} failed={results.Count - applied} debug={debugCaptureUi} includeInternal={includeInternalDebugWindows} registeredDebugVisible={debugVisible}");
        return results;
    }
}
