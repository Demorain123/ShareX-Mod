#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModCaptureRecipeRecorder : IAsyncDisposable
{
    private const string BindingName = "__sharexModRecipeEmitV06";

    private readonly ShareXModChromeCdpClient client;
    private readonly ShareXModV04Settings settings;
    private readonly string directory;
    private readonly object sync = new();
    private readonly List<ShareXModRecipeRawEvent> events = new();
    private readonly CancellationTokenSource pollCts = new();
    private Task? pollTask;
    private string? newDocumentScriptId;
    private long sequence;
    private bool stopped;

    private ShareXModCaptureRecipeRecorder(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string directory)
    {
        this.client = client;
        this.settings = settings;
        this.directory = directory;
    }

    public static async Task<ShareXModCaptureRecipeRecorder?> StartAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipeRecordingEnabled) return null;

        try
        {
            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("capture-recipe", directory);

            ShareXModCaptureRecipeRecorder recorder = new(client, settings, directory);
            recorder.client.CdpEventReceived += recorder.OnCdpEvent;
            await recorder.InstallAsync();
            recorder.pollTask = recorder.PollLoopAsync(recorder.pollCts.Token);
            return recorder;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> StopAndWriteAsync()
    {
        if (stopped) return ExistingRecipePath();

        stopped = true;
        pollCts.Cancel();
        if (pollTask != null)
        {
            try { await pollTask; } catch { }
        }

        try { await DrainOnceAsync(CancellationToken.None); } catch { }
        client.CdpEventReceived -= OnCdpEvent;

        try
        {
            if (!string.IsNullOrWhiteSpace(newDocumentScriptId))
            {
                using JsonDocument _ = await client.SendCdpCommandAsync(
                    "Page.removeScriptToEvaluateOnNewDocument",
                    new { identifier = newDocumentScriptId },
                    CancellationToken.None);
            }
        }
        catch { }

        try
        {
            using JsonDocument _ = await client.SendCdpCommandAsync(
                "Runtime.removeBinding",
                new { name = BindingName },
                CancellationToken.None);
        }
        catch { }

        List<ShareXModRecipeRawEvent> snapshot;
        lock (sync)
        {
            snapshot = events.OrderBy(x => x.Sequence).ToList();
        }

        try
        {
            JsonSerializerOptions jsonOptions = new()
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            string rawPath = Path.Combine(directory, "recipe-raw-events.json");
            await File.WriteAllTextAsync(
                rawPath,
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Capture Recipe Raw Events",
                    version = "0.10.1-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
                    delivery = "Runtime.bindingCalled with polling fallback",
                    eventCount = snapshot.Count,
                    events = snapshot
                }, jsonOptions),
                new UTF8Encoding(false));

            ShareXModCaptureRecipe recipe =
                ShareXModCaptureRecipeCompiler.Compile(snapshot, settings);

            string recipePath = Path.Combine(directory, "capture-recipe.json");
            await File.WriteAllTextAsync(
                recipePath,
                JsonSerializer.Serialize(recipe, jsonOptions),
                new UTF8Encoding(false));

            try
            {
                await ShareXModCaptureRecipePreflight.ValidateAsync(client, recipe, directory);
            }
            catch { }

            return recipePath;
        }
        catch
        {
            return null;
        }
    }

    private async Task InstallAsync()
    {
        using (JsonDocument _ = await client.SendCdpCommandAsync(
                   "Runtime.addBinding",
                   new { name = BindingName }))
        {
        }

        string source = BuildRecorderScript(
            Math.Clamp(settings.CaptureRecipeMaxRawEvents, 500, 100000),
            Math.Clamp(settings.CaptureRecipeScrollDebounceMs, 80, 1500));

        using (JsonDocument response = await client.SendCdpCommandAsync(
                   "Page.addScriptToEvaluateOnNewDocument",
                   new { source }))
        {
            JsonElement result = response.RootElement.GetProperty("result");
            if (result.TryGetProperty("identifier", out JsonElement identifier))
            {
                newDocumentScriptId = identifier.GetString();
            }
        }

        using JsonDocument current = await client.EvaluateAsync(source, false);
    }

    private void OnCdpEvent(string method, JsonElement root)
    {
        if (!method.Equals("Runtime.bindingCalled", StringComparison.Ordinal)) return;

        try
        {
            JsonElement parameters = root.GetProperty("params");
            if (!parameters.TryGetProperty("name", out JsonElement nameElement) ||
                !string.Equals(nameElement.GetString(), BindingName, StringComparison.Ordinal) ||
                !parameters.TryGetProperty("payload", out JsonElement payloadElement))
            {
                return;
            }

            string? payload = payloadElement.GetString();
            if (string.IsNullOrWhiteSpace(payload)) return;

            using JsonDocument json = JsonDocument.Parse(payload);
            ShareXModRecipeRawEvent? parsed = ParseRaw(json.RootElement);
            if (parsed != null) AddEvent(parsed);
        }
        catch { }
    }

    private void AddEvent(ShareXModRecipeRawEvent item)
    {
        ShareXModRecipeRawEvent sequenced = item with
        {
            Sequence = Interlocked.Increment(ref sequence)
        };

        lock (sync)
        {
            int limit = Math.Clamp(settings.CaptureRecipeMaxRawEvents, 500, 100000);
            if (events.Count < limit) events.Add(sequenced);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        int interval = Math.Clamp(settings.CaptureRecipePollIntervalMs, 150, 2000);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await DrainOnceAsync(cancellationToken); } catch { }
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DrainOnceAsync(CancellationToken cancellationToken)
    {
        const string expression = """
(() => {
  const state = window.__sharexModRecipeRecorderV06;
  if (!state) return [];
  return state.events.splice(0, state.events.length);
})()
""";

        using JsonDocument response = await client.EvaluateAsync(expression, false, cancellationToken);
        JsonElement result = response.RootElement.GetProperty("result").GetProperty("result");

        if (!result.TryGetProperty("value", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            ShareXModRecipeRawEvent? parsed = ParseRaw(item);
            if (parsed != null) AddEvent(parsed);
        }
    }

    private static ShareXModRecipeRawEvent? ParseRaw(JsonElement item)
    {
        try
        {
            string kind = GetString(item, "kind");
            if (kind.Length == 0 ||
                !item.TryGetProperty("page", out JsonElement pageElement) ||
                pageElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            ShareXModRecipeLocator? target = ParseOptionalLocator(item, "target");
            ShareXModRecipeLocator? viewportTopAnchor = ParseOptionalLocator(item, "viewportTopAnchor");
            ShareXModRecipeLocator? viewportBottomAnchor = ParseOptionalLocator(item, "viewportBottomAnchor");

            return new ShareXModRecipeRawEvent(
                0,
                kind,
                GetDouble(item, "time"),
                ParsePage(pageElement),
                target,
                GetDouble(item, "scrollX"),
                GetDouble(item, "scrollY"),
                GetDouble(item, "scrollWidth"),
                GetDouble(item, "scrollHeight"),
                GetDouble(item, "clientWidth"),
                GetDouble(item, "clientHeight"),
                GetBool(item, "isDocumentScroller"),
                GetBool(item, "trusted"))
            {
                ViewportTopAnchor = viewportTopAnchor,
                ViewportBottomAnchor = viewportBottomAnchor
            };
        }
        catch
        {
            return null;
        }
    }

    private static ShareXModRecipeLocator? ParseOptionalLocator(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? ParseLocator(value)
            : null;
    }

    private static ShareXModRecipePageState ParsePage(JsonElement item)
    {
        return new ShareXModRecipePageState(
            GetString(item, "url"),
            GetString(item, "title"),
            GetDouble(item, "scrollX"),
            GetDouble(item, "scrollY"),
            GetDouble(item, "documentWidth"),
            GetDouble(item, "documentHeight"),
            GetDouble(item, "viewportWidth"),
            GetDouble(item, "viewportHeight"),
            DateTimeOffset.Now);
    }

    private static ShareXModRecipeLocator ParseLocator(JsonElement item)
    {
        string tag = GetString(item, "tag");
        string id = GetString(item, "id");
        string testId = GetString(item, "testId");
        string role = GetString(item, "role");
        string aria = GetString(item, "ariaLabel");
        string name = GetString(item, "name");
        string text = GetString(item, "text");
        string href = GetString(item, "href");
        string rel = GetString(item, "rel");
        string type = GetString(item, "type");

        return new ShareXModRecipeLocator(
            tag, id, testId, role, aria, name, text, href, rel, type,
            GetDouble(item, "documentX"),
            GetDouble(item, "documentY"),
            GetDouble(item, "width"),
            GetDouble(item, "height"),
            ShareXModCaptureRecipeCompiler.LocatorFingerprint(
                tag, id, testId, role, aria, name, text, href));
    }

    private static string BuildRecorderScript(int maxEvents, int scrollDebounceMs)
    {
        return $$"""
(() => {
  const key = '__sharexModRecipeRecorderV06';
  if (window[key]) return { installed: true, reused: true };

  const state = {
    events: [],
    maxEvents: {{maxEvents}},
    fallbackCount: 0,
    scrollTimers: new Map(),
    startedAt: performance.now()
  };

  const cleanText = (value, limit = 180) =>
    String(value || '').replace(/\s+/g, ' ').trim().slice(0, limit);

  const rootElement = () =>
    document.scrollingElement || document.documentElement || document.body || null;

  const pageState = () => {
    const root = rootElement();
    return {
      url: location.href,
      title: document.title || '',
      scrollX: window.scrollX || root?.scrollLeft || 0,
      scrollY: window.scrollY || root?.scrollTop || 0,
      documentWidth: root?.scrollWidth || 0,
      documentHeight: root?.scrollHeight || 0,
      viewportWidth: innerWidth || document.documentElement?.clientWidth || 0,
      viewportHeight: innerHeight || document.documentElement?.clientHeight || 0
    };
  };

  const targetInfo = input => {
    let el = input;
    if (el === document || el === window) el = rootElement();
    if (!(el instanceof Element)) return null;

    const rect = el.getBoundingClientRect();
    return {
      tag: (el.tagName || '').toUpperCase(),
      id: el.id || '',
      testId: el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '',
      role: el.getAttribute('role') || '',
      ariaLabel: el.getAttribute('aria-label') || '',
      name: el.getAttribute('name') || '',
      text: cleanText(el.innerText || el.textContent || ''),
      href: el.href || el.getAttribute('href') || '',
      rel: el.getAttribute('rel') || '',
      type: el.getAttribute('type') || '',
      documentX: rect.left + (window.scrollX || 0),
      documentY: rect.top + (window.scrollY || 0),
      width: rect.width || 0,
      height: rect.height || 0
    };
  };

  // Constant-size viewport sampling: at most three x positions and a short ancestor walk for each
  // edge. This deliberately avoids querySelectorAll/DOMSnapshot during interactive scrolling.
  const semanticAnchorNear = viewportY => {
    const width = Math.max(1, innerWidth || document.documentElement?.clientWidth || 1);
    const height = Math.max(1, innerHeight || document.documentElement?.clientHeight || 1);
    const y = Math.max(1, Math.min(height - 2, viewportY));
    const xs = [width * 0.20, width * 0.50, width * 0.80];
    const semanticTags = new Set([
      'ARTICLE','SECTION','H1','H2','H3','H4','H5','H6','P','LI','FIGURE','FIGCAPTION',
      'IMG','TABLE','BLOCKQUOTE','PRE','DETAILS','SUMMARY','A','BUTTON'
    ]);

    let best = null;
    let bestScore = -1e9;

    const consider = start => {
      let el = start;
      for (let depth = 0; depth < 4 && el instanceof Element; depth++, el = el.parentElement) {
        const rect = el.getBoundingClientRect();
        if (rect.width < 24 || rect.height < 12) continue;
        const style = getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden' ||
            style.position === 'fixed' || style.position === 'sticky') continue;

        const info = targetInfo(el);
        if (!info) continue;
        const meaningful = semanticTags.has(info.tag) || info.id || info.testId || info.role ||
                           info.ariaLabel || info.text;
        if (!meaningful) continue;

        let score = 0;
        if (info.id) score += 45;
        if (info.testId) score += 50;
        if (info.role) score += 22;
        if (info.ariaLabel) score += 28;
        if (semanticTags.has(info.tag)) score += 18;
        if (info.text) score += Math.min(24, 6 + info.text.length / 20);
        score -= Math.abs((rect.top + rect.height * 0.5) - y) / 40;
        score -= depth * 2;

        if (score > bestScore) {
          bestScore = score;
          best = el;
        }
      }
    };

    for (const x of xs) {
      const stack = document.elementsFromPoint(x, y).slice(0, 8);
      for (const el of stack) consider(el);
    }

    return best ? targetInfo(best) : null;
  };

  const viewportAnchors = () => {
    const h = Math.max(1, innerHeight || document.documentElement?.clientHeight || 1);
    return {
      viewportTopAnchor: semanticAnchorNear(Math.max(12, h * 0.12)),
      viewportBottomAnchor: semanticAnchorNear(Math.max(12, h * 0.88))
    };
  };

  const push = event => {
    const anchors = viewportAnchors();
    const full = {
      ...event,
      ...anchors,
      time: performance.now(),
      page: pageState()
    };

    let delivered = false;
    try {
      if (typeof window.{{BindingName}} === 'function') {
        window.{{BindingName}}(JSON.stringify(full));
        delivered = true;
      }
    } catch {}

    if (!delivered && state.events.length < state.maxEvents) {
      state.events.push(full);
      state.fallbackCount++;
    }
  };

  window[key] = state;
  push({ kind: 'page', trusted: true, isDocumentScroller: true });
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () =>
      push({ kind: 'page', trusted: true, isDocumentScroller: true }), { once: true });
  }

  document.addEventListener('click', event => {
    if (!event.isTrusted) return;
    const target = event.target instanceof Element
      ? (event.target.closest('button,a,summary,details,[role],[aria-expanded],input,label') || event.target)
      : null;
    push({
      kind: 'click',
      trusted: true,
      target: targetInfo(target),
      isDocumentScroller: false
    });
  }, true);

  document.addEventListener('scroll', event => {
    const root = rootElement();
    const rawTarget = event.target === document ? root : event.target;
    const isDocumentScroller = rawTarget === root ||
                               rawTarget === document.documentElement ||
                               rawTarget === document.body;

    if (state.scrollTimers.has(rawTarget)) {
      clearTimeout(state.scrollTimers.get(rawTarget));
    }

    const timer = setTimeout(() => {
      state.scrollTimers.delete(rawTarget);
      const el = rawTarget instanceof Element ? rawTarget : null;
      const currentRoot = rootElement();
      push({
        kind: 'scroll',
        trusted: true,
        target: targetInfo(el),
        isDocumentScroller,
        scrollX: isDocumentScroller ? (window.scrollX || currentRoot?.scrollLeft || 0) : (el?.scrollLeft || 0),
        scrollY: isDocumentScroller ? (window.scrollY || currentRoot?.scrollTop || 0) : (el?.scrollTop || 0),
        scrollWidth: isDocumentScroller ? (currentRoot?.scrollWidth || 0) : (el?.scrollWidth || 0),
        scrollHeight: isDocumentScroller ? (currentRoot?.scrollHeight || 0) : (el?.scrollHeight || 0),
        clientWidth: isDocumentScroller ? (currentRoot?.clientWidth || 0) : (el?.clientWidth || 0),
        clientHeight: isDocumentScroller ? (currentRoot?.clientHeight || 0) : (el?.clientHeight || 0)
      });
    }, {{scrollDebounceMs}});

    state.scrollTimers.set(rawTarget, timer);
  }, true);

  return { installed: true, maxEvents: state.maxEvents, semanticBoundarySampling: true };
})()
""";
    }

    private string? ExistingRecipePath()
    {
        string path = Path.Combine(directory, "capture-recipe.json");
        return File.Exists(path) ? path : null;
    }

    private static string ResolveDirectory()
    {
        string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (!string.IsNullOrWhiteSpace(root)) return Path.Combine(root, "capture-recipe");

        return Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "CaptureRecipes",
            $"recipe-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
    }

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double GetDouble(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed)
            ? parsed
            : 0;

    private static bool GetBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public async ValueTask DisposeAsync()
    {
        if (!stopped) await StopAndWriteAsync();
        client.CdpEventReceived -= OnCdpEvent;
        pollCts.Dispose();
    }
}
