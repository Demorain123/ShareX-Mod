using System;
using System.Runtime.InteropServices;

namespace LongCapture.Standalone;

internal sealed class CaptureExclusionWatcher : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hWnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private readonly WinEventDelegate callback;
    private GCHandle callbackRoot;
    private IntPtr hook;
    private bool disposed;

    private CaptureExclusionWatcher()
    {
        callback = OnWinEvent;
        callbackRoot = GCHandle.Alloc(callback);
        hook = SetWinEventHook(
            EVENT_OBJECT_CREATE,
            EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            callback,
            unchecked((uint)Environment.ProcessId),
            0,
            WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero)
        {
            int error = Marshal.GetLastPInvokeError();
            callbackRoot.Free();
            LongCaptureLog.Warn($"capture exclusion watcher unavailable win32={error}");
        }
        else
        {
            LongCaptureLog.Info($"capture exclusion watcher started hook=0x{hook.ToInt64():X}");
        }
    }

    public static CaptureExclusionWatcher Start() => new();

    public bool IsActive => hook != IntPtr.Zero;

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hWnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTime)
    {
        if (disposed || hWnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0) return;
        if (eventType is not EVENT_OBJECT_CREATE and not EVENT_OBJECT_SHOW) return;

        CaptureExclusion.Apply(
            hWnd,
            eventType == EVENT_OBJECT_CREATE ? "win-event-create" : "win-event-show");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (hook != IntPtr.Zero)
        {
            if (!UnhookWinEvent(hook))
            {
                LongCaptureLog.Warn($"capture exclusion watcher unhook failed win32={Marshal.GetLastPInvokeError()}");
            }
            else
            {
                LongCaptureLog.Info("capture exclusion watcher stopped");
            }
            hook = IntPtr.Zero;
        }

        if (callbackRoot.IsAllocated)
        {
            callbackRoot.Free();
        }
    }
}
