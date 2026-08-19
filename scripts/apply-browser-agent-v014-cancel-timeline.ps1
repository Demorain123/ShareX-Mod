[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$bridge = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentBridgeServer.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$diagnostics = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentDiagnosticsExporter.cs"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.4] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.4 compatibility anchor missing: $Marker in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.4] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.4] applied: $Marker" -ForegroundColor Cyan
    }
}

# ---------------------------------------------------------------------------
# Main UI: Browser mode debug remains user-selectable; every Browser status
# message is copied into the timestamped LongCapture log; an F8 cancellation
# before frame 1 is not misreported as a capture failure.
# ---------------------------------------------------------------------------
Replace-One $main @'
            debugCaptureUi.Enabled = false;
            includeInternalDebugWindows.Enabled = false;
'@ @'
            debugCaptureUi.Enabled = !captureBusy; // v0.1.4 Browser debug remains user-selectable
            includeInternalDebugWindows.Enabled = !captureBusy && debugCaptureUi.Checked;
'@ 'v0.1.4 Browser debug remains user-selectable'

Replace-One $main @'
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(browserOptions, message =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() => statusLabel.Text = "Browser Agent: " + message));
'@ @'
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(browserOptions, message =>
            {
                LongCaptureLog.Info($"[BA_TIMELINE] status={LongCaptureLog.OneLine(message)}");
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() => statusLabel.Text = "Browser Agent: " + message));
'@ '[BA_TIMELINE] status='

Replace-One $main @'
        catch (Exception ex)
        {
            trayStopItem.Enabled = false;
'@ @'
        catch (OperationCanceledException)
        {
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            ShowMainWindow();
            statusLabel.Text = "Browser Assisted Capture cancelled by F8.";
            qualityLabel.Text = "Browser Agent: cancelled before a usable frame was saved; no failure is claimed.";
            LongCaptureLog.Info("[BA_TIMELINE] Browser Assisted Capture cancelled by user before a usable result was saved");
        }
        catch (Exception ex)
        {
            trayStopItem.Enabled = false;
'@ 'Browser Assisted Capture cancelled by F8.'

# ---------------------------------------------------------------------------
# Capture session: a user cancellation with zero frames stays a cancellation,
# and selected-region / preload milestones are emitted through the normal
# timestamped status channel.
# ---------------------------------------------------------------------------
Replace-One $session @'
        manifest.CaptureRegionHeightCss = ReadDouble(selectedRegion, "height");
        SaveManifest(manifestPath, manifest);

        if (options.PreloadDynamicContent)
'@ @'
        manifest.CaptureRegionHeightCss = ReadDouble(selectedRegion, "height");
        SaveManifest(manifestPath, manifest);
        status?.Invoke($"Region selected: x={manifest.CaptureRegionLeftCss:F0}, y={manifest.CaptureRegionTopCss:F0}, w={manifest.CaptureRegionWidthCss:F0}, h={manifest.CaptureRegionHeightCss:F0} CSS px");

        if (options.PreloadDynamicContent)
'@ 'Region selected: x='

Replace-One $session @'
            manifest.PreloadPageCounterTotal = ReadInt(preload, "pageCounterTotal");
            SaveManifest(manifestPath, manifest);
        }

        if (options.StartDelayMs > 0)
'@ @'
            manifest.PreloadPageCounterTotal = ReadInt(preload, "pageCounterTotal");
            SaveManifest(manifestPath, manifest);
            status?.Invoke($"Optional pre-scan finished: steps={manifest.PreloadSteps}, duration={manifest.PreloadDurationMs}ms, reachedEnd={manifest.PreloadReachedEnd}, page={manifest.PreloadPageCounterCurrent}/{manifest.PreloadPageCounterTotal}");
        }

        if (options.StartDelayMs > 0)
'@ 'Optional pre-scan finished:'

Replace-One $session @'
        if (manifest.Frames.Count == 0)
        {
            manifest.Status = cancelled ? "cancelled-no-frames" : "failed-no-frames";
            manifest.StopReason = stopReason;
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            throw new InvalidOperationException("Browser Agent capture ended before a usable frame was saved.");
        }
