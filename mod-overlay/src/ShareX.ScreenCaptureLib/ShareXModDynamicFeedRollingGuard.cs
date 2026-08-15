#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModDynamicFeedGuardReplacementPart(
    string File,
    double StartY,
    double EndY,
    int Width,
    int Height);

internal sealed record ShareXModDynamicFeedGuardReplacement(
    string OldFile,
    IReadOnlyList<ShareXModDynamicFeedGuardReplacementPart> NewParts,
    string Reason);

internal sealed record ShareXModDynamicFeedGuardPassResult(
    IReadOnlyList<ShareXModDynamicFeedGuardReplacement> Replacements,
    int SealedCount,
    int PendingCount,
    int UnresolvedCount);

internal static class ShareXModDynamicFeedRollingGuard
{
    private sealed record Evidence(
        string Fingerprint,
        int SemanticCount,
        int ImageCount,
        int PendingImages,
        string FontStatus,
        double DocumentHeight,
        DateTimeOffset CapturedAt);

    private sealed class Item
    {
        public string File { get; init; } = string.Empty;
        public double StartY { get; init; }
        public double EndY { get; init; }
        public Evidence Captured { get; set; } = null!;
        public bool Sealed { get; set; }
        public bool Unresolved { get; set; }
        public int Checks { get; set; }
        public string LastReason { get; set; } = string.Empty;
    }

    internal sealed class Session
    {
        private readonly ShareXModChromeCdpClient client;
        private readonly ShareXModV04Settings settings;
        private readonly string outputDirectory;
        private readonly string pageKey;
        private readonly List<Item> items = new();

        public Session(
            ShareXModChromeCdpClient client,
            ShareXModV04Settings settings,
            string outputDirectory,
            string pageKey)
        {
            this.client = client;
            this.settings = settings;
            this.outputDirectory = outputDirectory;
            this.pageKey = pageKey;
        }

        public async Task RecordAsync(
            string file,
            double startY,
            double endY)
        {
            if (!settings.DynamicFeedRollingGuardEnabled ||
                string.IsNullOrWhiteSpace(file) ||
                endY <= startY)
            {
                return;
            }

            Evidence evidence = await CaptureEvidenceAsync(client, startY, endY);
            items.RemoveAll(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));
            items.Add(new Item
            {
                File = file,
                StartY = startY,
                EndY = endY,
                Captured = evidence
            });

            TrimResolvedHistory();
            WriteAudit();
        }

