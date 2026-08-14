#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModCaptureModeReadinessResult(
    bool Ready,
    string State,
    string Message,
    string RecipePath,
    int ChromePageTargets);

internal static class ShareXModCaptureModeReadiness
{
    public static async Task<ShareXModCaptureModeReadinessResult> ProbeAsync(
        ShareXModCaptureMode mode)
    {
        ShareXModV04Settings settings = ShareXModV04Settings.Load();

        if (mode == ShareXModCaptureMode.Normal)
        {
            return new ShareXModCaptureModeReadinessResult(
                true,
                "ready",
                "Normal ShareX Start/Stop capture is ready.",
                string.Empty,
                0);
        }

        string recipe = ShareXModCaptureModeProfile.Current.RecipePath;
        if (mode == ShareXModCaptureMode.RunRecipe &&
            (string.IsNullOrWhiteSpace(recipe) || !System.IO.File.Exists(recipe)))
        {
            recipe = ShareXModCaptureModeProfile.FindLatestRecipe() ?? string.Empty;
        }

        if (mode == ShareXModCaptureMode.RunRecipe && string.IsNullOrWhiteSpace(recipe))
        {
            return new ShareXModCaptureModeReadinessResult(
                false,
                "no-recipe",
                "No Capture Recipe has been recorded yet.",
                string.Empty,
                0);
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2.5));
            await using ShareXModChromeCdpClient client = new();
            var targets = await client.ListTargetsAsync(settings.ChromeCdpEndpoint, timeout.Token);
            int pages = targets.Count;

            if (pages == 0)
            {
                return new ShareXModCaptureModeReadinessResult(
                    false,
                    "chrome-no-page-target",
                    "Chrome Enhanced endpoint is reachable, but no page target is available.",
                    recipe,
                    0);
            }

            return new ShareXModCaptureModeReadinessResult(
                true,
                "ready",
                mode == ShareXModCaptureMode.RecordRecipe
                    ? $"Chrome connected · {pages} page target(s) · ready to record."
                    : $"Chrome connected · Recipe ready · {pages} page target(s).",
                recipe,
                pages);
        }
        catch (OperationCanceledException)
        {
            return new ShareXModCaptureModeReadinessResult(
                false,
                "chrome-timeout",
                "Chrome Enhanced connection timed out; normal ShareX capture can still run.",
                recipe,
                0);
        }
        catch
        {
            return new ShareXModCaptureModeReadinessResult(
                false,
                "chrome-unavailable",
                "Chrome Enhanced is not connected. Normal capture remains available.",
                recipe,
                0);
        }
    }
}
