#nullable enable

using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

public partial class ScrollingCaptureWindow
{
    private async Task LoadShareXModResultAsync(Bitmap? bitmap)
    {
        if (bitmap != null && ShareXModSegmentedOutputRegistry.TryGet(bitmap, out string? manifest, out int sourceWidth, out long sourceHeight))
        {
            LoadImage(bitmap);
            ResultSizeText.Text = $"{sourceWidth}x{sourceHeight} (segmented)";
            CopyButton.IsEnabled = false;
            UploadButton.IsEnabled = true;

            string output = !string.IsNullOrWhiteSpace(manifest)
                ? Path.GetDirectoryName(manifest) ?? manifest
                : "segmented output";
            StatusText.Text = $"Segmented capture saved: {output}";
            return;
        }

        await LoadShareXModImageAsync(bitmap);
    }
}
