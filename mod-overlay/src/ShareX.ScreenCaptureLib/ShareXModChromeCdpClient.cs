#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModChromeTarget(string Id, string Title, string Url, string WebSocketDebuggerUrl);

internal sealed class ShareXModChromeCdpClient : IAsyncDisposable
{
    private readonly HttpClient http = new();
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private int commandId;

    public ShareXModChromeTarget? Target { get; private set; }

    // CDP events can arrive between a command and its response. The original client safely
    // ignored them; Capture Recipe needs a small event tap so Runtime.bindingCalled is not
    // lost when a demonstrated click immediately navigates away from the old document.
    public event Action<string, JsonElement>? CdpEventReceived;

    public async Task<IReadOnlyList<ShareXModChromeTarget>> ListTargetsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await http.GetAsync(endpoint.TrimEnd('/') + "/json/list", cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        List<ShareXModChromeTarget> targets = new();

        foreach (JsonElement item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement type) || type.GetString() != "page") continue;
            if (!item.TryGetProperty("webSocketDebuggerUrl", out JsonElement ws)) continue;
            targets.Add(new ShareXModChromeTarget(
                item.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("title", out JsonElement title) ? title.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("url", out JsonElement pageUrl) ? pageUrl.GetString() ?? string.Empty : string.Empty,
                ws.GetString() ?? string.Empty));
        }

        return targets;
    }

    public async Task ConnectAsync(ShareXModChromeTarget target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl)) throw new InvalidOperationException("Chrome target has no DevTools WebSocket URL.");
        await socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), cancellationToken);
        Target = target;
        using JsonDocument _1 = await SendCdpCommandAsync("Runtime.enable", null, cancellationToken);
        using JsonDocument _2 = await SendCdpCommandAsync("Page.enable", null, cancellationToken);
    }

    public Task<JsonDocument> EvaluateAsync(string expression, bool awaitPromise = false, CancellationToken cancellationToken = default)
    {
        return SendCdpCommandAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise,
            silent = false
        }, cancellationToken);
    }

    public async Task<byte[]> CaptureScreenshotAsync(bool captureBeyondViewport = false, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendCdpCommandAsync("Page.captureScreenshot", new
        {
            format = "png",
            fromSurface = true,
            captureBeyondViewport,
            optimizeForSpeed = true
        }, cancellationToken);

        string? data = response.RootElement.GetProperty("result").GetProperty("data").GetString();
        return string.IsNullOrEmpty(data) ? Array.Empty<byte>() : Convert.FromBase64String(data);
    }

    public async Task<JsonDocument> PreparePageAsync(ShareXModV04Settings settings, CancellationToken cancellationToken = default)
    {
        string script = $$"""
(() => {
  const stateKey = '__sharexModV04State';
  if (window[stateKey]) return window[stateKey].summary;
  const state = { details: [], spoilers: [], scrollers: [] };

  if ({{settings.ChromeExpandDetails.ToString().ToLowerInvariant()}}) {
    for (const el of document.querySelectorAll('details:not([open])')) {
      state.details.push(el);
      el.open = true;
    }
  }

  if ({{settings.ChromeRevealDiscourseSpoilers.ToString().ToLowerInvariant()}}) {
    for (const el of document.querySelectorAll('.spoiled.spoiler-blurred')) {
      state.spoilers.push({ el, className: el.className, aria: el.getAttribute('aria-expanded'), filter: el.style.filter });
      el.classList.remove('spoiler-blurred');
      el.classList.add('revealed');
      el.setAttribute('aria-expanded', 'true');
      el.style.filter = 'none';
    }
  }

  if ({{settings.ChromeExpandNestedScrollContainers.ToString().ToLowerInvariant()}}) {
    for (const el of document.querySelectorAll('body *')) {
      const cs = getComputedStyle(el);
      const overflowY = cs.overflowY;
      const scrollable = (overflowY === 'auto' || overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 64;
      if (!scrollable || el.clientHeight < 48) continue;
      state.scrollers.push({ el, style: el.getAttribute('style'), scrollTop: el.scrollTop });
      el.style.setProperty('max-height', 'none', 'important');
      el.style.setProperty('height', 'auto', 'important');
      el.style.setProperty('overflow-y', 'visible', 'important');
    }
  }

  state.summary = {
    expandedDetails: state.details.length,
    revealedSpoilers: state.spoilers.length,
    expandedScrollContainers: state.scrollers.length,
    scrollHeight: document.scrollingElement?.scrollHeight ?? document.documentElement.scrollHeight
  };
  window[stateKey] = state;
  return state.summary;
})()
""";
        return await EvaluateAsync(script, false, cancellationToken);
    }

    public async Task RestorePageAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
