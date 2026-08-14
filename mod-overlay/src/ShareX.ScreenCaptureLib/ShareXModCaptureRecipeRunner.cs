#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureRecipeRunner
{
    private sealed record Metrics(double CssWidth, double CssHeight, double ViewportHeight, double DipPerCss);
    private sealed record Part(string File, int Step, string PageKey, double StartY, double EndY, int Width, int Height);
    private sealed record ActionAudit(int Step, string Kind, string Status, string Detail, double Score, double Gap);

    public static async Task<ShareXModChromeBackgroundCaptureResult?> RunAsync(
        ShareXModChromeCdpClient client,
        ShareXModChromeTarget target,
        ShareXModV04Settings settings,
        Func<bool>? shouldStop = null)
    {
        if (!settings.CaptureRecipeAutomationEnabled ||
            string.IsNullOrWhiteSpace(settings.CaptureRecipeReplayPath) ||
            !File.Exists(settings.CaptureRecipeReplayPath))
        {
            return null;
        }

        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            ShareXModCaptureRecipe? recipe = JsonSerializer.Deserialize<ShareXModCaptureRecipe>(
                await File.ReadAllTextAsync(settings.CaptureRecipeReplayPath),
                options);

            if (recipe == null || recipe.Steps.Count == 0)
            {
                return null;
            }

            string directory = ResolveDirectory(settings);
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("capture-recipe-run", directory);

            List<Part> parts = new();
            List<ActionAudit> actions = new();
            int navigationCount = 0;
            bool stopped = false;
            bool failed = false;
            string stopReason = "recipe-complete";

            foreach (ShareXModCaptureRecipeStep step in recipe.Steps.OrderBy(x => x.Index))
            {
                if (shouldStop?.Invoke() == true)
                {
                    stopped = true;
                    stopReason = "manual-stop";
                    break;
                }

                switch (step.Kind)
                {
                    case ShareXModCaptureRecipeStepKind.PageCheckpoint:
                    {
                        string currentKey = await GetPageKeyAsync(client);
                        bool pass = string.Equals(currentKey, step.PageKey, StringComparison.Ordinal);
                        actions.Add(new ActionAudit(step.Index, "page-checkpoint", pass ? "ok" : "mismatch", currentKey, 0, 0));
                        if (!pass)
                        {
                            failed = true;
                            stopReason = "page-checkpoint-mismatch";
                        }
                        break;
                    }

                    case ShareXModCaptureRecipeStepKind.CaptureVerticalRange:
                    {
                        int before = parts.Count;
                        await CaptureVerticalRangeAsync(client, step, settings, directory, parts, shouldStop);
                        int added = parts.Count - before;
                        actions.Add(new ActionAudit(step.Index, "vertical-range", added > 0 ? "captured" : "empty", $"parts={added}", 0, 0));
                        if (added == 0)
                        {
                            failed = true;
                            stopReason = "vertical-capture-failed";
                        }
                        break;
                    }

                    case ShareXModCaptureRecipeStepKind.HorizontalSweep:
                    {
                        (bool ok, string detail, double score, double gap) =
                            await CaptureHorizontalSweepAsync(client, step, settings, directory, shouldStop);
                        actions.Add(new ActionAudit(step.Index, "horizontal-sweep", ok ? "captured" : "failed", detail, score, gap));
                        if (!ok && step.FailurePolicy.Contains("ask", StringComparison.OrdinalIgnoreCase))
                        {
                            failed = true;
                            stopReason = "horizontal-sweep-needs-review";
                        }
                        break;
                    }

                    case ShareXModCaptureRecipeStepKind.ExpandOrActivate:
                    {
                        (bool ok, string detail, double score, double gap) =
                            await ResolveAndActivateAsync(client, step, settings, expectNavigation: false);
                        actions.Add(new ActionAudit(step.Index, "activate", ok ? "executed" : "skipped", detail, score, gap));
                        if (!ok && step.FailurePolicy.Contains("report", StringComparison.OrdinalIgnoreCase))
                        {
                            // Non-critical expansion can be skipped but remains in the audit.
                        }
                        break;
                    }

                    case ShareXModCaptureRecipeStepKind.NextPage:
                    {
                        if (++navigationCount > Math.Clamp(settings.CaptureRecipeMaxNavigationCount, 1, 1000))
                        {
                            failed = true;
                            stopReason = "navigation-safety-limit";
                            actions.Add(new ActionAudit(step.Index, "next-page", "blocked", stopReason, 0, 0));
                            break;
                        }

                        (bool ok, string detail, double score, double gap) =
                            await ResolveAndActivateAsync(client, step, settings, expectNavigation: true);
                        actions.Add(new ActionAudit(step.Index, "next-page", ok ? "navigated" : "failed", detail, score, gap));
                        if (!ok)
                        {
                            failed = true;
                            stopReason = "navigation-unverified";
                        }
                        break;
                    }

                    case ShareXModCaptureRecipeStepKind.WaitStable:
                    {
                        ShareXModBrowserStabilityResult stable =
                            await ShareXModBrowserStabilityProbe.WaitForRegionAsync(
                                client,
                                settings,
                                Math.Max(0, step.StartY),
                                Math.Max(1, step.EndY - step.StartY));
                        actions.Add(new ActionAudit(step.Index, "wait-stable", stable.Stable ? "stable" : "timeout", $"elapsed={stable.ElapsedMs}", 0, 0));
                        break;
                    }
                }

                if (failed)
                {
                    break;
                }
            }

            if (parts.Count == 0)
            {
                await WriteManifestAsync(directory, recipe, target, parts, actions, stopped, failed, stopReason, null, 0, 0);
                return null;
            }

            int sourceWidth = parts[0].Width;
            bool sameWidth = parts.All(x => x.Width == sourceWidth);
            long sourceHeight = parts.Sum(x => (long)x.Height);
            string? finalPng = null;

            if (sameWidth)
            {
                try
                {
                    finalPng = Path.Combine(directory, "capture-full.png");
                    ShareXModSegmentedPngWriter.Write(
                        finalPng,
                        parts.Select(x => Path.Combine(directory, x.File)).ToArray(),
                        sourceWidth,
                        sourceHeight);
                }
                catch
                {
                    finalPng = null;
                }
            }

            string manifestPath = Path.Combine(directory, "recipe-run.json");
            await WriteManifestAsync(directory, recipe, target, parts, actions, stopped, failed, stopReason, finalPng, sourceWidth, sourceHeight);

            Bitmap preview = CreatePreview(directory, parts, sourceWidth, sourceHeight, settings.SegmentPreviewMaxHeight);
            ShareXModSegmentedOutputRegistry.Register(preview, manifestPath, finalPng ?? string.Empty, sourceWidth, sourceHeight);

            return new ShareXModChromeBackgroundCaptureResult
            {
                Preview = preview,
                DirectoryPath = directory,
                ManifestPath = manifestPath,
                PartCount = parts.Count,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                StoppedByUser = stopped,
                TruncatedBySafetyLimit = failed
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task CaptureVerticalRangeAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipeStep step,
        ShareXModV04Settings settings,
        string directory,
        List<Part> parts,
        Func<bool>? shouldStop)
    {
        Metrics metrics = await GetMetricsAsync(client);
        double y = Math.Clamp(Math.Min(step.StartY, step.EndY), 0, Math.Max(0, metrics.CssHeight - 1));
        double end = Math.Clamp(Math.Max(step.StartY, step.EndY), y + 1, metrics.CssHeight);
        int tile = Math.Clamp(settings.ChromeBackgroundTileHeight, 900, 12000);

        while (y < end - 1)
        {
            if (shouldStop?.Invoke() == true) return;

            metrics = await GetMetricsAsync(client);
            double cut = Math.Min(end, y + tile);
            double height = cut - y;
            if (height <= 1) break;

            ShareXModBrowserStabilityResult stable =
                await ShareXModBrowserStabilityProbe.WaitForRegionAsync(client, settings, y, height);

            if (settings.ChromeRepairRequireStableRegion && !stable.Stable)
            {
                // Preserve the range as failed instead of silently saving an unstable tile.
                break;
            }

            double dip = metrics.DipPerCss > 0 ? metrics.DipPerCss : 1;
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
                        x = 0d,
                        y = y * dip,
                        width = metrics.CssWidth * dip,
                        height = height * dip,
                        scale = 1d
                    }
                });

            string? base64 = response.RootElement.GetProperty("result").GetProperty("data").GetString();
            if (string.IsNullOrWhiteSpace(base64)) break;

            byte[] png = Convert.FromBase64String(base64);
            string file = $"vertical_{parts.Count + 1:D5}_step_{step.Index:D4}.png";
            string path = Path.Combine(directory, file);
            await File.WriteAllBytesAsync(path, png);

            using MemoryStream stream = new(png, writable: false);
            using Bitmap bitmap = new(stream);
            parts.Add(new Part(file, step.Index, step.PageKey, y, cut, bitmap.Width, bitmap.Height));
            y = cut;
        }
    }

    private static async Task<(bool ok, string detail, double score, double gap)> CaptureHorizontalSweepAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipeStep step,
        ShareXModV04Settings settings,
        string directory,
        Func<bool>? shouldStop)
    {
        if (step.Locator == null) return (false, "missing-locator", 0, 0);

        (bool ok, double score, double gap, string detail) = await ResolveElementAsync(
            client,
            step.Locator,
            action: "prepare-horizontal");
        if (!ok) return (false, detail, score, gap);

        string sweepDir = Path.Combine(directory, $"horizontal_step_{step.Index:D4}");
        Directory.CreateDirectory(sweepDir);

        int captured = 0;
        int maxParts = 80;
        for (int i = 0; i < maxParts; i++)
        {
            if (shouldStop?.Invoke() == true) break;

            string expression = $$"""
(() => {
  const el = window.__sharexModRecipeResolved;
  if (!(el instanceof Element)) return { done: true, missing: true };
  const max = Math.max(0, el.scrollWidth - el.clientWidth);
  const step = Math.max(1, Math.floor(el.clientWidth * 0.85));
  const x = Math.min(max, {{i}} * step);
  el.scrollLeft = x;
  const r = el.getBoundingClientRect();
  return {
    done: x >= max,
    x,
    max,
    documentX: r.left + scrollX,
    documentY: r.top + scrollY,
    width: r.width,
    height: r.height
  };
})()
""";

            using JsonDocument posResponse = await client.EvaluateAsync(expression, false);
            JsonElement value = posResponse.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
            if (value.TryGetProperty("missing", out JsonElement missing) && missing.ValueKind == JsonValueKind.True) break;

            double y = value.GetProperty("documentY").GetDouble();
            double h = value.GetProperty("height").GetDouble();
            double x = value.GetProperty("documentX").GetDouble();
            double w = value.GetProperty("width").GetDouble();
            bool done = value.GetProperty("done").GetBoolean();

            ShareXModBrowserStabilityResult stable =
                await ShareXModBrowserStabilityProbe.WaitForRegionAsync(client, settings, y, h);
            if (settings.ChromeRepairRequireStableRegion && !stable.Stable) break;

            Metrics metrics = await GetMetricsAsync(client);
            double dip = metrics.DipPerCss > 0 ? metrics.DipPerCss : 1;
            using JsonDocument shot = await client.SendCdpCommandAsync(
                "Page.captureScreenshot",
                new
                {
                    format = "png",
                    fromSurface = true,
                    captureBeyondViewport = true,
                    optimizeForSpeed = true,
                    clip = new { x = x * dip, y = y * dip, width = w * dip, height = h * dip, scale = 1d }
                });

            string? base64 = shot.RootElement.GetProperty("result").GetProperty("data").GetString();
            if (string.IsNullOrWhiteSpace(base64)) break;
            await File.WriteAllBytesAsync(Path.Combine(sweepDir, $"panel_{captured + 1:D4}.png"), Convert.FromBase64String(base64));
            captured++;
            if (done) break;
        }

        return (captured > 0, $"panels={captured}", score, gap);
    }

    private static async Task<(bool ok, string detail, double score, double gap)> ResolveAndActivateAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipeStep step,
        ShareXModV04Settings settings,
        bool expectNavigation)
    {
        if (step.Locator == null) return (false, "missing-locator", 0, 0);

        string beforeUrl = await GetUrlAsync(client);
        (bool ok, double score, double gap, string detail) = await ResolveElementAsync(
            client,
            step.Locator,
            action: "click");
        if (!ok) return (false, detail, score, gap);

        if (!expectNavigation)
        {
            await Task.Delay(120);
            return (true, "semantic-action-executed", score, gap);
        }

        int timeout = Math.Clamp(settings.CaptureRecipeActionTimeoutMs, 1000, 30000);
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeout);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            try
            {
                string afterUrl = await GetUrlAsync(client);
                if (!string.Equals(beforeUrl, afterUrl, StringComparison.Ordinal))
                {
                    return (true, $"url-changed:{afterUrl}", score, gap);
                }
            }
            catch
            {
                // Navigation can temporarily destroy the execution context; keep polling.
            }
        }

        return (false, "navigation-not-observed", score, gap);
    }

    private static async Task<(bool ok, double score, double gap, string detail)> ResolveElementAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipeLocator locator,
        string action)
    {
        string json = JsonSerializer.Serialize(locator);
        string actionJson = JsonSerializer.Serialize(action);

        string script = $$"""
(() => {
  const locator = {{json}};
  const action = {{actionJson}};
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
  } else if (locator.Tag) {
    pool = [...document.querySelectorAll(locator.Tag.toLowerCase())];
  }

  if (pool.length === 0) {
    pool = [...document.querySelectorAll('button,a,summary,details,[role],[aria-label],input,label,div')];
  }

  const scored = [];
  for (const el of pool.slice(0, 6000)) {
    if (!(el instanceof Element)) continue;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    if (r.width <= 0 || r.height <= 0 || cs.display === 'none' || cs.visibility === 'hidden') continue;

    const id = el.id || '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '';
    const role = el.getAttribute('role') || '';
    const aria = el.getAttribute('aria-label') || '';
    const name = el.getAttribute('name') || '';
    const text = clean(el.innerText || el.textContent || '').slice(0, 220);
    const href = el.href || el.getAttribute('href') || '';
    let score = 0;

    if (locator.Tag && el.tagName.toUpperCase() === locator.Tag.toUpperCase()) score += 8;
    if (locator.Id && id === locator.Id) score += 120;
    if (locator.TestId && testId === locator.TestId) score += 105;
    if (locator.Role && role === locator.Role) score += 30;
    if (locator.AriaLabel && aria === locator.AriaLabel) score += 55;
    if (locator.Name && name === locator.Name) score += 25;
    if (locator.Href && href === locator.Href) score += 45;
    const currentText = norm(text);
    if (wantedText && currentText === wantedText) score += 50;
    else if (wantedText && (currentText.includes(wantedText) || wantedText.includes(currentText))) score += 22;

    const dx = Math.abs((r.left + scrollX) - locator.DocumentX);
    const dy = Math.abs((r.top + scrollY) - locator.DocumentY);
    score += Math.max(0, 8 - Math.sqrt(dx * dx + dy * dy) / 250);
    if (score > 0) scored.push({ el, score });
  }

  scored.sort((a, b) => b.score - a.score);
  const best = scored[0];
  const second = scored[1];
  if (!best) return { ok: false, score: 0, gap: 0, detail: 'no-candidate' };
  const gap = best.score - (second?.score || 0);
  if (best.score < 45 || gap < 8) return { ok: false, score: best.score, gap, detail: 'ambiguous-locator' };

  window.__sharexModRecipeResolved = best.el;
  if (action === 'click') {
    best.el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
    best.el.click();
  }
  return { ok: true, score: best.score, gap, detail: best.el.tagName || '' };
})()
""";

        using JsonDocument response = await client.EvaluateAsync(script, false);
        JsonElement value = response.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
        return (
            value.GetProperty("ok").GetBoolean(),
            value.GetProperty("score").GetDouble(),
            value.GetProperty("gap").GetDouble(),
            value.GetProperty("detail").GetString() ?? string.Empty);
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

        using JsonDocument runtime = await client.EvaluateAsync("window.innerHeight || document.documentElement.clientHeight || 1", false);
        double viewport = runtime.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").GetDouble();
        return new Metrics(cssWidth, cssHeight, Math.Max(1, viewport), dipPerCss);
    }

    private static async Task<string> GetUrlAsync(ShareXModChromeCdpClient client)
    {
        using JsonDocument response = await client.EvaluateAsync("location.href", false);
        return response.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").GetString() ?? string.Empty;
    }

    private static async Task<string> GetPageKeyAsync(ShareXModChromeCdpClient client)
    {
        string url = await GetUrlAsync(client);
        string basis = url.Split('#')[0];
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static Bitmap CreatePreview(
        string directory,
        IReadOnlyList<Part> parts,
        int sourceWidth,
        long sourceHeight,
        int configuredMaxHeight)
    {
        int maxHeight = Math.Clamp(configuredMaxHeight, 2000, 30000);
        double scale = Math.Min(1, maxHeight / (double)Math.Max(1, sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        Bitmap preview = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(preview);
        g.Clear(Color.White);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int y = 0;

        foreach (Part part in parts)
        {
            using Bitmap image = new(Path.Combine(directory, part.File));
            int h = Math.Max(1, (int)Math.Round(image.Height * scale));
            g.DrawImage(image,
                new Rectangle(0, y, width, h),
                new Rectangle(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            y += h;
        }

        return preview;
    }

    private static async Task WriteManifestAsync(
        string directory,
        ShareXModCaptureRecipe recipe,
        ShareXModChromeTarget target,
        List<Part> parts,
        List<ActionAudit> actions,
        bool stopped,
        bool failed,
        string stopReason,
        string? finalPng,
        int sourceWidth,
        long sourceHeight)
    {
        string json = JsonSerializer.Serialize(new
        {
            format = "ShareX-Mod Capture Recipe Run",
            version = "0.6.1-dev",
            sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
            created = DateTimeOffset.Now,
            recipeVersion = recipe.Version,
            sourceRecipe = recipe.Source,
            target = new { target.Title, target.Url },
            stopped,
            failed,
            stopReason,
            sourceWidth,
            sourceHeight,
            finalPng = finalPng == null ? null : Path.GetFileName(finalPng),
            verticalPartCount = parts.Count,
            parts,
            actions
        }, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(
            Path.Combine(directory, "recipe-run.json"),
            json,
            new UTF8Encoding(false));
    }

    private static string ResolveDirectory(ShareXModV04Settings settings)
    {
        string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (!string.IsNullOrWhiteSpace(root)) return Path.Combine(root, "capture-recipe-run");

        string configured = string.IsNullOrWhiteSpace(settings.CaptureRecipeOutputDirectory)
            ? "ShareX-Mod\\CaptureRecipeRuns"
            : settings.CaptureRecipeOutputDirectory;
        string primary = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        Directory.CreateDirectory(primary);
        return Path.Combine(primary, $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
    }
}
