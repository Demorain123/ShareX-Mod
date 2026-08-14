#nullable enable

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModChromeOverlayDeduplicator
{
    public static async Task PreparePageAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings)
    {
        if (!settings.ChromeOverlayDeduplicateEnabled)
        {
            return;
        }

        int minWidth = Math.Clamp(settings.ChromeOverlayMinimumWidthCss, 8, 2000);
        int minHeight = Math.Clamp(settings.ChromeOverlayMinimumHeightCss, 8, 1000);
        int maxAreaPercent = Math.Clamp(settings.ChromeOverlayMaximumViewportAreaPercent, 1, 95);

        string script = $$"""
(() => {
  const key = '__sharexModOverlayDedupV064';
  if (window[key]) {
    for (const item of window[key].items || []) {
      try {
        item.el.style.setProperty('visibility', item.visibility || '', item.visibilityPriority || '');
      } catch {}
    }
    delete window[key];
  }

  const viewportArea = Math.max(1, innerWidth * innerHeight);
  const items = [];

  for (const el of document.querySelectorAll('body *')) {
    if (!(el instanceof HTMLElement)) continue;
    const cs = getComputedStyle(el);
    if (cs.position !== 'fixed' && cs.position !== 'sticky') continue;

    const r = el.getBoundingClientRect();
    if (r.width < {{minWidth}} || r.height < {{minHeight}}) continue;
    if (r.bottom <= 0 || r.right <= 0 || r.top >= innerHeight || r.left >= innerWidth) continue;

    const areaPercent = (r.width * r.height * 100) / viewportArea;
    if (areaPercent > {{maxAreaPercent}}) continue;

    // Avoid hiding the page's actual root/main content even if a site gives it an odd sticky rule.
    if (el.matches('html,body,main,[role="main"]')) continue;

    items.push({
      el,
      position: cs.position,
      visibility: el.style.getPropertyValue('visibility'),
      visibilityPriority: el.style.getPropertyPriority('visibility'),
      rect: { x: r.left, y: r.top, width: r.width, height: r.height }
    });
  }

  window[key] = {
    pageUrl: location.href,
    hidden: false,
    items
  };

  return {
    count: items.length,
    fixed: items.filter(x => x.position === 'fixed').length,
    sticky: items.filter(x => x.position === 'sticky').length
  };
})()
""";

        try
        {
            using JsonDocument _ = await client.EvaluateAsync(script, false);
        }
        catch
        {
        }
    }

    public static async Task SetHiddenAsync(
        ShareXModChromeCdpClient client,
        bool hidden)
    {
        string flag = hidden ? "true" : "false";
        string script = $$"""
(() => {
  const state = window.__sharexModOverlayDedupV064;
  if (!state) return { available: false, count: 0 };

  for (const item of state.items || []) {
    try {
      if ({{flag}}) {
        item.el.style.setProperty('visibility', 'hidden', 'important');
      } else {
        if (item.visibility) {
          item.el.style.setProperty('visibility', item.visibility, item.visibilityPriority || '');
        } else {
          item.el.style.removeProperty('visibility');
        }
      }
    } catch {}
  }

  state.hidden = {{flag}};
  return { available: true, count: state.items?.length || 0, hidden: state.hidden };
})()
""";

        try
        {
            using JsonDocument _ = await client.EvaluateAsync(script, false);
        }
        catch
        {
        }
    }

    public static async Task RestoreAsync(ShareXModChromeCdpClient client)
    {
        const string script = """
(() => {
  const key = '__sharexModOverlayDedupV064';
  const state = window[key];
  if (!state) return { restored: false };

  for (const item of state.items || []) {
    try {
      if (item.visibility) {
        item.el.style.setProperty('visibility', item.visibility, item.visibilityPriority || '');
      } else {
        item.el.style.removeProperty('visibility');
      }
    } catch {}
  }

  delete window[key];
  return { restored: true };
})()
""";

        try
        {
            using JsonDocument _ = await client.EvaluateAsync(script, false);
        }
        catch
        {
        }
    }
}