(() => {
  const key = '__sharexModV04State';
  const state = window[key];
  if (!state) return { restored: false };
  for (const el of state.details || []) el.open = false;
  for (const item of state.spoilers || []) {
    item.el.className = item.className;
    if (item.aria == null) item.el.removeAttribute('aria-expanded'); else item.el.setAttribute('aria-expanded', item.aria);
    item.el.style.filter = item.filter;
  }
  for (const item of state.scrollers || []) {
    if (item.style == null) item.el.removeAttribute('style'); else item.el.setAttribute('style', item.style);
    item.el.scrollTop = item.scrollTop;
  }
  delete window[key];
  return { restored: true };
})()
""";
        using JsonDocument _ = await EvaluateAsync(script, false, cancellationToken);
    }

    public async Task ScrollToAsync(double y, CancellationToken cancellationToken = default)
    {
        string ys = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using JsonDocument _ = await EvaluateAsync($"window.scrollTo({{top:{ys},behavior:'instant'}}); ({ys})", false, cancellationToken);
    }

    public async Task<(double y, double height, double viewport)> GetScrollMetricsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await EvaluateAsync("(() => { const s=document.scrollingElement||document.documentElement; return {y:s.scrollTop,height:s.scrollHeight,viewport:s.clientHeight}; })()", false, cancellationToken);
        JsonElement value = response.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
        return (value.GetProperty("y").GetDouble(), value.GetProperty("height").GetDouble(), value.GetProperty("viewport").GetDouble());
    }

    public async Task<JsonDocument> SendCdpCommandAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            int id = Interlocked.Increment(ref commandId);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

            byte[] buffer = new byte[256 * 1024];
            using MemoryStream message = new();
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Chrome DevTools connection closed.");
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;

                JsonDocument json = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);

                if (!json.RootElement.TryGetProperty("id", out JsonElement responseId))
                {
                    DispatchEvent(json.RootElement);
                    json.Dispose();
                    continue;
                }

                if (responseId.GetInt32() != id)
                {
                    // sendLock guarantees one outstanding command from this client, but preserve
                    // fail-safe behavior if a browser/proxy ever returns an unexpected response.
                    json.Dispose();
                    continue;
                }

                if (json.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    string text = error.ToString();
                    json.Dispose();
                    throw new InvalidOperationException($"Chrome DevTools command {method} failed: {text}");
                }
                return json;
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void DispatchEvent(JsonElement root)
    {
        if (!root.TryGetProperty("method", out JsonElement methodElement))
        {
            return;
        }

        string? method = methodElement.GetString();
        if (string.IsNullOrWhiteSpace(method))
        {
            return;
        }

        Action<string, JsonElement>? handler = CdpEventReceived;
        if (handler == null)
        {
            return;
        }

        try
        {
            // Clone detaches the event payload from the JsonDocument that the receive loop owns.
            handler(method, root.Clone());
        }
        catch
        {
            // Event observers are advisory and must never break command/response delivery.
        }
    }

    public async ValueTask DisposeAsync()
    {
        CdpEventReceived = null;
        try
        {
            if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ShareX-Mod finished", CancellationToken.None);
        }
        catch { }
        socket.Dispose();
        http.Dispose();
        sendLock.Dispose();
    }
}