'@ @'
        if (manifest.Frames.Count == 0)
        {
            manifest.Status = cancelled ? "cancelled-no-frames" : "failed-no-frames";
            manifest.StopReason = stopReason;
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            if (cancelled)
            {
                throw new OperationCanceledException("Browser Agent capture was cancelled before the first usable frame was saved.");
            }
            throw new InvalidOperationException("Browser Agent capture ended before a usable frame was saved.");
        }
'@ 'cancelled before the first usable frame'

# ---------------------------------------------------------------------------
# Desktop bridge: request lifecycle telemetry with request id/type/elapsed time,
# plus extension-side agent.event messages. This makes the diagnostics ZIP a
# usable timeline instead of only a final exception snapshot.
# ---------------------------------------------------------------------------
Replace-One $bridge 'using System.Collections.Concurrent;' @'
using System.Collections.Concurrent;
using System.Diagnostics;
'@ 'using System.Diagnostics;'

Replace-One $bridge @'
        long id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
'@ @'
        long id = Interlocked.Increment(ref nextRequestId);
        long requestStarted = Stopwatch.GetTimestamp();
        LongCaptureLog.Info($"[BA_REQ] id={id} type={LongCaptureLog.OneLine(type)} phase=create");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
'@ '[BA_REQ] id={id}'

Replace-One $bridge @'
                await BrowserAgentFrameCodec.WriteAsync(
                    pipe,
                    request,
                    BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
                    cancellationToken).ConfigureAwait(false);
'@ @'
                await BrowserAgentFrameCodec.WriteAsync(
                    pipe,
                    request,
                    BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
                    cancellationToken).ConfigureAwait(false);
                LongCaptureLog.Info($"[BA_REQ] id={id} type={LongCaptureLog.OneLine(type)} phase=sent elapsedMs={Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds:F1}");
'@ 'phase=sent elapsedMs='

Replace-One $bridge @'
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pending.TryRemove(id, out _);
            cancelledRequests.TryAdd(id, 0);
            throw;
        }
        catch
        {
            pending.TryRemove(id, out _);
            throw;
        }
'@ @'
            JsonElement response = await completion.Task.ConfigureAwait(false);
            LongCaptureLog.Info($"[BA_REQ] id={id} type={LongCaptureLog.OneLine(type)} phase=complete elapsedMs={Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds:F1}");
            return response;
        }
        catch (OperationCanceledException)
        {
            pending.TryRemove(id, out _);
            cancelledRequests.TryAdd(id, 0);
            LongCaptureLog.Info($"[BA_REQ] id={id} type={LongCaptureLog.OneLine(type)} phase=cancelled elapsedMs={Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds:F1}");
            throw;
        }
        catch (Exception ex)
        {
            pending.TryRemove(id, out _);
            LongCaptureLog.Warn($"[BA_REQ] id={id} type={LongCaptureLog.OneLine(type)} phase=error elapsedMs={Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds:F1} error={LongCaptureLog.OneLine(ex.Message)}");
            throw;
        }
'@ 'phase=complete elapsedMs='

