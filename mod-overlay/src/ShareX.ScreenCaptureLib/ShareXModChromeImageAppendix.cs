#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModChromeImageAsset
{
    public string Kind { get; set; } = "img";
    public string CurrentUrl { get; set; } = string.Empty;
    public string BestUrl { get; set; } = string.Empty;
    public string Alt { get; set; } = string.Empty;
    public double RenderedWidth { get; set; }
    public double RenderedHeight { get; set; }
    public int NaturalWidth { get; set; }
    public int NaturalHeight { get; set; }
    public double DocumentX { get; set; }
    public double DocumentY { get; set; }
    public double DevicePixelRatio { get; set; }
    public string CaptureRisk { get; set; } = string.Empty;
    public double Score { get; set; }
}

internal sealed class ShareXModChromeImageAppendixResult
{
    public string DirectoryPath { get; init; } = string.Empty;
    public int CandidateCount { get; init; }
    public int SavedCount { get; init; }
    public int MissingCount { get; init; }
}

internal static class ShareXModChromeImageAppendix
{
    public static async Task<ShareXModChromeImageAppendixResult?> ExportAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string captureDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!settings.ChromeImageAppendixEnabled || string.IsNullOrWhiteSpace(captureDirectory))
        {
            return null;
        }

        List<ShareXModChromeImageAsset> assets = await CollectAssetsAsync(client, settings, cancellationToken);
        if (assets.Count == 0)
        {
            return new ShareXModChromeImageAppendixResult { DirectoryPath = captureDirectory };
        }

        if (settings.ChromeImageAppendixPreferLargestSrcset)
        {
            await PreloadBestCandidatesAsync(client, assets, cancellationToken);
        }

        Dictionary<string, ResourceRef> resources = await GetImageResourcesAsync(client, cancellationToken);
        string appendixDirectory = Path.Combine(captureDirectory, "image-appendix");
        Directory.CreateDirectory(appendixDirectory);

        List<object> manifestAssets = new();
        int saved = 0;
        int missing = 0;

        for (int i = 0; i < assets.Count; i++)
        {
            ShareXModChromeImageAsset asset = assets[i];
            string[] urls = new[] { asset.BestUrl, asset.CurrentUrl }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            string? savedPath = null;
            string? savedUrl = null;
            string? mimeType = null;

            foreach (string url in urls)
            {
                try
                {
                    if (TryDecodeDataUrl(url, out byte[] dataBytes, out string dataMime))
                    {
                        string fileName = BuildFileName(i + 1, asset, dataMime, url);
                        savedPath = Path.Combine(appendixDirectory, fileName);
                        await File.WriteAllBytesAsync(savedPath, dataBytes, cancellationToken);
                        savedUrl = url;
                        mimeType = dataMime;
                        break;
                    }

                    if (!TryFindResource(resources, url, out ResourceRef resource))
                    {
                        continue;
                    }

                    using JsonDocument response = await client.SendCdpCommandAsync("Page.getResourceContent", new
                    {
                        frameId = resource.FrameId,
                        url = resource.Url
                    }, cancellationToken);

                    JsonElement result = response.RootElement.GetProperty("result");
                    string content = result.GetProperty("content").GetString() ?? string.Empty;
                    bool base64Encoded = result.TryGetProperty("base64Encoded", out JsonElement encoded) && encoded.GetBoolean();
                    byte[] bytes = base64Encoded ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content);
                    if (bytes.Length == 0)
                    {
                        continue;
                    }

                    mimeType = resource.MimeType;
                    string outputName = BuildFileName(i + 1, asset, mimeType, resource.Url);
                    savedPath = Path.Combine(appendixDirectory, outputName);
                    await File.WriteAllBytesAsync(savedPath, bytes, cancellationToken);
                    savedUrl = resource.Url;
                    break;
                }
                catch
                {
                    // Appendix export is best-effort and must never break the main long capture.
                }
            }

            if (savedPath != null)
            {
                saved++;
            }
            else
            {
                missing++;
            }

            manifestAssets.Add(new
            {
                index = i + 1,
                asset.Kind,
                asset.CaptureRisk,
                asset.Alt,
                asset.RenderedWidth,
                asset.RenderedHeight,
                asset.NaturalWidth,
                asset.NaturalHeight,
                asset.DevicePixelRatio,
                asset.DocumentX,
                asset.DocumentY,
                currentUrl = asset.CurrentUrl,
                preferredUrl = asset.BestUrl,
                savedUrl,
                mimeType,
                file = savedPath == null ? null : Path.GetFileName(savedPath)
            });
        }

        string manifestPath = Path.Combine(appendixDirectory, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            format = "ShareX-Mod Chrome image appendix",
            version = "0.4.0-dev",
            created = DateTimeOffset.Now,
            pageTitle = client.Target?.Title,
            pageUrl = client.Target?.Url,
            candidateCount = assets.Count,
            savedCount = saved,
            missingCount = missing,
            note = "Files are saved from Chrome page resources without upscaling or image re-encoding when resource bytes are available.",
            assets = manifestAssets
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        return new ShareXModChromeImageAppendixResult
        {
            DirectoryPath = appendixDirectory,
            CandidateCount = assets.Count,
            SavedCount = saved,
            MissingCount = missing
        };
    }

    private static async Task<List<ShareXModChromeImageAsset>> CollectAssetsAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        CancellationToken cancellationToken)
    {
        string script = $$"""
(() => {
  const minRw = {{Math.Max(1, settings.ChromeImageAppendixMinRenderedWidth)}};
  const minRh = {{Math.Max(1, settings.ChromeImageAppendixMinRenderedHeight)}};
  const minNw = {{Math.Max(1, settings.ChromeImageAppendixMinNaturalWidth)}};
  const minNh = {{Math.Max(1, settings.ChromeImageAppendixMinNaturalHeight)}};
  const maxAssets = {{Math.Clamp(settings.ChromeImageAppendixMaxAssets, 1, 200)}};
  const preferLargest = {{settings.ChromeImageAppendixPreferLargestSrcset.ToString().ToLowerInvariant()}};
  const includeBackgrounds = {{settings.ChromeImageAppendixIncludeCssBackgrounds.ToString().ToLowerInvariant()}};
  const dpr = window.devicePixelRatio || 1;

  function resolve(u) {
    try { return new URL(u, document.baseURI).href; } catch { return u || ''; }
  }

  function largestSrcset(img) {
    if (!preferLargest || !img.srcset) return img.currentSrc || img.src || '';
    const candidates = img.srcset.split(',').map(x => x.trim()).filter(Boolean).map(part => {
      const pieces = part.split(/\s+/);
      const descriptor = pieces.length > 1 ? pieces[pieces.length - 1] : '';
      const rawUrl = pieces.length > 1 ? pieces.slice(0, -1).join(' ') : pieces[0];
      let weight = 1;
      if (/^[0-9.]+w$/.test(descriptor)) weight = parseFloat(descriptor);
      else if (/^[0-9.]+x$/.test(descriptor)) weight = parseFloat(descriptor) * Math.max(img.naturalWidth || 1, img.width || 1);
      return { url: resolve(rawUrl), weight };
    });
    candidates.push({ url: img.currentSrc || img.src || '', weight: img.naturalWidth || 1 });
    candidates.sort((a, b) => b.weight - a.weight);
    return candidates[0]?.url || img.currentSrc || img.src || '';
  }

  function classify(renderedWidth, renderedHeight, naturalWidth, naturalHeight) {
    const neededW = renderedWidth * dpr;
    const neededH = renderedHeight * dpr;
    if (naturalWidth > 0 && naturalHeight > 0 && (naturalWidth + 1 < neededW || naturalHeight + 1 < neededH)) return 'undersampled-on-screen';
    if (naturalWidth > neededW * 1.5 || naturalHeight > neededH * 1.5) return 'detail-hidden-by-page-scale';
    return 'preserve-original';
  }

  const assets = [];
  for (const img of document.images) {
    const r = img.getBoundingClientRect();
    if (r.width <= 1 || r.height <= 1) continue;
    const qualifies = (r.width >= minRw && r.height >= minRh) || (img.naturalWidth >= minNw && img.naturalHeight >= minNh);
    if (!qualifies) continue;
    const currentUrl = resolve(img.currentSrc || img.src || '');
    const bestUrl = resolve(largestSrcset(img));
    if (!currentUrl && !bestUrl) continue;
    assets.push({
      kind: 'img', currentUrl, bestUrl,
      alt: img.alt || img.getAttribute('aria-label') || '',
      renderedWidth: r.width, renderedHeight: r.height,
      naturalWidth: img.naturalWidth || 0, naturalHeight: img.naturalHeight || 0,
      documentX: r.left + scrollX, documentY: r.top + scrollY,
      devicePixelRatio: dpr,
      captureRisk: classify(r.width, r.height, img.naturalWidth || 0, img.naturalHeight || 0),
      score: Math.max(r.width * r.height, (img.naturalWidth || 0) * (img.naturalHeight || 0) / 4)
    });
  }

  if (includeBackgrounds) {
    for (const el of document.querySelectorAll('body *')) {
      const r = el.getBoundingClientRect();
      if (r.width < minRw || r.height < minRh) continue;
      const bg = getComputedStyle(el).backgroundImage;
      if (!bg || bg === 'none') continue;
      const match = bg.match(/^url\(["']?(.*?)["']?\)$/);
      if (!match || !match[1]) continue;
      const url = resolve(match[1]);
      assets.push({
        kind: 'css-background', currentUrl: url, bestUrl: url, alt: '',
        renderedWidth: r.width, renderedHeight: r.height,
        naturalWidth: 0, naturalHeight: 0,
        documentX: r.left + scrollX, documentY: r.top + scrollY,
        devicePixelRatio: dpr, captureRisk: 'preserve-background-resource',
        score: r.width * r.height * 0.8
      });
    }
  }

  assets.sort((a, b) => b.score - a.score || a.documentY - b.documentY);
  const seen = new Set();
  const unique = [];
  for (const asset of assets) {
    const key = asset.bestUrl || asset.currentUrl;
    if (!key || seen.has(key)) continue;
    seen.add(key);
    unique.push(asset);
    if (unique.length >= maxAssets) break;
  }
  return unique;
})()
""";

        using JsonDocument response = await client.EvaluateAsync(script, false, cancellationToken);
        JsonElement result = response.RootElement.GetProperty("result").GetProperty("result");
        if (!result.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<ShareXModChromeImageAsset>();
        }

        return JsonSerializer.Deserialize<List<ShareXModChromeImageAsset>>(value.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<ShareXModChromeImageAsset>();
    }

    private static async Task PreloadBestCandidatesAsync(
        ShareXModChromeCdpClient client,
        IReadOnlyCollection<ShareXModChromeImageAsset> assets,
        CancellationToken cancellationToken)
    {
        string[] urls = assets
            .Where(x => !string.IsNullOrWhiteSpace(x.BestUrl) && !string.Equals(x.BestUrl, x.CurrentUrl, StringComparison.Ordinal))
            .Select(x => x.BestUrl)
            .Where(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || x.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToArray();

        if (urls.Length == 0) return;

        string json = JsonSerializer.Serialize(urls);
        string script = $$"""
(async () => {
  const urls = {{json}};
  const results = await Promise.all(urls.map(url => new Promise(resolve => {
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => resolve({ url, ok: true, width: img.naturalWidth, height: img.naturalHeight });
    img.onerror = () => resolve({ url, ok: false });
    img.src = url;
  })));
  return results;
})()
""";

        try
        {
            using JsonDocument _ = await client.EvaluateAsync(script, true, cancellationToken);
        }
        catch
        {
            // Current resources are still useful even when higher srcset candidates cannot be preloaded.
        }
    }

    private static async Task<Dictionary<string, ResourceRef>> GetImageResourcesAsync(ShareXModChromeCdpClient client, CancellationToken cancellationToken)
    {
        Dictionary<string, ResourceRef> resources = new(StringComparer.Ordinal);
        using JsonDocument response = await client.SendCdpCommandAsync("Page.getResourceTree", null, cancellationToken);
        JsonElement tree = response.RootElement.GetProperty("result").GetProperty("frameTree");
        AddFrameResources(tree, resources);
        return resources;
    }

    private static void AddFrameResources(JsonElement tree, Dictionary<string, ResourceRef> resources)
    {
        string frameId = tree.GetProperty("frame").GetProperty("id").GetString() ?? string.Empty;
        if (tree.TryGetProperty("resources", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement resource in list.EnumerateArray())
            {
                string type = resource.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
                string mime = resource.TryGetProperty("mimeType", out JsonElement mimeElement) ? mimeElement.GetString() ?? string.Empty : string.Empty;
                if (!type.Equals("Image", StringComparison.OrdinalIgnoreCase) && !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;
                string url = resource.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                if (url.Length == 0) continue;
                resources[url] = new ResourceRef(frameId, url, mime);
            }
        }

        if (tree.TryGetProperty("childFrames", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                AddFrameResources(child, resources);
            }
        }
    }

    private static bool TryFindResource(Dictionary<string, ResourceRef> resources, string url, out ResourceRef resource)
    {
        if (resources.TryGetValue(url, out resource!)) return true;
        string withoutFragment = RemoveFragment(url);
        if (!string.Equals(withoutFragment, url, StringComparison.Ordinal) && resources.TryGetValue(withoutFragment, out resource!)) return true;
        resource = null!;
        return false;
    }

    private static string RemoveFragment(string url)
    {
        int index = url.IndexOf('#');
        return index >= 0 ? url[..index] : url;
    }

    private static bool TryDecodeDataUrl(string url, out byte[] bytes, out string mimeType)
    {
        bytes = Array.Empty<byte>();
        mimeType = string.Empty;
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        int comma = url.IndexOf(',');
        if (comma < 5) return false;
        string header = url[5..comma];
        string payload = url[(comma + 1)..];
        bool base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        mimeType = header.Split(';', 2)[0];
        if (string.IsNullOrWhiteSpace(mimeType)) mimeType = "application/octet-stream";
        try
        {
            bytes = base64 ? Convert.FromBase64String(payload) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFileName(int index, ShareXModChromeImageAsset asset, string? mimeType, string url)
    {
        string extension = ExtensionFromMime(mimeType);
        if (extension.Length == 0)
        {
            try
            {
                string candidate = Path.GetExtension(new Uri(url).AbsolutePath);
                if (candidate.Length is > 1 and <= 8) extension = candidate.ToLowerInvariant();
            }
            catch { }
        }
        if (extension.Length == 0) extension = ".bin";

        string risk = Sanitize(asset.CaptureRisk);
        string kind = asset.Kind == "css-background" ? "bg" : "img";
        string dimensions = asset.NaturalWidth > 0 && asset.NaturalHeight > 0
            ? $"{asset.NaturalWidth}x{asset.NaturalHeight}"
            : $"rendered-{Math.Round(asset.RenderedWidth).ToString(CultureInfo.InvariantCulture)}x{Math.Round(asset.RenderedHeight).ToString(CultureInfo.InvariantCulture)}";
        return $"{index:D3}-{kind}-{dimensions}-{risk}{extension}";
    }

    private static string ExtensionFromMime(string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            "image/avif" => ".avif",
            "image/bmp" => ".bmp",
            "image/x-icon" => ".ico",
            _ => string.Empty
        };
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "asset";
        StringBuilder builder = new();
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-') builder.Append(c);
            else if (builder.Length == 0 || builder[^1] != '-') builder.Append('-');
        }
        string result = builder.ToString().Trim('-');
        return result.Length == 0 ? "asset" : result;
    }

    private sealed record ResourceRef(string FrameId, string Url, string MimeType);
}
