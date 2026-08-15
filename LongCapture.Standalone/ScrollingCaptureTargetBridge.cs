using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.Reflection;

namespace LongCapture.Standalone;

internal static class ScrollingCaptureTargetBridge
{
    public static bool TryAssignTarget(
        ScrollingCaptureService service,
        CaptureTargetDescriptor target,
        out string detail) =>
        TryAssignTarget(service, target, target.Bounds, out detail);

    public static bool TryRelockTargetToSelectedRegion(
        ScrollingCaptureService service,
        CaptureTargetDescriptor target,
        out Rectangle selectedRegion,
        out string detail)
    {
        selectedRegion = Rectangle.Empty;
        if (!TryReadSelectedRectangle(service, out Rectangle picked, out detail)) return false;

        Rectangle clipped = Rectangle.Intersect(picked, target.Bounds);
        if (clipped.Width < 32 || clipped.Height < 32)
        {
            detail = $"selected region {picked} does not substantially overlap locked target {target.Bounds}";
            return false;
        }

        if (!TryAssignTarget(service, target, clipped, out detail)) return false;
        selectedRegion = clipped;
        detail = $"relocked {target.HandleHex} to selected sub-region {clipped.Width}x{clipped.Height} at {clipped.X},{clipped.Y}";
        return true;
    }

    public static bool TryReadSelectedRectangle(
        ScrollingCaptureService service,
        out Rectangle selectedRegion,
        out string detail)
    {
        selectedRegion = Rectangle.Empty;
        try
        {
            if (!TryGetManager(service, out object? manager, out Type? managerType, out detail) || manager is null || managerType is null)
            {
                return false;
            }

            FieldInfo? selectedRectangleField = managerType.GetField("selectedRectangle", BindingFlags.Instance | BindingFlags.NonPublic);
            if (selectedRectangleField?.GetValue(manager) is not Rectangle rectangle || rectangle.IsEmpty)
            {
                detail = "ShareX selectedRectangle is empty after region selection";
                return false;
            }

            selectedRegion = rectangle;
            detail = $"selected region {rectangle.Width}x{rectangle.Height} at {rectangle.X},{rectangle.Y}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"selected-region bridge failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryAssignTarget(
        ScrollingCaptureService service,
        CaptureTargetDescriptor target,
        Rectangle captureRectangle,
        out string detail)
    {
        try
        {
            if (!TryGetManager(service, out object? manager, out Type? managerType, out detail) || manager is null || managerType is null)
            {
                return false;
            }

            FieldInfo? selectedWindowField = managerType.GetField("selectedWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? selectedRectangleField = managerType.GetField("selectedRectangle", BindingFlags.Instance | BindingFlags.NonPublic);
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
            selectedRectangleField.SetValue(manager, captureRectangle);
            detail = $"locked {target.HandleHex} to {captureRectangle.Width}x{captureRectangle.Height} at {captureRectangle.X},{captureRectangle.Y}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"target bridge failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetManager(
        ScrollingCaptureService service,
        out object? manager,
        out Type? managerType,
        out string detail)
    {
        FieldInfo? managerField = typeof(ScrollingCaptureService)
            .GetField("_manager", BindingFlags.Instance | BindingFlags.NonPublic);
        manager = managerField?.GetValue(service);
        managerType = manager?.GetType();
        if (manager is null || managerType is null)
        {
            detail = "ShareX scrolling manager field '_manager' was not found";
            return false;
        }

        detail = "manager resolved";
        return true;
    }
}
