#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModHorizontalSweepExecutionResult(
    bool Success,
    string Detail,
    double LocatorScore,
    double LocatorGap,
    int PanelCount,
    string Directory);

internal static class ShareXModCaptureRecipeHorizontalSweepExecutor
{
    private sealed record PanelGeometry(
        bool Resolved,
        double Score,
        double Gap,
        double DocumentX,
        double DocumentY,
        double ClientWidth,
        double ClientHeight,
        double ScrollWidth,
        double OriginalScrollLeft,
        string Detail);

    public static async Task<ShareXModHorizontalSweepExecutionResult> CaptureAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        ShareXModRecipeLocator locator,
        string captureDirectory,
        int uniqueStep,
        Func<bool>? shouldStop = null)
    {
        PanelGeometry geometry = await ResolveAsync(client, locator, null, scrollIntoView: true);
        if (!geometry.Resolved)
        {
            return new ShareXModHorizontalSweepExecutionResult(
                false,
                geometry.Detail,
                geometry.Score,
                geometry.Gap,
                0,
                string.Empty);
        }

        if (geometry.ScrollWidth <= geometry.ClientWidth + 2)
        {
            return new ShareXModHorizontalSweepExecutionResult(
                false,
                "resolved-container-is-not-horizontally-scrollable",
                geometry.Score,
                geometry.Gap,
                0,
                string.Empty);
        }

        string directory = Path.Combine(
            captureDirectory,
            $"horizontal_step_{uniqueStep:D8}");
        Directory.CreateDirectory(directory);

        double overlapRatio = Math.Clamp(
            settings.CaptureRecipeHorizontalCaptureOverlapRatio,
            0.05,
            0.60);
        double stride = Math.Max(40, geometry.ClientWidth * (1 - overlapRatio));
        double maxLeft = Math.Max(0, geometry.ScrollWidth - geometry.ClientWidth);
        int maxPanels = Math.Clamp(settings.CaptureRecipeHorizontalMaxPanels, 2, 500);

        List<double> positions = new() { 0 };
        for (double left = stride; left < maxLeft - 1 && positions.Count < maxPanels - 1; left += stride)
        {
            positions.Add(left);
        }
        if (positions.Count < maxPanels && maxLeft > 0 && Math.Abs(positions[^1] - maxLeft) > 1)
        {
            positions.Add(maxLeft);
        }

        int captured = 0;
        try
        {
            foreach (double position in positions)
            {
                if (shouldStop?.Invoke() == true)
                {
                    return new ShareXModHorizontalSweepExecutionResult(
                        false,
                        "manual-stop",
                        geometry.Score,
                        geometry.Gap,
                        captured,
                        directory);
                }

                PanelGeometry current = await ResolveAsync(
                    client,
                    locator,
                    position,
                    scrollIntoView: true);

                if (!current.Resolved)
                {
                    return new ShareXModHorizontalSweepExecutionResult(
                        false,
                        "container-lost-during-horizontal-sweep",
                        current.Score,
                        current.Gap,
                        captured,
                        directory);
                }

                ShareXModBrowserStabilityResult stable =
                    await ShareXModBrowserStabilityProbe.WaitForRegionAsync(
                        client,
                        settings,
                        current.DocumentY,
                        current.ClientHeight);

                if (settings.ChromeRepairRequireStableRegion && !stable.Stable)
                {
                    return new ShareXModHorizontalSweepExecutionResult(
                        false,
                        "horizontal-panel-not-stable",
                        current.Score,
                        current.Gap,
                        captured,
                        directory);
                }

                double dipPerCss = await GetDipPerCssAsync(client);
                await ShareXModChromeOverlayDeduplicator.SetHiddenAsync(client, true);
                try
                {
                    using JsonDocument response = await client.SendCdpCommandAsync(
                        "Page.captureScreenshot",
                        new
                        {
                            format = "png",
                            fromSurface = true,
                            captureBeyondViewport = true,
                            optimizeForSpeed = true,
                            clip = new
                            {
                                x = current.DocumentX * dipPerCss,
                                y = current.DocumentY * dipPerCss,
                                width = current.ClientWidth * dipPerCss,
                                height = current.ClientHeight * dipPerCss,
                                scale = 1d
                            }
                        });

                    string? base64 = response.RootElement
                        .GetProperty("result")
                        .GetProperty("data")
                        .GetString();
                    if (string.IsNullOrWhiteSpace(base64))
                    {
                        return new ShareXModHorizontalSweepExecutionResult(
                            false,
                            "horizontal-screenshot-empty",
                            current.Score,
                            current.Gap,
                            captured,
                            directory);
                    }

                    string file = Path.Combine(directory, $"panel_{captured + 1:D4}.png");
                    await File.WriteAllBytesAsync(file, Convert.FromBase64String(base64));
                    captured++;
                }
                finally
                {
                    await ShareXModChromeOverlayDeduplicator.SetHiddenAsync(client, false);
                }
            }
        }
        finally
        {
            try
            {
                await ResolveAsync(
                    client,
                    locator,
                    geometry.OriginalScrollLeft,
                    scrollIntoView: false);
            }
            catch
            {
            }
        }

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "sweep.json"),
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Horizontal Sweep",
                    version = "0.9.0-dev",
                    created = DateTimeOffset.Now,
                    uniqueStep,
                    locator,
                    geometry,
                    overlapRatio,
                    positions,
                    panelCount = captured
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }

        return new ShareXModHorizontalSweepExecutionResult(
            captured > 0,
            captured > 0 ? "captured" : "no-panels",
            geometry.Score,
            geometry.Gap,
            captured,
            directory);
    }

    private static async Task<PanelGeometry> ResolveAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipeLocator locator,
        double? setScrollLeft,
        bool scrollIntoView)
    {
        string locatorJson = JsonSerializer.Serialize(locator);
        string leftJson = setScrollLeft.HasValue
            ? setScrollLeft.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "null";
        string scrollJson = scrollIntoView ? "true" : "false";

        string script = $$"""
(async () => {
  const locator = {{locatorJson}};
  const requestedLeft = {{leftJson}};
  const shouldScrollIntoView = {{scrollJson}};
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const norm = value => clean(value).toLowerCase();
  const wantedText = norm(locator.Text);

  let pool = [];
  if (locator.Id) {
    const el = document.getElementById(locator.Id);
    if (el) pool = [el];
  } else if (locator.TestId) {
    const escaped = CSS.escape(locator.TestId);
    pool = [...document.querySelectorAll(`[data-testid="${escaped}"],[data-test-id="${escaped}"],[data-test="${escaped}"]`)];
  } else {
    pool = [...document.querySelectorAll('div,section,ul,ol,nav,table,[role="region"],[role="list"],[role="grid"]')];
  }

  const candidates = [];
  for (const el of pool.slice(0,6000)) {
    if (!(el instanceof HTMLElement)) continue;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    if (r.width < 80 || r.height < 24 || cs.display === 'none' || cs.visibility === 'hidden') continue;
    if (el.scrollWidth <= el.clientWidth + 2) continue;

    const id = el.id || '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '';
    const role = el.getAttribute('role') || '';
    const aria = el.getAttribute('aria-label') || '';
    const text = clean(el.innerText || el.textContent || '').slice(0,220);
    let score = 10;
    if (locator.Tag && el.tagName.toUpperCase() === locator.Tag.toUpperCase()) score += 7;
    if (locator.Id && id === locator.Id) score += 120;
    if (locator.TestId && testId === locator.TestId) score += 105;
    if (locator.Role && role === locator.Role) score += 28;
    if (locator.AriaLabel && aria === locator.AriaLabel) score += 52;
    const currentText = norm(text);
    if (wantedText && currentText === wantedText) score += 45;
    else if (wantedText && currentText && (currentText.includes(wantedText) || wantedText.includes(currentText))) score += 18;
    const dy = Math.abs((r.top + scrollY) - locator.DocumentY);
    score += Math.max(0, 5 - dy / 500);
    candidates.push({ el, score, text });
  }

  candidates.sort((a,b) => b.score - a.score);
  const best = candidates[0] || null;
  const second = candidates[1] || null;
  if (!best) return { resolved:false, score:0, gap:0, detail:'missing' };
  const gap = best.score - (second?.score || 0);
  if (best.score < 42 || gap < 7) {
    return { resolved:false, score:best.score, gap, detail:'ambiguous' };
  }

  const el = best.el;
  if (shouldScrollIntoView) {
    el.scrollIntoView({ block:'center', inline:'nearest', behavior:'instant' });
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  }
  const originalScrollLeft = el.scrollLeft;
  if (requestedLeft !== null) {
    el.scrollLeft = Math.max(0, Math.min(requestedLeft, el.scrollWidth - el.clientWidth));
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  }

  const rect = el.getBoundingClientRect();
  return {
    resolved:true,
    score:best.score,
    gap,
    documentX:rect.left + scrollX,
    documentY:rect.top + scrollY,
    clientWidth:rect.width,
    clientHeight:rect.height,
    scrollWidth:el.scrollWidth,
    originalScrollLeft,
    detail:`${el.tagName}:${best.text.slice(0,80)}`
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

            bool resolved = value.TryGetProperty("resolved", out JsonElement r) && r.ValueKind == JsonValueKind.True;
            return new PanelGeometry(
                resolved,
                value.TryGetProperty("score", out JsonElement score) ? score.GetDouble() : 0,
                value.TryGetProperty("gap", out JsonElement gap) ? gap.GetDouble() : 0,
                value.TryGetProperty("documentX", out JsonElement x) ? x.GetDouble() : 0,
                value.TryGetProperty("documentY", out JsonElement y) ? y.GetDouble() : 0,
                value.TryGetProperty("clientWidth", out JsonElement width) ? width.GetDouble() : 0,
                value.TryGetProperty("clientHeight", out JsonElement height) ? height.GetDouble() : 0,
                value.TryGetProperty("scrollWidth", out JsonElement scrollWidth) ? scrollWidth.GetDouble() : 0,
                value.TryGetProperty("originalScrollLeft", out JsonElement original) ? original.GetDouble() : 0,
                value.TryGetProperty("detail", out JsonElement detail) ? detail.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return new PanelGeometry(false, 0, 0, 0, 0, 0, 0, 0, 0, "resolver-error");
        }
    }

    private static async Task<double> GetDipPerCssAsync(ShareXModChromeCdpClient client)
    {
        try
        {
            using JsonDocument layout = await client.SendCdpCommandAsync("Page.getLayoutMetrics");
            JsonElement result = layout.RootElement.GetProperty("result");
            if (result.TryGetProperty("cssContentSize", out JsonElement css) &&
                result.TryGetProperty("contentSize", out JsonElement dip))
            {
                double cssWidth = css.GetProperty("width").GetDouble();
                double dipWidth = dip.GetProperty("width").GetDouble();
                if (cssWidth > 0 && dipWidth > 0) return dipWidth / cssWidth;
            }
        }
        catch
        {
        }

        return 1;
    }
}
