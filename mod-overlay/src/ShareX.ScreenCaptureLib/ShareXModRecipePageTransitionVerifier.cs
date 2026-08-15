#nullable enable

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipePageEvidence(
    string Url,
    string Title,
    string MainFingerprint,
    double DocumentHeight,
    int SemanticCount,
    double ScrollY);

internal sealed record ShareXModRecipePageTransitionResult(
    bool Changed,
    string Reason,
    ShareXModRecipePageEvidence Before,
    ShareXModRecipePageEvidence After,
    int ElapsedMs,
    int StableChangedProbes);

internal static class ShareXModRecipePageTransitionVerifier
{
    public static async Task<ShareXModRecipePageEvidence> CaptureAsync(
        ShareXModChromeCdpClient client)
    {
        const string expression = """
(() => {
  const docRoot = document.scrollingElement || document.documentElement;
  const main = document.querySelector('main,[role="main"],#content,.content,.main-content') || document.body || document.documentElement;
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  const nodes = [...main.querySelectorAll('h1,h2,h3,h4,p,li,article,section,figure,figcaption,img,table,blockquote,pre')]
    .filter(visible);

  const selected = nodes.length <= 32
    ? nodes
    : [...nodes.slice(0, 16), ...nodes.slice(-16)];

  let semantic = '';
  for (const el of selected) {
    const text = clean(el.innerText || el.textContent || '').slice(0, 180);
    const src = el.currentSrc || el.src || '';
    const href = el.href || '';
    semantic += `${el.tagName}|${text}|${src}|${href}\u001e`;
  }

  // FNV-1a is enough here: this is a change detector, not a security hash.
  let hash = 2166136261 >>> 0;
  for (let i = 0; i < semantic.length; i++) {
    hash ^= semantic.charCodeAt(i);
    hash = Math.imul(hash, 16777619) >>> 0;
  }

  return {
    url: location.href,
    title: document.title || '',
    mainFingerprint: hash.toString(16).padStart(8, '0'),
    documentHeight: docRoot?.scrollHeight || 0,
    semanticCount: nodes.length,
    scrollY: window.scrollY || docRoot?.scrollTop || 0
  };
})()
""";

        using JsonDocument response = await client.EvaluateAsync(expression, false);
        JsonElement value = response.RootElement
            .GetProperty("result")
            .GetProperty("result")
            .GetProperty("value");

        return new ShareXModRecipePageEvidence(
            value.TryGetProperty("url", out JsonElement url) ? url.GetString() ?? string.Empty : string.Empty,
            value.TryGetProperty("title", out JsonElement title) ? title.GetString() ?? string.Empty : string.Empty,
            value.TryGetProperty("mainFingerprint", out JsonElement fp) ? fp.GetString() ?? string.Empty : string.Empty,
            value.TryGetProperty("documentHeight", out JsonElement height) ? height.GetDouble() : 0,
            value.TryGetProperty("semanticCount", out JsonElement count) ? count.GetInt32() : 0,
            value.TryGetProperty("scrollY", out JsonElement sy) ? sy.GetDouble() : 0);
    }

    public static async Task<ShareXModRecipePageTransitionResult> WaitForChangeAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipePageEvidence before,
        int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 1000, 30000);
        DateTime started = DateTime.UtcNow;
        DateTime deadline = started.AddMilliseconds(timeoutMs);
        ShareXModRecipePageEvidence last = before;
        string? pendingFingerprint = null;
        int stableChangedProbes = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(160);

            ShareXModRecipePageEvidence current;
            try
            {
                current = await CaptureAsync(client);
            }
            catch
            {
                // A destroyed execution context during real navigation is expected.
                continue;
            }

            last = current;

            if (!string.Equals(before.Url, current.Url, StringComparison.Ordinal))
            {
                return new ShareXModRecipePageTransitionResult(
                    true,
                    "url-changed",
                    before,
                    current,
                    (int)(DateTime.UtcNow - started).TotalMilliseconds,
                    stableChangedProbes);
            }

            bool mainChanged =
                !string.Equals(before.MainFingerprint, current.MainFingerprint, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(current.MainFingerprint);

            bool structuralChanged =
                Math.Abs(current.DocumentHeight - before.DocumentHeight) >= 32 ||
                Math.Abs(current.SemanticCount - before.SemanticCount) >= 2;

            if (mainChanged)
            {
                if (string.Equals(pendingFingerprint, current.MainFingerprint, StringComparison.Ordinal))
                {
                    stableChangedProbes++;
                }
                else
                {
                    pendingFingerprint = current.MainFingerprint;
                    stableChangedProbes = 1;
                }

                // Require persistence across multiple probes. Structural change strengthens the
                // decision, but a same-size virtualized list is also accepted once its main
                // semantic fingerprint remains changed for three observations.
                if ((structuralChanged && stableChangedProbes >= 2) ||
                    stableChangedProbes >= 3)
                {
                    return new ShareXModRecipePageTransitionResult(
                        true,
                        structuralChanged
                            ? "same-url-semantic-and-structure-changed"
                            : "same-url-main-content-changed",
                        before,
                        current,
                        (int)(DateTime.UtcNow - started).TotalMilliseconds,
                        stableChangedProbes);
                }
            }
            else
            {
                pendingFingerprint = null;
                stableChangedProbes = 0;
            }
        }

        return new ShareXModRecipePageTransitionResult(
            false,
            "no-verified-page-transition",
            before,
            last,
            (int)(DateTime.UtcNow - started).TotalMilliseconds,
            stableChangedProbes);
    }
}
