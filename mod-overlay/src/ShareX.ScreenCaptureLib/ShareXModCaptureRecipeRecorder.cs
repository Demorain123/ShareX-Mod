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
        if (!settings.CaptureRecipeRecordingEnabled)
        {
            return null;
        }

        try
        {
            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("capture-recipe", directory);

            ShareXModCaptureRecipeRecorder recorder = new(client, settings, directory);
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
        if (stopped)
        {
            return ExistingRecipePath();
        }

        stopped = true;
        pollCts.Cancel();

        if (pollTask != null)
        {
            try
            {
                await pollTask;
            }
            catch
            {
            }
        }

        try
        {
            await DrainOnceAsync(CancellationToken.None);
        }
        catch
        {
        }

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
        catch
        {
        }

        List<ShareXModRecipeRawEvent> snapshot;
        lock (sync)
        {
            snapshot = events
                .OrderBy(x => x.Sequence)
                .ToList();
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
                    version = "0.6.0-dev",
                    sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                    created = DateTimeOffset.Now,
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

            return recipePath;
        }
        catch
        {
            return null;
        }
    }

    private async Task InstallAsync()
    {
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

        using JsonDocument _ = await client.EvaluateAsync(source, false);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        int interval = Math.Clamp(settings.CaptureRecipePollIntervalMs, 150, 2000);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(cancellationToken);
            }
            catch
            {
            }

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
  const out = state.events.splice(0, state.events.length);
  return out;
})()
""";

        using JsonDocument response =
            await client.EvaluateAsync(expression, false, cancellationToken);

        JsonElement result = response.RootElement
            .GetProperty("result")
            .GetProperty("result");

        if (!result.TryGetProperty("value", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        List<ShareXModRecipeRawEvent> batch = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            ShareXModRecipeRawEvent? parsed = ParseRaw(item);
            if (parsed != null)
            {
                batch.Add(parsed with { Sequence = Interlocked.Increment(ref sequence) });
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        lock (sync)
        {
            int limit = Math.Clamp(settings.CaptureRecipeMaxRawEvents, 500, 100000);
            foreach (ShareXModRecipeRawEvent item in batch)
            {
                if (events.Count >= limit)
                {
                    break;
                }

                events.Add(item);
            }
        }
    }

    private static ShareXModRecipeRawEvent? ParseRaw(JsonElement item)
    {
        try
        {
            string kind = GetString(item, "kind");
            if (kind.Length == 0)
            {
                return null;
            }

            double timestamp = GetDouble(item, "time");
            bool trusted = GetBool(item, "trusted");
            bool isDocument = GetBool(item, "isDocumentScroller");

            ShareXModRecipePageState page = ParsePage(item.GetProperty("page"));
            ShareXModRecipeLocator? target = null;

            if (item.TryGetProperty("target", out JsonElement targetElement) &&
                targetElement.ValueKind == JsonValueKind.Object)
            {
                target = ParseLocator(targetElement);
            }

            return new ShareXModRecipeRawEvent(
                0,
                kind,
                timestamp,
                page,
                target,
                GetDouble(item, "scrollX"),
                GetDouble(item, "scrollY"),
                GetDouble(item, "scrollWidth"),
                GetDouble(item, "scrollHeight"),
                GetDouble(item, "clientWidth"),
                GetDouble(item, "clientHeight"),
                isDocument,
                trusted);
        }
        catch
        {
            return null;
        }
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
            tag,
            id,
            testId,
            role,
            aria,
            name,
            text,
            href,
            rel,
            type,
            GetDouble(item, "documentX"),
            GetDouble(item, "documentY"),
            GetDouble(item, "width"),
            GetDouble(item, "height"),
            ShareXModCaptureRecipeCompiler.LocatorFingerprint(
                tag,
                id,
                testId,
                role,
                aria,
                name,
                text,
                href));
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
    scrollTimers: new Map(),
    startedAt: performance.now()
  };

  const cleanText = (value, limit = 180) =>
    String(value || '').replace(/\s+/g, ' ').trim().slice(0, limit);

  const pageState = () => {
    const root = document.scrollingElement || document.documentElement;
    return {
      url: location.href,
      title: document.title || '',
      scrollX: window.scrollX || root.scrollLeft || 0,
      scrollY: window.scrollY || root.scrollTop || 0,
      documentWidth: root.scrollWidth || 0,
      documentHeight: root.scrollHeight || 0,
      viewportWidth: innerWidth || document.documentElement.clientWidth || 0,
      viewportHeight: innerHeight || document.documentElement.clientHeight || 0
    };
  };

  const targetInfo = (input) => {
    let el = input;
    if (el === document || el === window) el = document.scrollingElement || document.documentElement;
    if (!(el instanceof Element)) return null;

    const rect = el.getBoundingClientRect();
    const role = el.getAttribute('role') || '';
    const ariaLabel = el.getAttribute('aria-label') || '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '';

    return {
      tag: (el.tagName || '').toUpperCase(),
      id: el.id || '',
      testId,
      role,
      ariaLabel,
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

  const push = (event) => {
    if (state.events.length >= state.maxEvents) return;
    state.events.push({
      ...event,
      time: performance.now(),
      page: pageState()
    });
  };

  push({ kind: 'page', trusted: true, isDocumentScroller: true });

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
    const rawTarget = event.target === document
      ? (document.scrollingElement || document.documentElement)
      : event.target;

    const isDocumentScroller = rawTarget === document.scrollingElement ||
                               rawTarget === document.documentElement ||
                               rawTarget === document.body;

    if (state.scrollTimers.has(rawTarget)) {
      clearTimeout(state.scrollTimers.get(rawTarget));
    }

    const timer = setTimeout(() => {
      state.scrollTimers.delete(rawTarget);
      const el = rawTarget instanceof Element ? rawTarget : null;
      const root = document.scrollingElement || document.documentElement;
      push({
        kind: 'scroll',
        trusted: true,
        target: targetInfo(el),
        isDocumentScroller,
        scrollX: isDocumentScroller ? (window.scrollX || root.scrollLeft || 0) : (el?.scrollLeft || 0),
        scrollY: isDocumentScroller ? (window.scrollY || root.scrollTop || 0) : (el?.scrollTop || 0),
        scrollWidth: isDocumentScroller ? root.scrollWidth : (el?.scrollWidth || 0),
        scrollHeight: isDocumentScroller ? root.scrollHeight : (el?.scrollHeight || 0),
        clientWidth: isDocumentScroller ? root.clientWidth : (el?.clientWidth || 0),
        clientHeight: isDocumentScroller ? root.clientHeight : (el?.clientHeight || 0)
      });
    }, {{scrollDebounceMs}});

    state.scrollTimers.set(rawTarget, timer);
  }, true);

  window[key] = state;
  return { installed: true, maxEvents: state.maxEvents };
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
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Path.Combine(root, "capture-recipe");
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "CaptureRecipes",
            $"recipe-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
    }

    private static string GetString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static double GetDouble(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed)
            ? parsed
            : 0;
    }

    private static bool GetBool(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    public async ValueTask DisposeAsync()
    {
        if (!stopped)
        {
            await StopAndWriteAsync();
        }

        pollCts.Dispose();
    }
}
