#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeRangeEvidence(
    int Step,
    string PageKey,
    double StartY,
    double EndY,
    string Fingerprint,
    int SemanticCount,
    int ImageCount,
    int PendingImages,
    string FontStatus,
    double DocumentHeight,
    DateTimeOffset CapturedAt);

internal sealed record ShareXModRecipeStaleRange(
    ShareXModCaptureRecipeStep OriginalStep,
    ShareXModCaptureRecipeStep EffectiveStep,
    ShareXModRecipeRangeEvidence Captured,
    ShareXModRecipeRangeEvidence Current,
    string[] Reasons);

internal static class ShareXModCaptureRecipePageGuard
{
    internal sealed class Session
    {
        private readonly ShareXModChromeCdpClient client;
        private readonly ShareXModV04Settings settings;
        private readonly Dictionary<int, (ShareXModCaptureRecipeStep step, ShareXModRecipeRangeEvidence evidence)> ranges = new();

        public Session(
            ShareXModChromeCdpClient client,
            ShareXModV04Settings settings)
        {
            this.client = client;
            this.settings = settings;
        }

        public async Task RecordAsync(
            ShareXModCaptureRecipeStep originalStep,
            ShareXModCaptureRecipeStep effectiveStep)
        {
            if (!settings.CaptureRecipePageGuardEnabled ||
                originalStep.Kind != ShareXModCaptureRecipeStepKind.CaptureVerticalRange)
            {
                return;
            }

            ShareXModRecipeRangeEvidence evidence =
                await CaptureEvidenceAsync(client, effectiveStep);

            ranges[originalStep.Index] = (originalStep, evidence);
        }

        public async Task<IReadOnlyList<ShareXModRecipeStaleRange>> RevalidateAsync(
            string pageKey)
        {
            if (!settings.CaptureRecipePageGuardEnabled)
            {
                return Array.Empty<ShareXModRecipeStaleRange>();
            }

            List<ShareXModRecipeStaleRange> stale = new();

            foreach ((ShareXModCaptureRecipeStep original, ShareXModRecipeRangeEvidence captured) in
                     ranges.Values
                         .Where(x => string.Equals(x.step.PageKey, pageKey, StringComparison.Ordinal))
                         .OrderBy(x => x.step.Index))
            {
                ShareXModCaptureRecipeStep effective =
                    await ShareXModRecipeRangeAnchorResolver.ResolveStepAsync(client, original);

                ShareXModRecipeRangeEvidence current =
                    await CaptureEvidenceAsync(client, effective);

                List<string> reasons = Compare(captured, current, settings);
                if (reasons.Count == 0)
                {
                    continue;
                }

                stale.Add(new ShareXModRecipeStaleRange(
                    original,
                    effective,
                    captured,
                    current,
                    reasons.ToArray()));
            }

            return stale;
        }

        public void ForgetStep(int step) => ranges.Remove(step);
    }

    public static Session Create(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings) =>
        new(client, settings);

