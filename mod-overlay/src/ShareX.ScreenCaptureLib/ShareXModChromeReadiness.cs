#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModChromeReadinessResult(
    bool Ready,
    string Detail);

internal static class ShareXModChromeReadiness
{
    public static async Task<ShareXModChromeReadinessResult> ProbeAsync(
        ShareXModV04Settings settings,
        ShareXModCaptureMode mode,
        string? recipePath)
    {
        if (mode == ShareXModCaptureMode.Normal)
        {
            return new ShareXModChromeReadinessResult(
                true,
                "Original Start/Stop capture; browser automation is not required.");
        }

        if (mode == ShareXModCaptureMode.RunRecipe)
        {
            if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
            {
                return new ShareXModChromeReadinessResult(
                    false,
                    "No recorded Capture Recipe is selected.");
            }

            if (!ShareXModCaptureRecipeReviewService.IsRunAllowed(
                    settings,
                    recipePath,
                    out string reviewReason))
            {
                return new ShareXModChromeReadinessResult(
                    false,
                    reviewReason);
            }
        }

        try
        {
            await using ShareXModChromeCdpClient client = new();
            ShareXModChromeConnectionResolution connection =
                await ShareXModChromeConnectionBroker.ResolveAsync(client, settings);

            if (!connection.Connected)
            {
                return new ShareXModChromeReadinessResult(
                    false,
                    "No Capture Browser connection · " + connection.Detail);
            }

            string action = mode switch
            {
                ShareXModCaptureMode.RecordRecipe => "recipe recording",
                ShareXModCaptureMode.SmartWeb => "smart web capture",
                _ => "reviewed recipe replay"
            };

            return new ShareXModChromeReadinessResult(
                true,
                $"Chrome {action} · {connection.Detail}");
        }
        catch (Exception ex)
        {
            return new ShareXModChromeReadinessResult(
                false,
                "Chrome readiness probe failed: " + ex.GetType().Name);
        }
    }
}
