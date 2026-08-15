#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModChromeBackgroundCaptureResult
{
    public Bitmap Preview { get; init; } = null!;
    public string DirectoryPath { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public int PartCount { get; init; }
    public int SourceWidth { get; init; }
    public long SourceHeight { get; init; }
    public bool StoppedByUser { get; init; }
    public bool TruncatedBySafetyLimit { get; init; }
}

internal static class ShareXModChromeBackgroundCapture
{
    private sealed record Metrics(double CssWidth, double CssHeight, double ViewportHeight, double DipPerCss);
    private sealed record PartInfo(string File, double StartY, double EndY, int PixelWidth, int PixelHeight);

    public static async Task<ShareXModChromeBackgroundCaptureResult?> CaptureAsync(
        ShareXModChromeCdpClient client,
        ShareXModChromeTarget target,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        if (!settings.ChromeBackgroundCapture)
        {
            return null;
        }

        Metrics initial = await GetMetricsAsync(client);
        if (initial.CssWidth <= 0 || initial.CssHeight <= 0)
        {
            return null;
        }

        string root = ResolveOutputDirectory(settings.ChromeBackgroundOutputDirectory);
        string directory = Path.Combine(root, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
        Directory.CreateDirectory(directory);

        double startY = Math.Clamp(settings.ChromeBackgroundStartY, 0, Math.Max(0, initial.CssHeight - 1));
        bool explicitEnd = settings.ChromeBackgroundEndY > startY;
        double dynamicEnd = explicitEnd
            ? Math.Min(settings.ChromeBackgroundEndY, initial.CssHeight)
            : initial.CssHeight;

        int targetHeight = Math.Clamp(settings.ChromeBackgroundTileHeight, 900, 12000);
        int minHeight = Math.Clamp(settings.ChromeBackgroundMinimumTileHeight, 500, targetHeight);
        int maxHeight = Math.Clamp(settings.ChromeBackgroundMaximumTileHeight, targetHeight, 16000);
        int maxParts = Math.Clamp(settings.ChromeBackgroundMaxParts, 1, 2000);
        double maxCssHeight = Math.Max(10000, settings.ChromeBackgroundMaxCssHeight);
        int stableBottomRequired = Math.Clamp(settings.ChromeBackgroundStableBottomPasses, 1, 10);

        List<PartInfo> parts = new();
        double y = startY;
        int stableBottomPasses = 0;
        bool stoppedByUser = false;
        bool truncated = false;

        while (parts.Count < maxParts && y < maxCssHeight)
        {
            if (shouldStop?.Invoke() == true)
            {
                stoppedByUser = true;
                break;
            }

            Metrics metrics = await GetMetricsAsync(client);
            if (!explicitEnd)
            {
                if (metrics.CssHeight > dynamicEnd + 2)
                {
                    dynamicEnd = metrics.CssHeight;
                    stableBottomPasses = 0;
                }
            }

            double endY = Math.Min(explicitEnd ? dynamicEnd : Math.Max(dynamicEnd, metrics.CssHeight), maxCssHeight);
            if (y >= endY - 1)
            {
                if (explicitEnd)
                {
                    break;
                }

                await WarmPositionAsync(client, Math.Max(0, endY - metrics.ViewportHeight), settings);
                Metrics afterBottom = await GetMetricsAsync(client);
                if (afterBottom.CssHeight > dynamicEnd + 2 && afterBottom.CssHeight <= maxCssHeight)
                {
                    dynamicEnd = afterBottom.CssHeight;
                    stableBottomPasses = 0;
                    continue;
                }

                stableBottomPasses++;
                if (stableBottomPasses >= stableBottomRequired)
                {
                    break;
                }
                continue;
            }

            double desiredCut = Math.Min(endY, y + targetHeight);
            double cutY = desiredCut;
            if (settings.ChromeBackgroundUseNaturalCuts && desiredCut < endY)
            {
                cutY = await FindNaturalCutAsync(client, y, desiredCut, endY, settings);
            }

            double minimumCut = Math.Min(endY, y + minHeight);
            double maximumCut = Math.Min(endY, y + maxHeight);
            cutY = Math.Clamp(cutY, minimumCut, maximumCut);
            if (cutY <= y + 1)
            {
                cutY = Math.Min(endY, y + targetHeight);
            }

            double centerScrollY = Math.Max(0, ((y + cutY) * 0.5) - metrics.ViewportHeight * 0.5);
            await WarmPositionAsync(client, centerScrollY, settings);

            if (shouldStop?.Invoke() == true)
            {
                stoppedByUser = true;
                break;
            }

            Metrics captureMetrics = await GetMetricsAsync(client);
            double actualEnd = Math.Min(cutY, captureMetrics.CssHeight);
            double cssHeight = actualEnd - y;
            if (cssHeight <= 1)
            {
                break;
            }

            double dipPerCss = captureMetrics.DipPerCss > 0 ? captureMetrics.DipPerCss : 1;
            using JsonDocument response = await client.SendCdpCommandAsync("Page.captureScreenshot", new
            {
                format = "png",
                fromSurface = true,
                captureBeyondViewport = true,
                optimizeForSpeed = true,
                clip = new
                {
                    x = 0d,
                    y = y * dipPerCss,
                    width = captureMetrics.CssWidth * dipPerCss,
                    height = cssHeight * dipPerCss,
                    scale = 1d
                }
            });

            string? base64 = response.RootElement.GetProperty("result").GetProperty("data").GetString();
            if (string.IsNullOrWhiteSpace(base64))
            {
                break;
            }

            byte[] png = Convert.FromBase64String(base64);
            string fileName = $"part_{parts.Count + 1:D4}.png";
            string path = Path.Combine(directory, fileName);
            await File.WriteAllBytesAsync(path, png);

            using (MemoryStream stream = new(png, writable: false))
            using (Bitmap bitmap = new(stream))
            {
                parts.Add(new PartInfo(fileName, y, actualEnd, bitmap.Width, bitmap.Height));
            }

            y = actualEnd;
        }

        if (parts.Count >= maxParts || y >= maxCssHeight)
        {
            truncated = true;
        }

        if (parts.Count == 0)
        {
            TryDeleteDirectory(directory);
            return null;
        }

        long totalPixelHeight = 0;
        int sourceWidth = 0;
        foreach (PartInfo part in parts)
        {
            sourceWidth = Math.Max(sourceWidth, part.PixelWidth);
            totalPixelHeight += part.PixelHeight;
        }

        string manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            format = "ShareX-Mod Chrome Enhanced background segmented capture",
            version = "0.4.0",
            created = DateTimeOffset.Now,
            title = target.Title,
            url = target.Url,
            startY,
            endY = y,
            explicitEnd,
            stoppedByUser,
            truncatedBySafetyLimit = truncated,
            sourceWidth,
            sourceHeight = totalPixelHeight,
            cssCapturedHeight = y - startY,
            partCount = parts.Count,
            parts
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        Bitmap preview = CreatePreview(directory, parts, sourceWidth, totalPixelHeight, settings.SegmentPreviewMaxHeight);
        ShareXModSegmentedOutputRegistry.Register(preview, manifestPath, sourceWidth, totalPixelHeight);

        return new ShareXModChromeBackgroundCaptureResult
        {
            Preview = preview,
            DirectoryPath = directory,
            ManifestPath = manifestPath,
            PartCount = parts.Count,
            SourceWidth = sourceWidth,
            SourceHeight = totalPixelHeight,
            StoppedByUser = stoppedByUser,
            TruncatedBySafetyLimit = truncated
        };
    }

    private static async Task<Metrics> GetMetricsAsync(ShareXModChromeCdpClient client)
    {
        using JsonDocument layout = await client.SendCdpCommandAsync("Page.getLayoutMetrics");
        JsonElement result = layout.RootElement.GetProperty("result");
        JsonElement css = result.TryGetProperty("cssContentSize", out JsonElement cssSize)
            ? cssSize
            : result.GetProperty("contentSize");
        JsonElement dip = result.TryGetProperty("contentSize", out JsonElement dipSize)
            ? dipSize
            : css;

        double cssWidth = css.GetProperty("width").GetDouble();
        double cssHeight = css.GetProperty("height").GetDouble();
        double dipWidth = dip.GetProperty("width").GetDouble();
        double dipPerCss = cssWidth > 0 && dipWidth > 0 ? dipWidth / cssWidth : 1;

        using JsonDocument runtime = await client.EvaluateAsync("(() => ({ h: window.innerHeight || document.documentElement.clientHeight || 1 }))()", false);
        JsonElement value = runtime.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
        double viewport = value.GetProperty("h").GetDouble();
        return new Metrics(cssWidth, cssHeight, Math.Max(1, viewport), dipPerCss);
    }

    private static async Task WarmPositionAsync(ShareXModChromeCdpClient client, double y, ShareXModV04Settings settings)
    {
        string ys = y.ToString("0.###", CultureInfo.InvariantCulture);
        int imageWait = Math.Clamp(settings.ChromeBackgroundImageWaitMs, 0, 10000);
        int settle = Math.Clamp(settings.ChromeBackgroundSettleMs, 50, 10000);
        string script = $$"""
(async () => {
  window.scrollTo({ top: {{ys}}, left: 0, behavior: 'instant' });
  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  const images = [...document.images].filter(img => {
    const r = img.getBoundingClientRect();
    return r.bottom > -window.innerHeight && r.top < window.innerHeight * 2;
  });
  const deadline = Date.now() + {{imageWait}};
  await Promise.all(images.map(img => img.complete ? Promise.resolve() : new Promise(resolve => {
    const done = () => resolve();
    img.addEventListener('load', done, { once: true });
    img.addEventListener('error', done, { once: true });
    setTimeout(done, Math.max(0, deadline - Date.now()));
  })));
  await new Promise(r => setTimeout(r, {{settle}}));
  return { y: window.scrollY, height: (document.scrollingElement || document.documentElement).scrollHeight };
})()
""";
        using JsonDocument _ = await client.EvaluateAsync(script, true);
    }

    private static async Task<double> FindNaturalCutAsync(
        ShareXModChromeCdpClient client,
        double startY,
        double targetY,
        double endY,
        ShareXModV04Settings settings)
    {
        int radius = Math.Clamp(settings.ChromeBackgroundCutSearchRadius, 100, 4000);
        double low = Math.Max(startY + 300, targetY - radius);
        double high = Math.Min(endY, targetY + radius);
        string sStart = startY.ToString("0.###", CultureInfo.InvariantCulture);
        string sTarget = targetY.ToString("0.###", CultureInfo.InvariantCulture);
        string sLow = low.ToString("0.###", CultureInfo.InvariantCulture);
        string sHigh = high.ToString("0.###", CultureInfo.InvariantCulture);

        string script = $$"""
(() => {
  const start = {{sStart}}, target = {{sTarget}}, low = {{sLow}}, high = {{sHigh}};
  let best = target, bestScore = Number.POSITIVE_INFINITY;
  const selector = 'article,section,p,li,blockquote,pre,table,figure,figcaption,img,video,details,h1,h2,h3,h4,h5,h6,div';
  for (const el of document.querySelectorAll(selector)) {
    const r = el.getBoundingClientRect();
    if (r.height < 18 || r.width < 80) continue;
    const cs = getComputedStyle(el);
    if (cs.position === 'fixed' || cs.position === 'sticky') continue;
    const top = r.top + window.scrollY, bottom = r.bottom + window.scrollY;
    for (const y of [top, bottom]) {
      if (y < low || y > high || y <= start + 64) continue;
      let penalty = 0;
      if (['P','LI','PRE','TABLE'].includes(el.tagName)) penalty += 45;
      const score = Math.abs(y - target) + penalty;
      if (score < bestScore) { bestScore = score; best = y; }
    }
  }
  return best;
})()
""";

        try
        {
            using JsonDocument result = await client.EvaluateAsync(script, false);
            JsonElement value = result.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
            return value.GetDouble();
        }
        catch
        {
            return targetY;
        }
    }

    private static Bitmap CreatePreview(string directory, IReadOnlyList<PartInfo> parts, int sourceWidth, long sourceHeight, int configuredMaxHeight)
    {
        int maxHeight = Math.Clamp(configuredMaxHeight, 2000, 30000);
        double scale = Math.Min(1, maxHeight / (double)Math.Max(1, sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        Bitmap preview = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.White);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int y = 0;
        foreach (PartInfo part in parts)
        {
            using Bitmap image = new(Path.Combine(directory, part.File));
            int drawHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            graphics.DrawImage(image,
                new Rectangle(0, y, width, drawHeight),
                new Rectangle(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            y += drawHeight;
        }

        return preview;
    }

    private static string ResolveOutputDirectory(string configured)
    {
        string relative = string.IsNullOrWhiteSpace(configured)
            ? "ShareX-Mod\\ChromeBackgroundCaptures"
            : configured;
        string primary = Path.IsPathRooted(relative)
            ? relative
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
        try
        {
            Directory.CreateDirectory(primary);
            return primary;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX-Mod",
                "ChromeBackgroundCaptures");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