        public async Task<ShareXModDynamicFeedGuardPassResult> VerifyDueAsync(
            double currentY,
            bool flush,
            Func<bool>? shouldStop = null)
        {
            if (!settings.DynamicFeedRollingGuardEnabled)
            {
                return new ShareXModDynamicFeedGuardPassResult(
                    Array.Empty<ShareXModDynamicFeedGuardReplacement>(),
                    0,
                    0,
                    0);
            }

            List<ShareXModDynamicFeedGuardReplacement> replacements = new();
            int sealedNow = 0;

            int maxPending = Math.Clamp(settings.DynamicFeedGuardMaxPendingRanges, 2, 24);
            double delayRatio = Math.Clamp(settings.DynamicFeedGuardDelayRangeRatio, 0.15, 3.0);
            double maxAgeRatio = Math.Clamp(settings.DynamicFeedGuardMaxAgeRangeRatio, delayRatio + 0.25, 8.0);

            List<Item> pending = items
                .Where(x => !x.Sealed && !x.Unresolved)
                .OrderBy(x => x.EndY)
                .ToList();

            foreach (Item item in pending)
            {
                if (shouldStop?.Invoke() == true && !flush)
                {
                    break;
                }

                double rangeHeight = Math.Max(1, item.EndY - item.StartY);
                double age = Math.Max(0, currentY - item.EndY);
                bool due = flush ||
                           age >= rangeHeight * delayRatio ||
                           item.Captured.PendingImages > 0 ||
                           item.Captured.FontStatus.Equals("loading", StringComparison.OrdinalIgnoreCase) ||
                           pending.Count > maxPending;

                if (!due)
                {
                    continue;
                }

                Evidence current = await CaptureEvidenceAsync(
                    client,
                    item.StartY,
                    item.EndY);
                item.Checks++;

                List<string> reasons = Compare(item.Captured, current, settings);
                bool virtualized = current.SemanticCount == 0 &&
                                   item.Captured.SemanticCount > 0;

                if (virtualized)
                {
                    item.Unresolved = true;
                    item.LastReason = "range-virtualized-before-seal";
                    continue;
                }

                bool currentReady = current.PendingImages == 0 &&
                                    !current.FontStatus.Equals("loading", StringComparison.OrdinalIgnoreCase);

                if (reasons.Count == 0 && currentReady)
                {
                    item.Sealed = true;
                    item.Captured = current;
                    item.LastReason = "stable-sealed";
                    sealedNow++;
                    continue;
                }

                bool mustRepair = flush ||
                                  age >= rangeHeight * maxAgeRatio ||
                                  (reasons.Count > 0 && currentReady);

                if (!mustRepair)
                {
                    item.Captured = current;
                    item.LastReason = reasons.Count == 0
                        ? "waiting-for-resources"
                        : "changed-waiting-for-settle";
                    continue;
                }

                ShareXModCaptureRecipeStep synthetic = new(
                    0,
                    ShareXModCaptureRecipeStepKind.CaptureVerticalRange,
                    pageKey,
                    item.StartY,
                    item.EndY,
                    0,
                    0,
                    null,
                    true,
                    reasons.Count == 0
                        ? new[] { "dynamic-feed-rolling-guard" }
                        : reasons.ToArray(),
                    "mark-unresolved");

                ShareXModRecipeExactRangeResult repaired =
                    await ShareXModCaptureRecipeExactRange.CaptureAsync(
                        client,
                        settings,
                        synthetic,
                        outputDirectory,
                        $"feed_guard_{DateTime.Now:HHmmssfff}_{Math.Round(item.StartY):0}",
                        includeOverlayOnFirstTile: false,
                        shouldStop: null);

                if (!repaired.Success)
                {
                    item.Unresolved = true;
                    item.LastReason = "guard-recapture-failed:" + repaired.Detail;
                    continue;
                }

                IReadOnlyList<ShareXModDynamicFeedGuardReplacementPart> newParts =
                    repaired.Parts
                        .Select(x => new ShareXModDynamicFeedGuardReplacementPart(
                            x.File,
                            x.StartY,
                            x.EndY,
                            x.Width,
                            x.Height))
                        .ToArray();

                replacements.Add(new ShareXModDynamicFeedGuardReplacement(
                    item.File,
                    newParts,
                    reasons.Count == 0
                        ? "resource-timeout-recapture"
                        : string.Join(',', reasons)));

                item.Sealed = true;
                item.Captured = await CaptureEvidenceAsync(
                    client,
                    item.StartY,
                    item.EndY);
                item.LastReason = "recaptured-and-sealed";
                sealedNow++;
            }

            TrimResolvedHistory();
            WriteAudit();

            return new ShareXModDynamicFeedGuardPassResult(
                replacements,
                sealedNow,
                items.Count(x => !x.Sealed && !x.Unresolved),
                items.Count(x => x.Unresolved));
        }

        private void TrimResolvedHistory()
        {
            int keep = Math.Clamp(settings.DynamicFeedGuardAuditHistoryRanges, 8, 256);
            List<Item> resolved = items
                .Where(x => x.Sealed || x.Unresolved)
                .OrderByDescending(x => x.EndY)
                .Skip(keep)
                .ToList();

            foreach (Item item in resolved)
            {
                items.Remove(item);
            }
        }

