using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal static class DebugStateSnapshot
{
    public static string Save(Form form, string outputDirectory, object state)
    {
        Directory.CreateDirectory(outputDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        string baseName = Path.Combine(outputDirectory, $"LongCapture-DebugState_{stamp}");
        string imagePath = baseName + ".png";
        string jsonPath = baseName + ".json";

        try
        {
            Size size = form.ClientSize.Width > 0 && form.ClientSize.Height > 0
                ? form.ClientSize
                : new Size(Math.Max(1, form.Width), Math.Max(1, form.Height));
            using Bitmap bitmap = new(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(imagePath, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"debug state GUI snapshot failed type={ex.GetType().Name} message={LongCaptureLog.OneLine(ex.Message)}");
        }

        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        LongCaptureLog.Info($"debug state snapshot saved image={LongCaptureLog.OneLine(imagePath)} json={LongCaptureLog.OneLine(jsonPath)}");
        return jsonPath;
    }
}
