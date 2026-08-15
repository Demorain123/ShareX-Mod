#nullable enable

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeSemanticActionResult(
    bool Resolved,
    bool Activated,
    bool Transitioned,
    double Score,
    double Gap,
    string Detail);

internal static class ShareXModRecipeSemanticActionExecutor
{
    public static async Task<ShareXModRecipeSemanticActionResult> ActivateAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipeLocator locator,
        int timeoutMs,
        bool expectTransition)
    {
        ShareXModRecipePageEvidence? before = expectTransition
            ? await ShareXModRecipePageTransitionVerifier.CaptureAsync(client)
            : null;

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
    return r.width > 0 && r.height > 0 &&
           r.bottom > 0 && r.right > 0 &&
           r.top < innerHeight && r.left < innerWidth &&
           cs.display !== 'none' && cs.visibility !== 'hidden' &&
           cs.pointerEvents !== 'none';
  };

  let pool = [];
  if (locator.Id) {
    const el = document.getElementById(locator.Id);
    if (el) pool = [el];
  } else if (locator.TestId) {
    const escaped = CSS.escape(locator.TestId);
    pool = [...document.querySelectorAll(`[data-testid="${escaped}"],[data-test-id="${escaped}"],[data-test="${escaped}"]`)];
  } else {
    pool = [...document.querySelectorAll('a,button,[role="button"],[role="link"],input[type="button"],input[type="submit"],summary')];
  }

  const candidates = [];
  for (const el of pool.slice(0, 5000)) {
    if (!(el instanceof Element) || !visible(el)) continue;
    const r = el.getBoundingClientRect();
    const id = el.id || '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '';
    const role = el.getAttribute('role') || '';
    const aria = el.getAttribute('aria-label') || '';
    const name = el.getAttribute('name') || '';
    const href = el.href || el.getAttribute('href') || '';
    const text = clean(el.innerText || el.textContent || el.value || '').slice(0, 220);
    let score = 0;

    if (locator.Tag && el.tagName.toUpperCase() === locator.Tag.toUpperCase()) score += 7;
    if (locator.Id && id === locator.Id) score += 120;
    if (locator.TestId && testId === locator.TestId) score += 105;
    if (locator.Role && role === locator.Role) score += 30;
    if (locator.AriaLabel && aria === locator.AriaLabel) score += 55;
    if (locator.Name && name === locator.Name) score += 22;
    if (locator.Href && href === locator.Href) score += 45;

    const currentText = norm(text);
    if (wantedText && currentText === wantedText) score += 52;
    else if (wantedText && currentText &&
             (currentText.includes(wantedText) || wantedText.includes(currentText))) score += 20;

    // Geometry is only a weak tie breaker.
    const dx = Math.abs((r.left + r.width * 0.5 + scrollX) - locator.DocumentX);
    const dy = Math.abs((r.top + r.height * 0.5 + scrollY) - locator.DocumentY);
    score += Math.max(0, 6 - (dx + dy) / 500);

    if (el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') score -= 80;

    if (score > 0) {
      candidates.push({
        score,
        x: r.left + r.width * 0.5,
        y: r.top + r.height * 0.5,
        tag: el.tagName || '',
        text,
        disabled: el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true'
      });
    }
  }

  candidates.sort((a,b) => b.score - a.score);
  const best = candidates[0] || null;
  const second = candidates[1] || null;
  if (!best) return { resolved:false, score:0, gap:0, x:0, y:0, detail:'missing' };

  const gap = best.score - (second?.score || 0);
  const resolved = best.score >= 48 && gap >= 8 && !best.disabled;
  return {
    resolved,
    score: best.score,
    gap,
    x: best.x,
    y: best.y,
    detail: resolved ? `${best.tag}:${best.text.slice(0,90)}` : best.disabled ? 'disabled' : 'ambiguous'
  };
})()
""";

        JsonElement value;
        try
        {
            using JsonDocument response = await client.EvaluateAsync(script, false);
            value = response.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value")
                .Clone();
        }
        catch
        {
            return new ShareXModRecipeSemanticActionResult(
                false, false, false, 0, 0, "resolver-error");
        }

        bool resolved =
            value.TryGetProperty("resolved", out JsonElement r) &&
            r.ValueKind == JsonValueKind.True;
        double score = value.TryGetProperty("score", out JsonElement s) ? s.GetDouble() : 0;
        double gap = value.TryGetProperty("gap", out JsonElement g) ? g.GetDouble() : 0;
        string detail = value.TryGetProperty("detail", out JsonElement d) ? d.GetString() ?? string.Empty : string.Empty;

        if (!resolved)
        {
            return new ShareXModRecipeSemanticActionResult(
                false, false, false, score, gap, detail);
        }

        double x = value.GetProperty("x").GetDouble();
        double y = value.GetProperty("y").GetDouble();

        try
        {
            using JsonDocument _move = await client.SendCdpCommandAsync(
                "Input.dispatchMouseEvent",
                new
                {
                    type = "mouseMoved",
                    x,
                    y,
                    button = "none",
                    buttons = 0
                });

            using JsonDocument _down = await client.SendCdpCommandAsync(
                "Input.dispatchMouseEvent",
                new
                {
                    type = "mousePressed",
                    x,
                    y,
                    button = "left",
                    buttons = 1,
                    clickCount = 1
                });

            using JsonDocument _up = await client.SendCdpCommandAsync(
                "Input.dispatchMouseEvent",
                new
                {
                    type = "mouseReleased",
                    x,
                    y,
                    button = "left",
                    buttons = 0,
                    clickCount = 1
                });
        }
        catch
        {
            return new ShareXModRecipeSemanticActionResult(
                true, false, false, score, gap, "cdp-input-failed");
        }

        if (!expectTransition)
        {
            return new ShareXModRecipeSemanticActionResult(
                true, true, true, score, gap, "activated");
        }

        ShareXModRecipePageTransitionResult transition =
            await ShareXModRecipePageTransitionVerifier.WaitForChangeAsync(
                client,
                before!,
                Math.Clamp(timeoutMs, 1000, 30000));

        return new ShareXModRecipeSemanticActionResult(
            true,
            true,
            transition.Changed,
            score,
            gap,
            transition.Reason);
    }
}