Replace-One $bridge @'
            if (root.TryGetProperty("type", out JsonElement typeElement) &&
                string.Equals(typeElement.GetString(), "agent.attached", StringComparison.Ordinal))
            {
'@ @'
            if (root.TryGetProperty("type", out JsonElement eventTypeElement) &&
                string.Equals(eventTypeElement.GetString(), "agent.event", StringComparison.Ordinal))
            {
                string eventJson = root.TryGetProperty("result", out JsonElement eventResult)
                    ? JsonSerializer.Serialize(eventResult)
                    : "{}";
                LongCaptureLog.Info($"[BA_AGENT] {LongCaptureLog.OneLine(eventJson)}");
                continue;
            }

            if (root.TryGetProperty("type", out JsonElement typeElement) &&
                string.Equals(typeElement.GetString(), "agent.attached", StringComparison.Ordinal))
            {
'@ '[BA_AGENT]'

# ---------------------------------------------------------------------------
# Extension: real out-of-band cancel, a DOM cancellation marker that can abort
# an in-flight executeScript stability Promise, and an opt-in gentle pre-scan
# instead of v0.1.3's immediate jump-to-bottom loop.
# ---------------------------------------------------------------------------
Replace-One $worker 'let captureRegion = null;' @'
let captureRegion = null;
let captureCancelled = false;
'@ 'let captureCancelled = false;'

Replace-One $worker @'
      case "preload":
        result = await preloadDynamicContent(payload);
        break;
      case "begin":
'@ @'
      case "preload":
        result = await preloadDynamicContent(payload);
        break;
      case "cancel":
        result = await cancelActiveOperation(payload);
        break;
      case "begin":
'@ 'case "cancel":'

Replace-One $worker @'
async function selectCaptureRegion(payload) {
  const state = await collectState();
'@ @'
async function selectCaptureRegion(payload) {
  captureCancelled = false;
  try { await executeInTarget(clearLongCaptureCancellation); } catch (_) { }
  postAgentEvent("region-selection-start", { fullViewport: payload.fullViewport === true });
  const state = await collectState();
'@ 'region-selection-start'

Replace-One $worker @'
  captureRegion.height = Math.min(captureRegion.height, state.viewportHeight - captureRegion.top);
  return { cancelled: false, captureRegion, viewportWidth: state.viewportWidth, viewportHeight: state.viewportHeight };
}

async function preloadDynamicContent(payload) {
'@ @'
  captureRegion.height = Math.min(captureRegion.height, state.viewportHeight - captureRegion.top);
  postAgentEvent("region-selected", captureRegion);
  return { cancelled: false, captureRegion, viewportWidth: state.viewportWidth, viewportHeight: state.viewportHeight };
}

function postAgentEvent(event, detail = {}) {
  try {
    nativePort?.postMessage({
      type: "agent.event",
      result: { event, utc: new Date().toISOString(), ...detail }
    });
  } catch (_) { }
}

function throwIfCaptureCancelled() {
  if (captureCancelled) throw new Error("Browser Agent operation cancelled");
}

async function cancelActiveOperation(payload) {
  captureCancelled = true;
  postAgentEvent("cancel-requested", { reason: String(payload?.reason || "unknown") });
  try { await executeInTarget(signalLongCaptureCancellation); } catch (_) { }
  return { cancelled: true, reason: String(payload?.reason || "unknown") };
}

function signalLongCaptureCancellation() {
  document.documentElement?.setAttribute("data-longcapture-browser-agent-cancelled", "1");
  try { window.dispatchEvent(new CustomEvent("longcapture-browser-agent-cancel")); } catch (_) { }
  return { cancelled: true };
}

function clearLongCaptureCancellation() {
  document.documentElement?.removeAttribute("data-longcapture-browser-agent-cancelled");
  return { cancelled: false };
}

async function preloadDynamicContent(payload) {
'@ 'function postAgentEvent(event'

# Replace the v0.1.3 jump-to-bottom preload body with a paced, cancellable walk.
$workerText = [IO.File]::ReadAllText($worker)
$preloadPattern = '(?s)async function preloadDynamicContent\(payload\) \{.*?\r?\n\}\r?\n\r?\nasync function beginCapture\(payload\) \{'
$preloadReplacement = @'
async function preloadDynamicContent(payload) {
  const options = stabilityOptions(payload);
  const maxDurationMs = clampNumber(payload.maxDurationMs, 10000, 180000, 90000);
  const started = Date.now();
  let initial = await collectState();
  let latest = initial;
  let greatestHeight = initial.scrollHeight;
  let steps = 0;
  let reachedEnd = false;

  postAgentEvent("preload-start", {
    scrollY: initial.scrollY,
    scrollHeight: initial.scrollHeight,
    pageCurrent: initial.pageCounterCurrent || 0,
    pageTotal: initial.pageCounterTotal || 0
  });

  throwIfCaptureCancelled();
  await executeInTarget(scrollDocumentToAbsolute, [0]);
  await waitForStabilityOnTarget({
    stableWindowMs: Math.max(550, Math.min(options.stableWindowMs, 900)),
    maxWaitMs: Math.min(Math.max(options.maxWaitMs, 3500), 5500),
    sampleMs: options.sampleMs
  });

  while (Date.now() - started < maxDurationMs && steps < 240) {
    throwIfCaptureCancelled();
    latest = await collectState();
    const maxY = Math.max(0, latest.scrollHeight - latest.viewportHeight);
    const regionHeight = Math.max(80, Number(captureRegion?.height || latest.viewportHeight));
    const stepCss = Math.max(160, Math.min(latest.viewportHeight * 0.90, regionHeight * 0.95));
    const nextY = Math.min(maxY, latest.scrollY + stepCss);

    if (nextY <= latest.scrollY + 1) {
      const end = await confirmDocumentEnd(options);
      throwIfCaptureCancelled();
      const afterEnd = end.state || await collectState();
      greatestHeight = Math.max(greatestHeight, afterEnd.scrollHeight);
      const counterComplete = afterEnd.pageCounterTotal > 0 && afterEnd.pageCounterCurrent >= afterEnd.pageCounterTotal;
      reachedEnd = end.confirmed === true && (counterComplete || afterEnd.pageCounterTotal <= 0);
      latest = afterEnd;
      break;
    }

    await executeInTarget(scrollDocumentToAbsolute, [nextY]);
    await waitForStabilityOnTarget({
      stableWindowMs: Math.max(500, Math.min(options.stableWindowMs, 800)),
      maxWaitMs: Math.min(Math.max(options.maxWaitMs, 3000), 5000),
      sampleMs: options.sampleMs
    });
    throwIfCaptureCancelled();

    latest = await collectState();
    steps++;
    greatestHeight = Math.max(greatestHeight, latest.scrollHeight);
    postAgentEvent("preload-step", {
      step: steps,
      scrollY: Math.round(latest.scrollY),
      scrollHeight: Math.round(latest.scrollHeight),
      pageCurrent: latest.pageCounterCurrent || 0,
      pageTotal: latest.pageCounterTotal || 0
    });

    if (latest.scrollY + latest.viewportHeight >= latest.scrollHeight - 2) {
      const end = await confirmDocumentEnd(options);
      throwIfCaptureCancelled();
      const afterEnd = end.state || await collectState();
      greatestHeight = Math.max(greatestHeight, afterEnd.scrollHeight);
      const counterComplete = afterEnd.pageCounterTotal > 0 && afterEnd.pageCounterCurrent >= afterEnd.pageCounterTotal;
      if (end.confirmed === true && (counterComplete || afterEnd.pageCounterTotal <= 0)) {
        reachedEnd = true;
        latest = afterEnd;
        break;
      }
    }
  }

  if (!captureCancelled) {
    await executeInTarget(scrollDocumentToAbsolute, [0]);
    await waitForStabilityOnTarget(options);
  }

  postAgentEvent("preload-end", {
    cancelled: captureCancelled,
    reachedEnd,
    steps,
    durationMs: Date.now() - started,
    finalScrollHeight: Math.round(greatestHeight),
    pageCurrent: latest.pageCounterCurrent || 0,
    pageTotal: latest.pageCounterTotal || 0
  });

  return {
    cancelled: captureCancelled,
    reachedEnd,
    steps,
    durationMs: Date.now() - started,
    growthCss: Math.max(0, greatestHeight - initial.scrollHeight),
    pageCounterCurrent: latest.pageCounterCurrent || 0,
    pageCounterTotal: latest.pageCounterTotal || 0,
    finalScrollHeight: greatestHeight
  };
}

async function beginCapture(payload) {
'@
if (-not $workerText.Contains('preloadDynamicContent(payload)')) { throw "v0.1.4 preload function missing" }
if (-not $workerText.Contains('await executeInTarget(scrollDocumentToAbsolute, [maxY]);')) {
    if (-not $workerText.Contains('postAgentEvent("preload-step"')) { throw "v0.1.4 preload anchor missing" }
} elseif (-not $CheckOnly) {
    $updated = [regex]::Replace($workerText, $preloadPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $preloadReplacement }, 1)
    if ($updated -eq $workerText) { throw "v0.1.4 preload regex replacement did not change worker" }
    [IO.File]::WriteAllText($worker, $updated, [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.4] applied: gentle cancellable preload" -ForegroundColor Cyan
}

Replace-One $worker @'
async function captureAndScroll(payload) {
  const options = stabilityOptions(payload);
'@ @'
async function captureAndScroll(payload) {
  throwIfCaptureCancelled();
  const options = stabilityOptions(payload);
'@ 'captureAndScroll(payload) {`r`n  throwIfCaptureCancelled();'

Replace-One $worker @'
  const stabilityBefore = await waitForStabilityOnTarget(options);
  const captured = await captureCurrentViewport(payload, stabilityBefore, hideFixed);
'@ @'
  const stabilityBefore = await waitForStabilityOnTarget(options);
  throwIfCaptureCancelled();
  const captured = await captureCurrentViewport(payload, stabilityBefore, hideFixed);
'@ 'stabilityBefore);`r`n  throwIfCaptureCancelled();'

Replace-One $worker @'
    while (true) {
      await new Promise(resolve => setTimeout(resolve, sampleMs));
'@ @'
    while (true) {
      if (document.documentElement?.getAttribute("data-longcapture-browser-agent-cancelled") === "1") {
        throw new Error("LongCapture operation cancelled");
      }
      await new Promise(resolve => setTimeout(resolve, sampleMs));
'@ 'data-longcapture-browser-agent-cancelled") === "1"'

Replace-One $worker @'
async function scrollDocumentToAbsolute(scrollY) {
  const restore = (element, name, value, priority) => {
'@ @'
async function scrollDocumentToAbsolute(scrollY) {
  if (document.documentElement?.getAttribute("data-longcapture-browser-agent-cancelled") === "1") {
    throw new Error("LongCapture operation cancelled");
  }
  const restore = (element, name, value, priority) => {
'@ 'async function scrollDocumentToAbsolute(scrollY) {`r`n  if (document.documentElement'

Replace-One $worker @'
async function scrollDocumentBy(delta) {
  const restore = (element, name, value, priority) => {
'@ @'
async function scrollDocumentBy(delta) {
  if (document.documentElement?.getAttribute("data-longcapture-browser-agent-cancelled") === "1") {
    throw new Error("LongCapture operation cancelled");
  }
  const restore = (element, name, value, priority) => {
'@ 'async function scrollDocumentBy(delta) {`r`n  if (document.documentElement'

Replace-One $worker @'
    const cleanup = () => {
      window.removeEventListener("keydown", onKey, true);
      try { overlay.remove(); } catch (_) { }
    };
    const finish = (value) => {
      cleanup();
      resolve(value);
    };
    const onKey = event => {
'@ @'
    const cleanup = () => {
      window.removeEventListener("keydown", onKey, true);
      window.removeEventListener("longcapture-browser-agent-cancel", onExternalCancel, true);
      try { overlay.remove(); } catch (_) { }
    };
    const finish = (value) => {
      cleanup();
      resolve(value);
    };
    const onExternalCancel = () => finish({ cancelled: true });
    window.addEventListener("longcapture-browser-agent-cancel", onExternalCancel, true);
    const onKey = event => {
'@ 'longcapture-browser-agent-cancel", onExternalCancel'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($worker)
    $text = $text.Replace('protocolVersion: "0.1.3"', 'protocolVersion: "0.1.4"')
    $text = $text.Replace('LongCapture Browser Agent v0.1.3 attached to this tab', 'LongCapture Browser Agent v0.1.4 attached to this tab')
    [IO.File]::WriteAllText($worker, $text, [Text.UTF8Encoding]::new($true))
}

Replace-One $diagnostics '            writer.WriteLine("LongCapture Browser Agent v0.1.2 diagnostics");' '            writer.WriteLine("LongCapture Browser Agent v0.1.4 diagnostics — request/timeline logging enabled");' 'v0.1.4 diagnostics — request/timeline logging enabled'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.4 cancel/timeline compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.4 hard-stop, gentle-preload and timeline overlay applied." -ForegroundColor Green
}
