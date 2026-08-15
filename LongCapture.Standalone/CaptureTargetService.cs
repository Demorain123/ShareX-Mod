using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LongCapture.Standalone;

internal sealed class CaptureTargetDescriptor
{
    public CaptureTargetDescriptor(IntPtr handle, string title, int processId, string processName, Rectangle bounds, Icon? icon = null)
    {
        Handle = handle;
        Title = title;
        ProcessId = processId;
        ProcessName = processName;
        Bounds = bounds;
        Icon = icon;
    }

    public IntPtr Handle { get; }
    public string Title { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }
    public Rectangle Bounds { get; }
    public Icon? Icon { get; }

    public string HandleHex => $"0x{Handle.ToInt64():X}";

    public override string ToString() => $"{Title}  [{ProcessName} · PID {ProcessId}]";
}

internal static class CaptureTargetService
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private static readonly Dictionary<int, Icon?> ProcessIconCache = new();
    private static readonly object IconSync = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    public static IReadOnlyList<CaptureTargetDescriptor> EnumerateTopLevelWindows()
    {
        var result = new List<CaptureTargetDescriptor>();
        int ownPid = Environment.ProcessId;

        EnumWindowsProc callback = (hWnd, lParam) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint rawPid);
                if (rawPid == 0 || rawPid == ownPid) return true;

                if (!TryCreateTarget(hWnd, out CaptureTargetDescriptor? target, out string _detail) || target is null) return true;
                result.Add(target);
            }
            catch
            {
                // One inaccessible top-level window must not block the rest of the picker.
            }

            return true;
        };

        EnumWindows(callback, IntPtr.Zero);

        return result
            .GroupBy(target => target.Handle)
            .Select(group => group.First())
            .OrderBy(target => target.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryGetForegroundTarget(out CaptureTargetDescriptor? target, out string detail)
    {
        target = null;
        IntPtr hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            detail = "Windows did not report a foreground window; activate the target and try F7 again";
            return false;
        }

        GetWindowThreadProcessId(hWnd, out uint rawPid);
        if (rawPid == unchecked((uint)Environment.ProcessId))
        {
            detail = "LongCapture itself is foreground; activate the window to capture, then press F7";
            return false;
        }

        return TryCreateTarget(hWnd, out target, out detail);
    }

    public static bool TryActivateTarget(CaptureTargetDescriptor target, out string detail)
    {
        if (!TryRefreshTarget(target, out CaptureTargetDescriptor? refreshed, out detail) || refreshed is null)
        {
            return false;
        }

        bool activated = SetForegroundWindow(refreshed.Handle);
        detail = activated
            ? $"activated {refreshed.Title}"
            : $"Windows did not grant foreground activation for {refreshed.Title}; click the target once before selecting the region";
        return activated;
    }

    public static bool TryRefreshTarget(
        CaptureTargetDescriptor target,
        out CaptureTargetDescriptor? refreshed,
        out string detail) =>
        TryCreateTarget(target.Handle, out refreshed, out detail);

    public static bool TryCreateTarget(
        IntPtr hWnd,
        out CaptureTargetDescriptor? target,
        out string detail)
    {
        target = null;

        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            detail = "window handle is no longer valid";
            return false;
        }

        if (!IsWindowVisible(hWnd))
        {
            detail = "window is no longer visible";
            return false;
        }

        if (IsIconic(hWnd))
        {
            detail = "window is minimized; restore it before capture";
            return false;
        }

        string title = ReadWindowTitle(hWnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            detail = "window has no readable title";
            return false;
        }

        if (!TryGetCaptureRectangle(hWnd, out Rectangle bounds))
        {
            detail = "window client rectangle is empty or inaccessible";
            return false;
        }

        GetWindowThreadProcessId(hWnd, out uint rawPid);
        int processId = rawPid <= int.MaxValue ? (int)rawPid : 0;
        string processName = ReadProcessName(processId);
        Icon? icon = GetCachedWindowIcon(hWnd, processId);

        target = new CaptureTargetDescriptor(hWnd, title, processId, processName, bounds, icon);
        detail = $"{title} · {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y}";
        return true;
    }

    public static bool TryGetCaptureRectangle(IntPtr hWnd, out Rectangle rectangle)
    {
        rectangle = Rectangle.Empty;

        if (GetClientRect(hWnd, out RECT client))
        {
            var topLeft = new POINT { X = client.Left, Y = client.Top };
            var bottomRight = new POINT { X = client.Right, Y = client.Bottom };
            if (ClientToScreen(hWnd, ref topLeft) && ClientToScreen(hWnd, ref bottomRight))
            {
                int width = bottomRight.X - topLeft.X;
                int height = bottomRight.Y - topLeft.Y;
                if (width >= 32 && height >= 32)
                {
                    rectangle = new Rectangle(topLeft.X, topLeft.Y, width, height);
                    return true;
                }
            }
        }

        if (GetWindowRect(hWnd, out RECT window))
        {
            int width = window.Right - window.Left;
            int height = window.Bottom - window.Top;
            if (width >= 32 && height >= 32)
            {
                rectangle = new Rectangle(window.Left, window.Top, width, height);
                return true;
            }
        }

        return false;
    }

    private static Icon? GetCachedWindowIcon(IntPtr hWnd, int processId)
    {
        lock (IconSync)
        {
            if (processId > 0 && ProcessIconCache.TryGetValue(processId, out Icon? cached)) return cached;

            Icon? icon = TryReadWindowIcon(hWnd);
            if (icon is null && processId > 0)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) icon = Icon.ExtractAssociatedIcon(path);
                }
                catch
                {
                    // Cross-integrity processes may block MainModule access; WM_GETICON is enough when available.
                }
            }

            if (processId > 0) ProcessIconCache[processId] = icon;
            return icon;
        }
    }

    private static Icon? TryReadWindowIcon(IntPtr hWnd)
    {
        try
        {
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_SMALL2), IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_BIG), IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICONSM);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICON);
            return hIcon == IntPtr.Zero ? null : (Icon)Icon.FromHandle(hIcon).Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLengthW(hWnd);
        if (length <= 0) return string.Empty;

        var builder = new StringBuilder(length + 1);
        int copied = GetWindowTextW(hWnd, builder, builder.Capacity);
        return copied > 0 ? builder.ToString() : string.Empty;
    }

    private static string ReadProcessName(int processId)
    {
        if (processId <= 0) return "unknown";
        try
        {
            using Process process = Process.GetProcessById(processId);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? "unknown" : process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }
}