    private static List<string> Compare(
        ShareXModRecipeRangeEvidence captured,
        ShareXModRecipeRangeEvidence current,
        ShareXModV04Settings settings)
    {
        List<string> reasons = new();

        double shiftThreshold = Math.Clamp(
            settings.CaptureRecipePageGuardBoundaryShiftCss,
            1,
            1000);

        if (Math.Abs(captured.StartY - current.StartY) >= shiftThreshold ||
            Math.Abs(captured.EndY - current.EndY) >= shiftThreshold)
        {
            reasons.Add("semantic-boundary-moved");
        }

        if (!string.Equals(
                captured.Fingerprint,
                current.Fingerprint,
                StringComparison.Ordinal) &&
            captured.SemanticCount > 0 &&
            current.SemanticCount > 0)
        {
            reasons.Add("range-semantic-fingerprint-changed");
        }

        if (current.PendingImages > 0)
        {
            reasons.Add("range-has-pending-images");
        }

        if (current.FontStatus.Equals("loading", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("fonts-still-loading");
        }

        int semanticDelta = Math.Abs(current.SemanticCount - captured.SemanticCount);
        if (semanticDelta >= Math.Clamp(settings.CaptureRecipePageGuardSemanticCountDelta, 1, 1000))
        {
            reasons.Add("range-semantic-count-changed");
        }

        int imageDelta = Math.Abs(current.ImageCount - captured.ImageCount);
        if (imageDelta >= Math.Clamp(settings.CaptureRecipePageGuardImageCountDelta, 1, 1000))
        {
            reasons.Add("range-image-count-changed");
        }

        return reasons;
    }

    private static async Task<ShareXModRecipeRangeEvidence> CaptureEvidenceAsync(
        ShareXModChromeCdpClient client,
        ShareXModCaptureRecipeStep step)
    {
        string start = step.StartY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string end = step.EndY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        string script = $$"""
(() => {
  const startY = {{start}};
  const endY = Math.max(startY + 1, {{end}});
  const root = document.scrollingElement || document.documentElement;
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();

  const nodes = [...document.querySelectorAll(
    'article,section,h1,h2,h3,h4,p,li,figure,figcaption,img,video,table,blockquote,pre,details,summary,[role="article"],[data-post-number]'
  )].filter(el => {
    const r = el.getBoundingClientRect();
    const y1 = r.top + scrollY;
    const y2 = r.bottom + scrollY;
    const cs = getComputedStyle(el);
    return r.width > 0 && r.height > 0 &&
           cs.display !== 'none' && cs.visibility !== 'hidden' &&
           y2 >= startY && y1 <= endY;
  });

  let semantic = '';
  let imageCount = 0;
  let pendingImages = 0;

  for (const el of nodes.slice(0, 1200)) {
    const r = el.getBoundingClientRect();
    const y = r.top + scrollY;
    const h = r.height;
    const text = clean(el.innerText || el.textContent || '').slice(0, 180);
    const src = el.currentSrc || el.src || '';
    const id = el.id || '';
    const post = el.getAttribute('data-post-number') || '';
    semantic += `${el.tagName}|${id}|${post}|${text}|${src}|${Math.round(y)}|${Math.round(h)}\u001e`;

    if (el instanceof HTMLImageElement) {
      imageCount++;
      if (!el.complete || (el.currentSrc && el.naturalWidth <= 0)) pendingImages++;
    }
  }

  // Include images nested inside semantic containers too.
  for (const img of document.images) {
    const r = img.getBoundingClientRect();
    const y1 = r.top + scrollY;
    const y2 = r.bottom + scrollY;
    if (y2 < startY || y1 > endY) continue;
    if (!nodes.includes(img)) imageCount++;
    if (!img.complete || (img.currentSrc && img.naturalWidth <= 0)) pendingImages++;
  }

  let hash = 2166136261 >>> 0;
  for (let i = 0; i < semantic.length; i++) {
    hash ^= semantic.charCodeAt(i);
    hash = Math.imul(hash, 16777619) >>> 0;
  }

  return {
    fingerprint: hash.toString(16).padStart(8, '0'),
    semanticCount: nodes.length,
    imageCount,
    pendingImages,
    fontStatus: document.fonts?.status || 'unknown',
    documentHeight: root?.scrollHeight || 0
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

            return new ShareXModRecipeRangeEvidence(
                step.Index,
                step.PageKey,
                step.StartY,
                step.EndY,
                value.TryGetProperty("fingerprint", out JsonElement fp) ? fp.GetString() ?? string.Empty : string.Empty,
                value.TryGetProperty("semanticCount", out JsonElement count) ? count.GetInt32() : 0,
                value.TryGetProperty("imageCount", out JsonElement images) ? images.GetInt32() : 0,
                value.TryGetProperty("pendingImages", out JsonElement pending) ? pending.GetInt32() : 0,
                value.TryGetProperty("fontStatus", out JsonElement fonts) ? fonts.GetString() ?? "unknown" : "unknown",
                value.TryGetProperty("documentHeight", out JsonElement height) ? height.GetDouble() : 0,
                DateTimeOffset.Now);
        }
        catch
        {
            return new ShareXModRecipeRangeEvidence(
                step.Index,
                step.PageKey,
                step.StartY,
                step.EndY,
                string.Empty,
                0,
                0,
                0,
                "unknown",
                0,
                DateTimeOffset.Now);
        }
    }
}
