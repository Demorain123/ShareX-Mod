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

        return result;
    }
}
