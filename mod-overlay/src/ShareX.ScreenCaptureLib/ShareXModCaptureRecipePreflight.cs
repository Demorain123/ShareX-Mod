#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeLocatorResolution(
    int StepIndex,
    ShareXModCaptureRecipeStepKind Kind,
    string PageKey,
    string Status,
    double BestScore,
    double SecondScore,
    double UniquenessGap,
    int CandidateCount,
    string ResolvedTag,
    string ResolvedText,
    double DocumentX,
    double DocumentY,
    string[] Evidence);

internal static class ShareXModCaptureRecipePreflight
{
    public static async Task<string?> ValidateAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipe recipe,
        string outputDirectory)
    {
        try
        {
            string currentUrl = await GetCurrentUrlAsync(client);
            string currentPageKey = ComputePageKey(currentUrl);
            List<ShareXModRecipeLocatorResolution> results = new();

            foreach (ShareXModCaptureRecipeStep step in recipe.Steps)
            {
                if (step.Locator == null)
                {
                    continue;
                }

                if (!string.Equals(step.PageKey, currentPageKey, StringComparison.Ordinal))
                {
                    results.Add(new ShareXModRecipeLocatorResolution(
                        step.Index,
                        step.Kind,
                        step.PageKey,
                        "deferred-page",
                        0,
                        0,
                        0,
                        0,
                        string.Empty,
                        string.Empty,
                        0,
                        0,
                        new[] { "locator belongs to another recorded page" }));
                    continue;
                }

                results.Add(await ResolveAsync(client, step));
            }

            int strong = results.Count(x => x.Status == "strong");
            int usable = results.Count(x => x.Status == "usable");
            int ambiguous = results.Count(x => x.Status == "ambiguous");
            int missing = results.Count(x => x.Status == "missing");
            int deferred = results.Count(x => x.Status == "deferred-page");

            string overall = missing > 0 || ambiguous > 0
                ? "needs-review"
                : results.Count == 0
                    ? "no-actions"
                    : "ready-for-page";

            string path = Path.Combine(outputDirectory, "capture-recipe-preflight.json");
            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Capture Recipe Preflight",
                version = "0.6.0-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                currentUrl,
                currentPageKey,
                overall,
                counts = new
                {
                    strong,
                    usable,
                    ambiguous,
                    missing,
                    deferred
                },
                resolutions = results
            }, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ShareXModRecipeLocatorResolution> ResolveAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipeStep step)
    {
        ShareXModRecipeLocator locator = step.Locator!;
        string locatorJson = JsonSerializer.Serialize(locator);

        string script = $$"""
(() => {
  const locator = {{locatorJson}};
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const norm = value => clean(value).toLowerCase();
  const wantedText = norm(locator.Text);
  const wantedHref = String(locator.Href || '');

  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  let pool;
  if (locator.Id) {
    const exact = document.getElementById(locator.Id);
    pool = exact ? [exact] : [];
  } else if (locator.TestId) {
    const escaped = CSS.escape(locator.TestId);
    pool = [...document.querySelectorAll(`[data-testid="${escaped}"],[data-test-id="${escaped}"],[data-test="${escaped}"]`)];
  } else if (locator.Tag) {
    pool = [...document.querySelectorAll(locator.Tag.toLowerCase())];
  } else {
    pool = [...document.querySelectorAll('button,a,summary,[role],[aria-label],input,label')];
  }

  const scored = [];
  for (const el of pool.slice(0, 6000)) {
    if (!(el instanceof Element) || !visible(el)) continue;

    let score = 0;
    const id = el.id || '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '';
    const role = el.getAttribute('role') || '';
    const aria = el.getAttribute('aria-label') || '';
    const name = el.getAttribute('name') || '';
    const text = clean(el.innerText || el.textContent || '').slice(0, 220);
    const href = el.href || el.getAttribute('href') || '';
    const rel = el.getAttribute('rel') || '';

    if (locator.Tag && el.tagName.toUpperCase() === locator.Tag.toUpperCase()) score += 8;
    if (locator.Id && id === locator.Id) score += 120;
    if (locator.TestId && testId === locator.TestId) score += 105;
    if (locator.Role && role === locator.Role) score += 30;
    if (locator.AriaLabel && aria === locator.AriaLabel) score += 55;
    if (locator.Name && name === locator.Name) score += 25;
    if (locator.Rel && rel === locator.Rel) score += 25;
    if (wantedHref && href === wantedHref) score += 45;

    const currentText = norm(text);
    if (wantedText && currentText === wantedText) score += 50;
    else if (wantedText && (currentText.includes(wantedText) || wantedText.includes(currentText))) score += 22;

    const r = el.getBoundingClientRect();
    const dx = Math.abs((r.left + scrollX) - locator.DocumentX);
    const dy = Math.abs((r.top + scrollY) - locator.DocumentY);
    const geometryDistance = Math.sqrt(dx * dx + dy * dy);
    score += Math.max(0, 8 - geometryDistance / 250);

    if (score <= 0) continue;
    scored.push({
      score,
      tag: el.tagName || '',
      text,
      x: r.left + scrollX,
      y: r.top + scrollY
    });
  }

  scored.sort((a, b) => b.score - a.score);
  return {
    candidateCount: scored.length,
    best: scored[0] || null,
    second: scored[1] || null
  };
})()
""";

        try
        {
            using JsonDocument response = await client.EvaluateAsync(script, false);
            JsonElement value = response.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value");

            int count = value.TryGetProperty("candidateCount", out JsonElement countElement)
                ? countElement.GetInt32()
                : 0;

            if (count == 0 ||
                !value.TryGetProperty("best", out JsonElement best) ||
                best.ValueKind != JsonValueKind.Object)
            {
                return Missing(step, "no semantic locator candidate resolved");
            }

            double bestScore = best.GetProperty("score").GetDouble();
            double secondScore = 0;

            if (value.TryGetProperty("second", out JsonElement second) &&
                second.ValueKind == JsonValueKind.Object)
            {
                secondScore = second.GetProperty("score").GetDouble();
            }

            double gap = bestScore - secondScore;
            string status = bestScore >= 80 && gap >= 20
                ? "strong"
                : bestScore >= 45 && gap >= 8
                    ? "usable"
                    : "ambiguous";

            return new ShareXModRecipeLocatorResolution(
                step.Index,
                step.Kind,
                step.PageKey,
                status,
                bestScore,
                secondScore,
                gap,
                count,
                best.GetProperty("tag").GetString() ?? string.Empty,
                best.GetProperty("text").GetString() ?? string.Empty,
                best.GetProperty("x").GetDouble(),
                best.GetProperty("y").GetDouble(),
                status == "ambiguous"
                    ? new[] { "best locator match is not sufficiently unique" }
                    : new[] { "semantic locator re-resolved on current page" });
        }
        catch
        {
            return Missing(step, "locator resolution command failed");
        }
    }

    private static ShareXModRecipeLocatorResolution Missing(
        ShareXModCaptureRecipeStep step,
        string reason)
    {
        return new ShareXModRecipeLocatorResolution(
            step.Index,
            step.Kind,
            step.PageKey,
            "missing",
            0,
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            0,
            0,
            new[] { reason });
    }

    private static async Task<string> GetCurrentUrlAsync(ShareXModChromeCdpClient client)
    {
        using JsonDocument response = await client.EvaluateAsync("location.href", false);
        return response.RootElement
            .GetProperty("result")
            .GetProperty("result")
            .GetProperty("value")
            .GetString() ?? string.Empty;
    }

    private static string ComputePageKey(string url)
    {
        string basis = url.Split('#')[0];
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
