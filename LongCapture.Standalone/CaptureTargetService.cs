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
    public CaptureTargetDescriptor(IntPtr handle, string title, int processId, string processName, Rectangle bounds)
    {
        Handle = handle;
        Title = title;
        ProcessId = processId;
        ProcessName = processName;
        Bounds = bounds;
    }

    public IntPtr Handle { get; }
    public string Title { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }
    public Rectangle Bounds { get; }

    public string HandleHex => $"0x{Handle.ToInt64():X}";

    public override string ToString() => $"{Title}  [{ProcessName} · PID {ProcessId}]";
}

internal static class CaptureTargetService
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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

        target = new CaptureTargetDescriptor(hWnd, title, processId, processName, bounds);
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
