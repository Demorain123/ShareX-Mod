#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModChromeLayoutShiftTracker
{
    public static async Task StartAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings)
    {
        if (!settings.ChromeLayoutShiftTrackingEnabled)
        {
            return;
        }

        int limit = Math.Clamp(settings.ChromeLayoutShiftMaxEntries, 32, 4096);

        string script = $$"""
(() => {
  const key = '__sharexModLayoutShiftTrackerV05';
  if (window[key]) return { started: true, reused: true };

  const state = {
    entries: [],
    maxEntries: {{limit}},
    observer: null
  };

  const push = (entry) => {
    if (state.entries.length >= state.maxEntries) return;

    const scrollY = window.scrollY || 0;
    const scrollX = window.scrollX || 0;
    const sources = [];

    for (const source of entry.sources || []) {
      const node = source.node;
      const describe = node ? {
        tag: node.tagName || '',
        id: node.id || '',
        className: typeof node.className === 'string' ? node.className.slice(0, 180) : '',
        ariaLabel: node.getAttribute?.('aria-label') || ''
      } : null;

      const previous = source.previousRect || {};
      const current = source.currentRect || {};

      sources.push({
        node: describe,
        previousRect: {
          x: previous.x || 0,
          y: previous.y || 0,
          width: previous.width || 0,
          height: previous.height || 0,
          documentX: (previous.x || 0) + scrollX,
          documentY: (previous.y || 0) + scrollY
        },
        currentRect: {
          x: current.x || 0,
          y: current.y || 0,
          width: current.width || 0,
          height: current.height || 0,
          documentX: (current.x || 0) + scrollX,
          documentY: (current.y || 0) + scrollY
        }
      });
    }

    state.entries.push({
      value: entry.value || 0,
      hadRecentInput: !!entry.hadRecentInput,
      startTime: entry.startTime || 0,
      scrollX,
      scrollY,
      sources
    });
  };

  try {
    state.observer = new PerformanceObserver(list => {
      for (const entry of list.getEntries()) {
        push(entry);
      }
    });
    state.observer.observe({ type: 'layout-shift', buffered: true });
  } catch (e) {
    state.error = String(e);
  }

  window[key] = state;
  return {
    started: !!state.observer,
    supported: PerformanceObserver.supportedEntryTypes?.includes('layout-shift') ?? false,
    error: state.error || ''
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

    public static async Task<string?> FlushAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings)
    {
        if (!settings.ChromeLayoutShiftTrackingEnabled)
        {
            return null;
        }

        try
        {
            using JsonDocument response = await client.EvaluateAsync(
                """
(() => {
  const key = '__sharexModLayoutShiftTrackerV05';
  const state = window[key];
  if (!state) return { available: false, entries: [] };

  try { state.observer?.takeRecords?.().forEach(entry => {
    // takeRecords is best-effort; normal observer callback usually already recorded the entry.
  }); } catch {}

  return {
    available: true,
    error: state.error || '',
    entryCount: state.entries.length,
    entries: state.entries
  };
})()
""",
                false);

            JsonElement value = response.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value");

            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("layout-shifts", directory);

            string path = Path.Combine(directory, "layout-shifts.json");
            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Layout Shift Evidence",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                evidence = value
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveDirectory()
    {
        string? sessionRoot = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (!string.IsNullOrWhiteSpace(sessionRoot))
        {
            return Path.Combine(sessionRoot, "layout-shifts");
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "LayoutShiftEvidence",
            $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
    }
}
