using ShareX.ScreenCaptureLib;
using System;
using System.Reflection;

namespace LongCapture.Standalone;

internal static class ScrollingCaptureTargetBridge
{
    public static bool TryAssignTarget(
        ScrollingCaptureService service,
        CaptureTargetDescriptor target,
        out string detail)
    {
        try
        {
            FieldInfo? managerField = typeof(ScrollingCaptureService)
                .GetField("_manager", BindingFlags.Instance | BindingFlags.NonPublic);
            object? manager = managerField?.GetValue(service);
            if (manager is null)
            {
                detail = "ShareX scrolling manager field '_manager' was not found";
                return false;
            }

            Type managerType = manager.GetType();
            FieldInfo? selectedWindowField = managerType
                .GetField("selectedWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? selectedRectangleField = managerType
                .GetField("selectedRectangle", BindingFlags.Instance | BindingFlags.NonPublic);
            if (selectedWindowField is null || selectedRectangleField is null)
            {
                detail = "ShareX selectedWindow/selectedRectangle fields were not found";
                return false;
            }

            Type? windowInfoType = Type.GetType(
                "ShareX.HelpersLib.WindowInfo, ShareX.HelpersLib",
                throwOnError: false);
            if (windowInfoType is null)
            {
                detail = "ShareX.HelpersLib.WindowInfo could not be loaded";
                return false;
            }

            object? windowInfo = Activator.CreateInstance(windowInfoType, target.Handle);
            if (windowInfo is null)
            {
                detail = "ShareX WindowInfo could not be created for the selected HWND";
                return false;
            }

            selectedWindowField.SetValue(manager, windowInfo);
            selectedRectangleField.SetValue(manager, target.Bounds);
            detail = $"locked {target.HandleHex} to {target.Bounds.Width}x{target.Bounds.Height} at {target.Bounds.X},{target.Bounds.Y}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"target bridge failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
