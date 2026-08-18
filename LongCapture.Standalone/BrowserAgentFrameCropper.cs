using System.Drawing.Imaging;
using System.Text.Json;

namespace LongCapture.Standalone;

internal readonly record struct BrowserAgentCaptureRegion(
    double LeftCss,
    double TopCss,
    double WidthCss,
    double HeightCss)
{
    public bool IsValid => WidthCss > 1 && HeightCss > 1;
}

internal sealed class BrowserAgentCroppedFrame
{
    public required byte[] Png { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required BrowserAgentCaptureRegion Region { get; init; }
}

internal static class BrowserAgentFrameCropper
{
    public static BrowserAgentCroppedFrame Crop(
        byte[] png,
        JsonElement response,
        JsonElement before)
    {
        using var input = new MemoryStream(png, writable: false);
        using var source = new Bitmap(input);

        double viewportWidth = ReadDouble(before, "viewportWidth");
        double viewportHeight = ReadDouble(before, "viewportHeight");
        BrowserAgentCaptureRegion region = ReadRegion(response, viewportWidth, viewportHeight);

        if (!region.IsValid)
        {
            region = new BrowserAgentCaptureRegion(0, 0, viewportWidth, viewportHeight);
        }

        double scaleX = source.Width / Math.Max(1.0, viewportWidth);
        double scaleY = source.Height / Math.Max(1.0, viewportHeight);

        int left = Math.Clamp((int)Math.Round(region.LeftCss * scaleX), 0, Math.Max(0, source.Width - 1));
        int top = Math.Clamp((int)Math.Round(region.TopCss * scaleY), 0, Math.Max(0, source.Height - 1));
        int right = Math.Clamp((int)Math.Round((region.LeftCss + region.WidthCss) * scaleX), left + 1, source.Width);
        int bottom = Math.Clamp((int)Math.Round((region.TopCss + region.HeightCss) * scaleY), top + 1, source.Height);
        var cropRectangle = Rectangle.FromLTRB(left, top, right, bottom);

        if (cropRectangle.Left == 0 && cropRectangle.Top == 0 &&
            cropRectangle.Width == source.Width && cropRectangle.Height == source.Height)
        {
            return new BrowserAgentCroppedFrame
            {
                Png = png,
                PixelWidth = source.Width,
                PixelHeight = source.Height,
                Region = region
            };
        }

        using var cropped = new Bitmap(cropRectangle.Width, cropRectangle.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(cropped))
        {
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, cropped.Width, cropped.Height),
                cropRectangle,
                GraphicsUnit.Pixel);
        }

        using var output = new MemoryStream();
        cropped.Save(output, ImageFormat.Png);
        return new BrowserAgentCroppedFrame
        {
            Png = output.ToArray(),
            PixelWidth = cropped.Width,
            PixelHeight = cropped.Height,
            Region = region
        };
    }

    private static BrowserAgentCaptureRegion ReadRegion(JsonElement response, double viewportWidth, double viewportHeight)
    {
        if (!response.TryGetProperty("captureRegion", out JsonElement region) || region.ValueKind != JsonValueKind.Object)
        {
            return new BrowserAgentCaptureRegion(0, 0, viewportWidth, viewportHeight);
        }

        double left = Math.Clamp(ReadDouble(region, "left"), 0, Math.Max(0, viewportWidth - 1));
        double top = Math.Clamp(ReadDouble(region, "top"), 0, Math.Max(0, viewportHeight - 1));
        double width = Math.Clamp(ReadDouble(region, "width"), 1, Math.Max(1, viewportWidth - left));
        double height = Math.Clamp(ReadDouble(region, "height"), 1, Math.Max(1, viewportHeight - top));
        return new BrowserAgentCaptureRegion(left, top, width, height);
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : 0;
    }
}
