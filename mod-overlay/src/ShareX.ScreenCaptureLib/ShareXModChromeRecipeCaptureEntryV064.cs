#nullable enable

using System;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModChromeRecipeCaptureEntryV064
{
    public static async Task<ShareXModChromeBackgroundCaptureResult?> TryCaptureAsync(
        IntPtr selectedWindowHandle,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        ShareXModChromeBackgroundCaptureResult? result =
            await ShareXModChromeRecipeCaptureEntryV062.TryCaptureAsync(
                selectedWindowHandle,
                settings,
                shouldStop);

        if (result == null)
        {
            return null;
        }

        try
        {
            ShareXModRecipeOutputLedger.TryWrite(result, settings);
        }
        catch
        {
            // The screenshot remains valid even if advisory ledger output fails.
        }

        try
        {
            // Deliberately post-capture: SHA-256 reads saved files only after scrolling, repair,
            // appendices and Position Ledger have finished, so it cannot make the live capture loop
            // stutter. Native originals are already hashed by their collectors and are optional here.
            await ShareXModCaptureIntegrityManifest.TryWriteAsync(result, settings);
        }
        catch
        {
            // Integrity output is advisory and must never invalidate an otherwise valid screenshot.
        }

        return result;
    }
}
