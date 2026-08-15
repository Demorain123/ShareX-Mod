#nullable enable

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeResolvedAnchor(
    bool Resolved,
    double Score,
    double Gap,
    double DocumentY,
    string Detail);

internal static class ShareXModRecipeRangeAnchorResolver
{
    public static async Task<ShareXModCaptureRecipeStep> ResolveStepAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipeStep step)
    {
        if (step.Kind != ShareXModCaptureRecipeStepKind.CaptureVerticalRange || step.Locator == null)
        {
            return step;
        }

        double resolvedStart = step.StartY;
        double resolvedEnd = step.EndY;
        bool startMoved = false;
        bool endMoved = false;

        ShareXModRecipeResolvedAnchor start = await ResolveAsync(client, step.Locator);
        if (start.Resolved &&
            ShareXModRecipeAnchorEvidence.TryGetStartOffset(step.Evidence, out double startOffset))
        {
            resolvedStart = Math.Max(0, start.DocumentY + startOffset);
            startMoved = true;
        }

        if (ShareXModRecipeAnchorEvidence.TryGetEndAnchor(step.Evidence, out ShareXModRecipeLocator? endLocator) &&
            endLocator != null)
        {
            ShareXModRecipeResolvedAnchor end = await ResolveAsync(client, endLocator);
            if (end.Resolved &&
                ShareXModRecipeAnchorEvidence.TryGetEndOffset(step.Evidence, out double endOffset))
            {
                resolvedEnd = Math.Max(resolvedStart + 1, end.DocumentY + endOffset);
                endMoved = true;
            }
        }

        // If only the start placeholder resolves, translate the whole recorded range by the same
        // delta instead of combining a new start with an old absolute end coordinate.
        if (startMoved && !endMoved)
        {
            double delta = resolvedStart - step.StartY;
            resolvedEnd = Math.Max(resolvedStart + 1, step.EndY + delta);
        }

        double originalLength = Math.Max(1, step.EndY - step.StartY);
        double resolvedLength = Math.Max(1, resolvedEnd - resolvedStart);
        double ratio = resolvedLength / originalLength;
        if (ratio < 0.20 || ratio > 5.0) return step;

        return step with { StartY = resolvedStart, EndY = resolvedEnd };
    }

    public static async Task<ShareXModRecipeResolvedAnchor> ResolveAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipeLocator locator)
    {
        string locatorJson = JsonSerializer.Serialize(locator);
        string script = $$"""
(() => {
  const locator = {{locatorJson}};
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const norm = value => clean(value).toLowerCase();
  const wantedText = norm(locator.Text);
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

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
    pool = [...document.querySelectorAll('article,section,h1,h2,h3,h4,p,li,figure,figcaption,img,table,blockquote,pre,div')];
  }

  const candidates = [];
  for (const el of pool.slice(0, 8000)) {
    if (!(el instanceof Element) || !visible(el)) continue;
    const r = el.getBoundingClientRect();
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
    if (locator.Role && role === locator.Role) score += 28;
    if (locator.AriaLabel && aria === locator.AriaLabel) score += 48;
    if (locator.Name && name === locator.Name) score += 24;
    if (locator.Href && href === locator.Href) score += 40;

    const currentText = norm(text);
    if (wantedText && currentText === wantedText) score += 48;
    else if (wantedText && currentText && (currentText.includes(wantedText) || wantedText.includes(currentText))) score += 20;

    const documentY = r.top + scrollY;
    const dy = Math.abs(documentY - locator.DocumentY);
    score += Math.max(0, 6 - dy / 400);

    if (score > 0) candidates.push({ score, documentY, tag: el.tagName || '', text });
  }

  candidates.sort((a, b) => b.score - a.score);
  const best = candidates[0] || null;
  const second = candidates[1] || null;
  if (!best) return { resolved: false, score: 0, gap: 0, documentY: 0, detail: 'missing' };

  const secondScore = second?.score || 0;
  const gap = best.score - secondScore;
  const resolved = best.score >= 45 && gap >= 8;
  return {
    resolved,
    score: best.score,
    gap,
    documentY: best.documentY,
    detail: resolved ? `${best.tag}:${best.text.slice(0,80)}` : 'ambiguous'
  };
})()
""";

        try
        {
            using JsonDocument response = await client.EvaluateAsync(script, false);
            JsonElement value = response.RootElement
                .GetProperty("result").GetProperty("result").GetProperty("value");

            return new ShareXModRecipeResolvedAnchor(
                value.TryGetProperty("resolved", out JsonElement resolved) && resolved.ValueKind == JsonValueKind.True,
                value.TryGetProperty("score", out JsonElement score) ? score.GetDouble() : 0,
                value.TryGetProperty("gap", out JsonElement gap) ? gap.GetDouble() : 0,
                value.TryGetProperty("documentY", out JsonElement y) ? y.GetDouble() : 0,
                value.TryGetProperty("detail", out JsonElement detail) ? detail.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return new ShareXModRecipeResolvedAnchor(false, 0, 0, 0, "resolver-error");
        }
    }
}