        private void WriteAudit()
        {
            try
            {
                string directory = Path.Combine(outputDirectory, "rolling-guard");
                Directory.CreateDirectory(directory);
                ShareXModCaptureSessionContext.RegisterComponent("dynamic-feed-rolling-guard", directory);

                File.WriteAllText(
                    Path.Combine(directory, "rolling-guard.json"),
                    JsonSerializer.Serialize(new
                    {
                        format = "ShareX-Mod Dynamic Feed Rolling Guard",
                        version = "0.10.0-dev",
                        sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                        updated = DateTimeOffset.Now,
                        trackedCount = items.Count,
                        sealedCount = items.Count(x => x.Sealed),
                        pendingCount = items.Count(x => !x.Sealed && !x.Unresolved),
                        unresolvedCount = items.Count(x => x.Unresolved),
                        ranges = items.Select(x => new
                        {
                            x.File,
                            x.StartY,
                            x.EndY,
                            x.Sealed,
                            x.Unresolved,
                            x.Checks,
                            x.LastReason,
                            captured = x.Captured
                        })
                    }, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public static Session Create(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string outputDirectory,
        string pageKey) =>
        new(client, settings, outputDirectory, pageKey);

    public static void ArchiveReplacedPart(
        string outputDirectory,
        string file)
    {
        try
        {
            string source = Path.Combine(outputDirectory, file);
            if (!File.Exists(source)) return;

            string directory = Path.Combine(
                outputDirectory,
                "rolling-guard-stale-parts");
            Directory.CreateDirectory(directory);

            string target = Path.Combine(
                directory,
                $"{DateTime.Now:yyyyMMdd-HHmmssfff}_{Path.GetFileName(file)}");
            File.Move(source, target, false);
        }
        catch
        {
        }
    }

    private static List<string> Compare(
        Evidence before,
        Evidence current,
        ShareXModV04Settings settings)
    {
        List<string> reasons = new();

        if (!string.Equals(
                before.Fingerprint,
                current.Fingerprint,
                StringComparison.Ordinal) &&
            before.SemanticCount > 0 &&
            current.SemanticCount > 0)
        {
            reasons.Add("semantic-fingerprint-changed");
        }

        if (Math.Abs(current.SemanticCount - before.SemanticCount) >=
            Math.Clamp(settings.DynamicFeedGuardSemanticCountDelta, 1, 1000))
        {
            reasons.Add("semantic-count-changed");
        }

        if (Math.Abs(current.ImageCount - before.ImageCount) >=
            Math.Clamp(settings.DynamicFeedGuardImageCountDelta, 1, 1000))
        {
            reasons.Add("image-count-changed");
        }

        if (current.PendingImages > 0)
        {
            reasons.Add("pending-images");
        }

        if (current.FontStatus.Equals("loading", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("fonts-loading");
        }

        return reasons;
    }

    private static async Task<Evidence> CaptureEvidenceAsync(
        ShareXModChromeCdpClient client,
        double startY,
        double endY)
    {
        string start = startY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string end = endY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        string script = $$"""
(() => {
  const startY = {{start}};
  const endY = Math.max(startY + 1, {{end}});
  const root = document.scrollingElement || document.documentElement;
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const selector = 'article,section,h1,h2,h3,h4,p,li,figure,figcaption,img,video,table,blockquote,pre,details,summary,[role="article"],[data-post-number]';
  const nodes = [...document.querySelectorAll(selector)].filter(el => {
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

  for (const el of nodes.slice(0,1200)) {
    const r = el.getBoundingClientRect();
    const y = r.top + scrollY;
    const h = r.height;
    const text = clean(el.innerText || el.textContent || '').slice(0,180);
    const src = el.currentSrc || el.src || '';
    semantic += `${el.tagName}|${el.id || ''}|${el.getAttribute('data-post-number') || ''}|${text}|${src}|${Math.round(y)}|${Math.round(h)}\u001e`;
    if (el instanceof HTMLImageElement) {
      imageCount++;
      if (!el.complete || (el.currentSrc && el.naturalWidth <= 0)) pendingImages++;
    }
  }

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
    fingerprint: hash.toString(16).padStart(8,'0'),
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

            return new Evidence(
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
            return new Evidence(string.Empty, 0, 0, 0, "unknown", 0, DateTimeOffset.Now);
        }
    }
}
