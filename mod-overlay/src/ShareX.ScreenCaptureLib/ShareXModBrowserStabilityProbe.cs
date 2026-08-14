#nullable enable

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModBrowserStabilityResult(
    bool Stable,
    bool TimedOut,
    int ElapsedMs,
    int MutationCount,
    int LayoutShiftCount,
    int PendingImages,
    double ScrollHeight,
    string FontStatus,
    string[] Notes);

internal static class ShareXModBrowserStabilityProbe
{
    public static async Task<ShareXModBrowserStabilityResult> WaitForRegionAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        double cssY,
        double cssHeight)
    {
        int quietMs = Math.Clamp(settings.ChromeRepairStabilityQuietMs, 100, 2500);
        int maxWaitMs = Math.Clamp(settings.ChromeRepairStabilityMaxWaitMs, quietMs + 250, 30000);
        int probeMs = Math.Clamp(settings.ChromeRepairStabilityProbeMs, 50, 1000);
        int imageDecodeBudgetMs = Math.Clamp(settings.ChromeRepairImageDecodeBudgetMs, 0, 5000);

        string sy = cssY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string sh = cssHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        string script = $$"""
(async () => {
  const y = {{sy}};
  const h = Math.max(1, {{sh}});
  const quietMs = {{quietMs}};
  const maxWaitMs = {{maxWaitMs}};
  const probeMs = {{probeMs}};
  const decodeBudgetMs = {{imageDecodeBudgetMs}};

  const started = performance.now();
  const center = Math.max(0, y + h * 0.5 - innerHeight * 0.5);
  scrollTo({ top: center, left: scrollX, behavior: 'instant' });
  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));

  let mutationCount = 0;
  let layoutShiftCount = 0;
  let lastActivity = performance.now();
  let lastHeight = (document.scrollingElement || document.documentElement).scrollHeight;
  let stableHeightPasses = 0;
  const notes = [];

  const mutationObserver = new MutationObserver(() => {
    mutationCount++;
    lastActivity = performance.now();
  });

  try {
    mutationObserver.observe(document.documentElement, {
      subtree: true,
      childList: true,
      attributes: true,
      characterData: true
    });
  } catch (e) {
    notes.push('mutation-observer-unavailable');
  }

  let shiftObserver = null;
  try {
    if (PerformanceObserver.supportedEntryTypes?.includes('layout-shift')) {
      shiftObserver = new PerformanceObserver(list => {
        const entries = list.getEntries();
        if (entries.length > 0) {
          layoutShiftCount += entries.length;
          lastActivity = performance.now();
        }
      });
      shiftObserver.observe({ type: 'layout-shift', buffered: false });
    }
  } catch (e) {
    notes.push('layout-shift-observer-unavailable');
  }

  const regionImages = () => [...document.images].filter(img => {
    const r = img.getBoundingClientRect();
    const documentTop = r.top + scrollY;
    const documentBottom = r.bottom + scrollY;
    return documentBottom >= y - innerHeight &&
           documentTop <= y + h + innerHeight;
  });

  let stable = false;
  let pendingImages = 0;

  while (performance.now() - started < maxWaitMs) {
    const images = regionImages();
    pendingImages = images.filter(img =>
      !img.complete || (img.currentSrc && img.naturalWidth <= 0)
    ).length;

    const currentHeight =
      (document.scrollingElement || document.documentElement).scrollHeight;

    if (Math.abs(currentHeight - lastHeight) <= 1) {
      stableHeightPasses++;
    } else {
      stableHeightPasses = 0;
      lastHeight = currentHeight;
      lastActivity = performance.now();
    }

    const fontStatus = document.fonts?.status || 'unknown';
    const quietFor = performance.now() - lastActivity;

    if (pendingImages === 0 &&
        fontStatus !== 'loading' &&
        quietFor >= quietMs &&
        stableHeightPasses >= 2) {
      stable = true;
      break;
    }

    await new Promise(r => setTimeout(r, probeMs));
  }

  if (document.fonts?.ready) {
    try {
      await Promise.race([
        document.fonts.ready,
        new Promise(resolve => setTimeout(resolve, Math.min(1000, maxWaitMs)))
      ]);
    } catch {}
  }

  if (decodeBudgetMs > 0) {
    try {
      const images = regionImages().filter(img => img.complete && img.naturalWidth > 0);
      const decodePromise = Promise.allSettled(
        images.map(img => typeof img.decode === 'function'
          ? img.decode().catch(() => {})
          : Promise.resolve())
      );
      await Promise.race([
        decodePromise,
        new Promise(resolve => setTimeout(resolve, decodeBudgetMs))
      ]);
    } catch {}
  }

  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));

  mutationObserver.disconnect();
  try { shiftObserver?.disconnect(); } catch {}

  const elapsedMs = Math.round(performance.now() - started);
  const finalHeight =
    (document.scrollingElement || document.documentElement).scrollHeight;
  pendingImages = regionImages().filter(img =>
    !img.complete || (img.currentSrc && img.naturalWidth <= 0)
  ).length;

  const fontStatus = document.fonts?.status || 'unknown';
  const timedOut = !stable;

  if (timedOut) notes.push('stability-timeout');
  if (pendingImages > 0) notes.push('pending-images');
  if (fontStatus === 'loading') notes.push('fonts-loading');

  return {
    stable,
    timedOut,
    elapsedMs,
    mutationCount,
    layoutShiftCount,
    pendingImages,
    scrollHeight: finalHeight,
    fontStatus,
    notes
  };
})()
""";

        try
        {
            using JsonDocument response = await client.EvaluateAsync(script, true);
            JsonElement value = response.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value");

            return new ShareXModBrowserStabilityResult(
                value.TryGetProperty("stable", out JsonElement stable) && stable.GetBoolean(),
                value.TryGetProperty("timedOut", out JsonElement timedOut) && timedOut.GetBoolean(),
                value.TryGetProperty("elapsedMs", out JsonElement elapsed) ? elapsed.GetInt32() : 0,
                value.TryGetProperty("mutationCount", out JsonElement mutations) ? mutations.GetInt32() : 0,
                value.TryGetProperty("layoutShiftCount", out JsonElement shifts) ? shifts.GetInt32() : 0,
                value.TryGetProperty("pendingImages", out JsonElement pending) ? pending.GetInt32() : 0,
                value.TryGetProperty("scrollHeight", out JsonElement height) ? height.GetDouble() : 0,
                value.TryGetProperty("fontStatus", out JsonElement fonts) ? fonts.GetString() ?? "unknown" : "unknown",
                ReadStrings(value, "notes"));
        }
        catch
        {
            return new ShareXModBrowserStabilityResult(
                false,
                true,
                0,
                0,
                0,
                0,
                0,
                "unknown",
                new[] { "stability-probe-failed" });
        }
    }

    private static string[] ReadStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new System.Collections.Generic.List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
        }

        return result.ToArray();
    }
}
