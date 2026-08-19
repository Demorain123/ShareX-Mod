const HOST_NAME = "com.longcapture.browser_agent";
const MIN_CAPTURE_INTERVAL_MS = 520;
const DEFAULT_STABLE_WINDOW_MS = 900;
const DEFAULT_MAX_STABILITY_WAIT_MS = 7000;
const DEFAULT_END_ROUNDS = 3;
let nativePort = null;
let target = null;

chrome.action.onClicked.addListener((tab) => { void attachTab(tab); });

async function attachTab(tab) {
  if (!tab || typeof tab.id !== "number" || typeof tab.windowId !== "number") {
    return;
  }

  if (nativePort) {
    try { nativePort.disconnect(); } catch (_) { }
    nativePort = null;
  }

  target = {
    tabId: tab.id,
    windowId: tab.windowId,
    title: tab.title || "",
    url: tab.url || ""
  };

  try {
    nativePort = chrome.runtime.connectNative(HOST_NAME);
    nativePort.onMessage.addListener((message) => { void handleDesktopMessage(message); });
    nativePort.onDisconnect.addListener(() => {
      const detail = chrome.runtime.lastError?.message || "Native host disconnected";
      console.warn("LongCapture Browser Agent disconnected:", detail);
      nativePort = null;
      void setBadge("", "LongCapture Browser Agent disconnected");
    });

    nativePort.postMessage({
      type: "agent.attached",
      ok: true,
      result: {
        title: target.title,
        url: target.url,
        tabId: target.tabId,
        windowId: target.windowId,
        protocolVersion: "0.1.2"
      }
    });
    await setBadge("ON", "LongCapture Browser Agent v0.1.2 attached to this tab");
  } catch (error) {
    console.error("LongCapture Browser Agent could not connect:", error);
    nativePort = null;
    await setBadge("ERR", String(error?.message || error));
  }
}

async function handleDesktopMessage(message) {
  if (!nativePort || !message || typeof message.id !== "number" || typeof message.type !== "string") {
    return;
  }

  try {
    await assertTargetIsActive();
    const payload = message.payload || {};
    let result;

    switch (message.type) {
      case "preview":
        result = await previewCaptureRegion(payload);
        break;
      case "begin":
        result = await beginCapture(payload);
        break;
      case "probe":
        result = await collectState();
        break;
      case "captureAndScroll":
        result = await captureAndScroll(payload);
        break;
      case "captureAt":
        result = await captureAt(payload);
        break;
      case "moveTo":
        result = await moveTo(payload);
        break;
      case "restore":
        result = await executeInTarget(restoreHiddenCandidates);
        break;
      default:
        throw new Error(`Unsupported Browser Agent command: ${message.type}`);
    }

    nativePort?.postMessage({ id: message.id, type: "response", ok: true, result });
  } catch (error) {
    try {
      nativePort?.postMessage({
        id: message.id,
        type: "response",
        ok: false,
        error: String(error?.message || error)
      });
    } catch (_) { }
  }
}

function stabilityOptions(payload) {
  return {
    stableWindowMs: clampNumber(payload.stableWindowMs, 400, 3000, DEFAULT_STABLE_WINDOW_MS),
    maxWaitMs: clampNumber(payload.maxWaitMs, 1500, 12000, DEFAULT_MAX_STABILITY_WAIT_MS),
    sampleMs: clampNumber(payload.sampleMs, 80, 300, 120)
  };
}

async function previewCaptureRegion(payload) {
  const durationMs = clampNumber(payload.durationMs, 300, 1600, 650);
  return await executeInTarget(showCapturePreview, [durationMs]);
}

async function beginCapture(payload) {
  const options = stabilityOptions(payload);
  await executeInTarget(scrollDocumentToAbsolute, [0]);
  const stability = await waitForStabilityOnTarget(options);
  const state = await collectState();
  return { ...state, stability };
}

async function captureAndScroll(payload) {
  const options = stabilityOptions(payload);
  const overlapRatio = clampNumber(payload.overlapRatio, 0.10, 0.45, 0.26);
  const hideFixed = payload.hideFixed === true;

  const warmup = await warmLazyBoundary(options);
  const stabilityBefore = await waitForStabilityOnTarget(options);
  const captured = await captureCurrentViewport(payload, stabilityBefore, hideFixed);

  let atBottom = captured.before.scrollY + captured.before.viewportHeight >= captured.before.scrollHeight - 2;
  let stabilityAfter = null;
  let after = captured.afterCapture;
  let endConfirmation = {
    confirmed: false,
    rounds: 0,
    growthCss: 0,
    counterIncomplete: false,
    confidence: "not-at-bottom"
  };

  const delta = Math.max(1, Math.floor(captured.before.viewportHeight * (1 - overlapRatio)));

  if (atBottom) {
    endConfirmation = await confirmDocumentEnd(options);
    atBottom = endConfirmation.confirmed === true;

    if (!atBottom && Number(endConfirmation.growthCss || 0) > 1) {
      const grown = endConfirmation.state || await collectState();
      const maxY = Math.max(0, grown.scrollHeight - grown.viewportHeight);
      const nextY = Math.min(maxY, captured.before.scrollY + delta);
      await executeInTarget(scrollDocumentToAbsolute, [nextY]);
      stabilityAfter = await waitForStabilityOnTarget(options);
      after = await collectState();
    } else {
      after = await collectState();
    }
  } else {
    await executeInTarget(scrollDocumentBy, [delta]);
    stabilityAfter = await waitForStabilityOnTarget(options);
    after = await collectState();
  }

  return {
    ...captured,
    after,
    stabilityAfter,
    warmupTriggered: warmup.triggered,
    warmupGrowthCss: warmup.growthCss,
    atBottom,
    endConfirmation
  };
}

async function captureAt(payload) {
  const options = stabilityOptions(payload);
  const targetScrollY = Math.max(0, Number(payload.scrollY) || 0);
  const hideFixed = payload.hideFixed === true;

  await executeInTarget(scrollDocumentToAbsolute, [targetScrollY]);
  const stabilityBefore = await waitForStabilityOnTarget(options);
  const captured = await captureCurrentViewport(payload, stabilityBefore, hideFixed);
  const atBottom = captured.before.scrollY + captured.before.viewportHeight >= captured.before.scrollHeight - 2;

  return {
    ...captured,
    after: captured.afterCapture,
    stabilityAfter: stabilityBefore,
    warmupTriggered: false,
    warmupGrowthCss: 0,
    atBottom,
    endConfirmation: {
      confirmed: false,
      rounds: 0,
      growthCss: 0,
      counterIncomplete: false,
      confidence: "recapture-not-confirmed"
    }
  };
}

async function moveTo(payload) {
  const options = stabilityOptions(payload);
  const targetScrollY = Math.max(0, Number(payload.scrollY) || 0);
  await executeInTarget(scrollDocumentToAbsolute, [targetScrollY]);
  const stability = await waitForStabilityOnTarget(options);
  const state = await collectState();
  return { state, stability };
}

async function captureCurrentViewport(payload, stabilityBefore, hideFixed) {
  const before = await collectState();
  let hiddenCount = 0;
  let pngDataUrl;

  try {
    if (hideFixed) {
      const hidden = await executeInTarget(hideSafeFixedCandidates);
      hiddenCount = Number(hidden?.hiddenCount || 0);
      await sleep(34);
    }

    await assertTargetIsActive();
    pngDataUrl = await chrome.tabs.captureVisibleTab(target.windowId, { format: "png" });
  } finally {
    if (hideFixed) {
      try { await executeInTarget(restoreHiddenCandidates); } catch (_) { }
    }
  }

  const afterCapture = await collectState();
  const captureStateChanged =
    before.stateHash !== afterCapture.stateHash ||
    Math.abs(before.scrollHeight - afterCapture.scrollHeight) > 1 ||
    Math.abs(before.scrollY - afterCapture.scrollY) > 0.5;

  return {
    before,
    afterCapture,
    stabilityBefore,
    hiddenCount,
    captureStateChanged,
    pngDataUrl
  };
}

async function warmLazyBoundary(options) {
  const initial = await collectState();
  const distanceToBottom = initial.scrollHeight - (initial.scrollY + initial.viewportHeight);
  const triggerDistance = initial.viewportHeight * 2.2;

  if (distanceToBottom > triggerDistance || distanceToBottom <= 2) {
    return { triggered: false, growthCss: 0 };
  }

  const savedY = initial.scrollY;
  let greatestHeight = initial.scrollHeight;

  for (let attempt = 0; attempt < 2; attempt++) {
    const state = await collectState();
    const maxY = Math.max(0, state.scrollHeight - state.viewportHeight);
    const probeY = Math.min(maxY, savedY + state.viewportHeight * 0.90);
    if (probeY <= savedY + 2) break;

    await executeInTarget(scrollDocumentToAbsolute, [probeY]);
    await waitForStabilityOnTarget({
      stableWindowMs: Math.max(options.stableWindowMs, 1050),
      maxWaitMs: Math.max(options.maxWaitMs, 8000),
      sampleMs: options.sampleMs
    });

    const grown = await collectState();
    greatestHeight = Math.max(greatestHeight, grown.scrollHeight);
    if (grown.scrollHeight <= state.scrollHeight + 2) break;
  }

  await executeInTarget(scrollDocumentToAbsolute, [savedY]);
  await waitForStabilityOnTarget(options);
  return {
    triggered: true,
    growthCss: Math.max(0, greatestHeight - initial.scrollHeight)
  };
}

async function confirmDocumentEnd(options) {
  const initial = await collectState();
  const initialHeight = initial.scrollHeight;
  let greatestHeight = initialHeight;
  let latest = initial;
  let rounds = 0;
  let sawCounterIncomplete =
    initial.pageCounterTotal > 0 && initial.pageCounterCurrent > 0 && initial.pageCounterCurrent < initial.pageCounterTotal;

  for (let round = 1; round <= DEFAULT_END_ROUNDS; round++) {
    rounds = round;
    latest = await collectState();
    const maxY = Math.max(0, latest.scrollHeight - latest.viewportHeight);

    // Re-enter the bottom trigger zone on later rounds. This helps sites whose
    // IntersectionObserver / infinite-loader only fires on a fresh edge crossing.
    if (round > 1 && maxY > latest.viewportHeight * 0.20) {
      await executeInTarget(scrollDocumentToAbsolute, [Math.max(0, maxY - latest.viewportHeight * 0.18)]);
      await sleep(140);
    }
    await executeInTarget(scrollDocumentToAbsolute, [maxY]);

    const counterIncomplete =
      latest.pageCounterTotal > 0 && latest.pageCounterCurrent > 0 && latest.pageCounterCurrent < latest.pageCounterTotal;
    sawCounterIncomplete = sawCounterIncomplete || counterIncomplete;
    const holdMs = counterIncomplete ? 1900 : 1150;
    const holdStart = Date.now();

    while (Date.now() - holdStart < holdMs) {
      await sleep(180);
      const probe = await collectState();
      greatestHeight = Math.max(greatestHeight, probe.scrollHeight);
      if (probe.scrollHeight > latest.scrollHeight + 2 || probe.scrollHeight > initialHeight + 2) {
        await waitForStabilityOnTarget({
          stableWindowMs: Math.max(options.stableWindowMs, 1050),
          maxWaitMs: Math.max(options.maxWaitMs, 8000),
          sampleMs: options.sampleMs
        });
        const grown = await collectState();
        return {
          confirmed: false,
          rounds,
          growthCss: Math.max(0, grown.scrollHeight - initialHeight),
          counterIncomplete: sawCounterIncomplete,
          confidence: "more-content-loaded",
          state: grown
        };
      }
      latest = probe;
    }

    const stable = await waitForStabilityOnTarget({
      stableWindowMs: Math.max(options.stableWindowMs, 1050),
      maxWaitMs: Math.max(options.maxWaitMs, 8000),
      sampleMs: options.sampleMs
    });
    latest = await collectState();
    greatestHeight = Math.max(greatestHeight, latest.scrollHeight);

    if (!stable.stable || latest.scrollHeight > initialHeight + 2) {
      if (latest.scrollHeight > initialHeight + 2) {
        return {
          confirmed: false,
          rounds,
          growthCss: Math.max(0, latest.scrollHeight - initialHeight),
          counterIncomplete: sawCounterIncomplete,
          confidence: "more-content-loaded",
          state: latest
        };
      }
    }
  }

  latest = await collectState();
  const finalCounterIncomplete =
    latest.pageCounterTotal > 0 && latest.pageCounterCurrent > 0 && latest.pageCounterCurrent < latest.pageCounterTotal;
  sawCounterIncomplete = sawCounterIncomplete || finalCounterIncomplete;

  return {
    confirmed: true,
    rounds,
    growthCss: Math.max(0, greatestHeight - initialHeight),
    counterIncomplete: sawCounterIncomplete,
    confidence: sawCounterIncomplete
      ? "stable-bottom-dom-counter-incomplete"
      : latest.pageCounterTotal > 0
        ? "stable-bottom-dom-counter-complete"
        : "stable-bottom-geometry",
    state: latest
  };
}

async function waitForStabilityOnTarget(options) {
  const result = await executeInTarget(waitForPageStability, [
    options.stableWindowMs,
    options.maxWaitMs,
    options.sampleMs
  ]);
  if (!result || typeof result.stable !== "boolean") {
    throw new Error("Browser Agent page-stability probe returned no result.");
  }
  return result;
}

async function assertTargetIsActive() {
  if (!target || typeof target.tabId !== "number") {
    throw new Error("No Chromium tab is attached. Click the LongCapture Browser Agent extension icon on the target tab.");
  }

  const tab = await chrome.tabs.get(target.tabId);
  if (!tab.active || tab.windowId !== target.windowId) {
    throw new Error("The attached Chromium tab is no longer the active tab in its window. Reactivate it before continuing.");
  }
}

async function collectState() {
  const state = await executeInTarget(collectDocumentState);
  if (!state || typeof state.scrollY !== "number") {
    throw new Error("Browser Agent could not read document geometry from the active tab.");
  }
  return state;
}

async function executeInTarget(func, args = []) {
  const results = await chrome.scripting.executeScript({
    target: { tabId: target.tabId, allFrames: false },
    func,
    args
  });
  return results?.[0]?.result;
}

function collectDocumentState() {
  const round2Local = (value) => Math.round(Number(value || 0) * 100) / 100;
  const viewportWidth = window.innerWidth;
  const viewportHeight = window.innerHeight;
  const scrollY = window.scrollY || window.pageYOffset || 0;
  const root = document.scrollingElement || document.documentElement;
  const scrollHeight = Math.max(
    root?.scrollHeight || 0,
    document.documentElement?.scrollHeight || 0,
    document.body?.scrollHeight || 0
  );
  const layoutHeight = Math.max(
    document.documentElement?.getBoundingClientRect?.().height || 0,
    document.body?.getBoundingClientRect?.().height || 0
  );

  const fixedCandidates = [];
  const elements = document.body ? document.body.getElementsByTagName("*") : [];
  let elementCount = 0;
  let bestCounter = null;

  for (const element of elements) {
    elementCount++;
    if (fixedCandidates.length >= 64 && bestCounter) continue;

    const style = getComputedStyle(element);
    if (style.position !== "fixed" && style.position !== "sticky") continue;
    if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity || 1) <= 0.02) continue;

    const rect = element.getBoundingClientRect();
    if (!rect || rect.width <= 1 || rect.height <= 1) continue;
    if (rect.bottom <= 0 || rect.right <= 0 || rect.top >= viewportHeight || rect.left >= viewportWidth) continue;

    const touchesTop = Math.abs(rect.top) <= 3;
    const touchesBottom = Math.abs(rect.bottom - viewportHeight) <= 3;
    const touchesLeft = Math.abs(rect.left) <= 3;
    const touchesRight = Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky" && !(touchesTop || touchesBottom || touchesLeft || touchesRight)) continue;

    const viewportArea = Math.max(1, viewportWidth * viewportHeight);
    const visibleWidth = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
    const visibleHeight = Math.max(0, Math.min(viewportHeight, rect.bottom) - Math.max(0, rect.top));
    const areaRatio = (visibleWidth * visibleHeight) / viewportArea;
    if (areaRatio >= 0.65) continue;

    if (fixedCandidates.length < 64) {
      fixedCandidates.push({
        tag: String(element.tagName || "").toLowerCase(),
        position: style.position,
        left: round2Local(rect.left),
        top: round2Local(rect.top),
        width: round2Local(rect.width),
        height: round2Local(rect.height),
        zIndex: style.zIndex || "auto",
        edge: [touchesTop ? "top" : "", touchesBottom ? "bottom" : "", touchesLeft ? "left" : "", touchesRight ? "right" : ""].filter(Boolean).join(",")
      });
    }

    const rawText = String(element.innerText || element.textContent || "").replace(/\s+/g, " ").trim();
    if (rawText.length > 0 && rawText.length <= 48) {
      const match = rawText.match(/(?:^|\D)(\d{1,6})\s*\/\s*(\d{1,6})(?:\D|$)/);
      if (match) {
        const current = Number(match[1]);
        const total = Number(match[2]);
        if (Number.isFinite(current) && Number.isFinite(total) && current >= 0 && total > 0 && current <= total && total <= 100000) {
          const score = (touchesBottom ? 4 : 0) + (touchesRight ? 4 : 0) + (touchesTop ? 1 : 0) + (areaRatio < 0.08 ? 2 : 0);
          if (!bestCounter || score > bestCounter.score || (score === bestCounter.score && total > bestCounter.total)) {
            bestCounter = { current, total, text: `${current} / ${total}`, score };
          }
        }
      }
    }
  }

  return {
    scrollY,
    scrollHeight,
    layoutHeight,
    viewportWidth,
    viewportHeight,
    devicePixelRatio: window.devicePixelRatio || 1,
    fixedCandidateCount: fixedCandidates.length,
    fixedCandidates,
    pageCounterCurrent: bestCounter?.current || 0,
    pageCounterTotal: bestCounter?.total || 0,
    pageCounterText: bestCounter?.text || "",
    stateHash: `${Math.round(scrollY)}:${Math.round(scrollHeight)}:${Math.round(layoutHeight)}:${elementCount}:${document.body?.childElementCount || 0}`
  };
}

async function waitForPageStability(stableWindowMs, maxWaitMs, sampleMs) {
  const start = performance.now();
  let lastChange = start;
  let mutationCount = 0;
  let resizeCount = 0;
  let heightChangeCount = 0;
  let samples = 0;
  let pendingImages = 0;

  const readMetrics = () => {
    const root = document.scrollingElement || document.documentElement;
    const scrollHeight = Math.max(
      root?.scrollHeight || 0,
      document.documentElement?.scrollHeight || 0,
      document.body?.scrollHeight || 0
    );
    const layoutHeight = Math.max(
      document.documentElement?.getBoundingClientRect?.().height || 0,
      document.body?.getBoundingClientRect?.().height || 0
    );

    let pending = 0;
    const viewportHeight = window.innerHeight || 1;
    for (const image of document.images || []) {
      if (image.complete) continue;
      const rect = image.getBoundingClientRect();
      if (rect.bottom >= -viewportHeight && rect.top <= viewportHeight * 2) pending++;
      if (pending >= 64) break;
    }

    return {
      scrollHeight,
      layoutHeight,
      pendingImages: pending,
      fontsLoading: document.fonts?.status === "loading"
    };
  };

  let metrics = readMetrics();
  const initialScrollHeight = metrics.scrollHeight;

  const markChange = () => {
    lastChange = performance.now();
  };

  const mutationObserver = new MutationObserver((records) => {
    mutationCount += records.length;
    markChange();
  });

  mutationObserver.observe(document.documentElement, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["src", "srcset", "sizes", "hidden", "open"]
  });

  let resizeObserver = null;
  try {
    resizeObserver = new ResizeObserver((entries) => {
      resizeCount += entries.length;
      markChange();
    });
    resizeObserver.observe(document.documentElement);
    if (document.body) resizeObserver.observe(document.body);
  } catch (_) {
    resizeObserver = null;
  }

  try {
    while (true) {
      await new Promise(resolve => setTimeout(resolve, sampleMs));
      samples++;

      const current = readMetrics();
      pendingImages = current.pendingImages;
      if (
        Math.abs(current.scrollHeight - metrics.scrollHeight) > 1 ||
        Math.abs(current.layoutHeight - metrics.layoutHeight) > 1
      ) {
        heightChangeCount++;
        markChange();
      }
      if (current.pendingImages !== metrics.pendingImages || current.fontsLoading !== metrics.fontsLoading) {
        markChange();
      }
      metrics = current;

      const now = performance.now();
      const quietFor = now - lastChange;
      const stable = quietFor >= stableWindowMs && current.pendingImages === 0 && !current.fontsLoading;
      if (stable) {
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        const finalMetrics = readMetrics();
        if (
          Math.abs(finalMetrics.scrollHeight - metrics.scrollHeight) <= 1 &&
          Math.abs(finalMetrics.layoutHeight - metrics.layoutHeight) <= 1 &&
          finalMetrics.pendingImages === 0 &&
          !finalMetrics.fontsLoading
        ) {
          return {
            stable: true,
            timedOut: false,
            waitedMs: Math.round(performance.now() - start),
            quietMs: Math.round(performance.now() - lastChange),
            mutationCount,
            resizeCount,
            heightChangeCount,
            samples,
            pendingImages: finalMetrics.pendingImages,
            initialScrollHeight,
            finalScrollHeight: finalMetrics.scrollHeight
          };
        }
        metrics = finalMetrics;
        markChange();
      }

      if (now - start >= maxWaitMs) {
        return {
          stable: false,
          timedOut: true,
          waitedMs: Math.round(now - start),
          quietMs: Math.round(now - lastChange),
          mutationCount,
          resizeCount,
          heightChangeCount,
          samples,
          pendingImages,
          initialScrollHeight,
          finalScrollHeight: current.scrollHeight
        };
      }
    }
  } finally {
    mutationObserver.disconnect();
    resizeObserver?.disconnect();
  }
}

async function scrollDocumentToAbsolute(scrollY) {
  const restore = (element, name, value, priority) => {
    if (!element) return;
    if (value) element.style.setProperty(name, value, priority || "");
    else element.style.removeProperty(name);
  };
  const html = document.documentElement;
  const body = document.body;
  const oldHtmlValue = html?.style.getPropertyValue("scroll-behavior") || "";
  const oldHtmlPriority = html?.style.getPropertyPriority("scroll-behavior") || "";
  const oldBodyValue = body?.style.getPropertyValue("scroll-behavior") || "";
  const oldBodyPriority = body?.style.getPropertyPriority("scroll-behavior") || "";

  try {
    html?.style.setProperty("scroll-behavior", "auto", "important");
    body?.style.setProperty("scroll-behavior", "auto", "important");
    window.scrollTo(0, Math.max(0, Number(scrollY) || 0));
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  } finally {
    restore(html, "scroll-behavior", oldHtmlValue, oldHtmlPriority);
    restore(body, "scroll-behavior", oldBodyValue, oldBodyPriority);
  }
  return { scrollY: window.scrollY || 0 };
}

async function scrollDocumentBy(delta) {
  const restore = (element, name, value, priority) => {
    if (!element) return;
    if (value) element.style.setProperty(name, value, priority || "");
    else element.style.removeProperty(name);
  };
  const html = document.documentElement;
  const body = document.body;
  const oldHtmlValue = html?.style.getPropertyValue("scroll-behavior") || "";
  const oldHtmlPriority = html?.style.getPropertyPriority("scroll-behavior") || "";
  const oldBodyValue = body?.style.getPropertyValue("scroll-behavior") || "";
  const oldBodyPriority = body?.style.getPropertyPriority("scroll-behavior") || "";

  try {
    html?.style.setProperty("scroll-behavior", "auto", "important");
    body?.style.setProperty("scroll-behavior", "auto", "important");
    window.scrollBy(0, Number(delta) || 0);
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  } finally {
    restore(html, "scroll-behavior", oldHtmlValue, oldHtmlPriority);
    restore(body, "scroll-behavior", oldBodyValue, oldBodyPriority);
  }
  return { scrollY: window.scrollY || 0 };
}

function showCapturePreview(durationMs) {
  const marker = "data-longcapture-browser-agent-preview";
  const existing = document.querySelector(`[${marker}="1"]`);
  if (existing) existing.remove();

  const overlay = document.createElement("div");
  overlay.setAttribute(marker, "1");
  overlay.style.setProperty("position", "fixed", "important");
  overlay.style.setProperty("left", "0", "important");
  overlay.style.setProperty("top", "0", "important");
  overlay.style.setProperty("width", "100vw", "important");
  overlay.style.setProperty("height", "100vh", "important");
  overlay.style.setProperty("box-sizing", "border-box", "important");
  overlay.style.setProperty("border", "3px solid #1687ff", "important");
  overlay.style.setProperty("background", "rgba(22,135,255,0.035)", "important");
  overlay.style.setProperty("z-index", "2147483647", "important");
  overlay.style.setProperty("pointer-events", "none", "important");
  overlay.style.setProperty("font", "600 14px system-ui, sans-serif", "important");
  overlay.style.setProperty("color", "white", "important");

  const label = document.createElement("div");
  label.textContent = "LongCapture Browser Assisted Capture · F8 again to stop";
  label.style.setProperty("position", "absolute", "important");
  label.style.setProperty("top", "10px", "important");
  label.style.setProperty("left", "10px", "important");
  label.style.setProperty("padding", "7px 10px", "important");
  label.style.setProperty("border-radius", "7px", "important");
  label.style.setProperty("background", "rgba(0,0,0,0.78)", "important");
  overlay.appendChild(label);
  (document.documentElement || document.body).appendChild(overlay);

  return new Promise(resolve => {
    setTimeout(() => {
      try { overlay.remove(); } catch (_) { }
      resolve({ shown: true, viewportWidth: window.innerWidth, viewportHeight: window.innerHeight });
    }, Math.max(250, Number(durationMs) || 650));
  });
}

function hideSafeFixedCandidates() {
  const marker = "data-longcapture-browser-agent-v01-hidden";
  const valueAttr = "data-longcapture-browser-agent-v01-visibility";
  const priorityAttr = "data-longcapture-browser-agent-v01-visibility-priority";
  const viewportWidth = window.innerWidth;
  const viewportHeight = window.innerHeight;
  const viewportArea = Math.max(1, viewportWidth * viewportHeight);
  let hiddenCount = 0;

  for (const element of document.querySelectorAll(`[${marker}="1"]`)) {
    const value = element.getAttribute(valueAttr) || "";
    const priority = element.getAttribute(priorityAttr) || "";
    if (value) element.style.setProperty("visibility", value, priority);
    else element.style.removeProperty("visibility");
    element.removeAttribute(marker);
    element.removeAttribute(valueAttr);
    element.removeAttribute(priorityAttr);
  }

  const elements = document.body ? document.body.getElementsByTagName("*") : [];
  for (const element of elements) {
    const style = getComputedStyle(element);
    if (style.position !== "fixed" && style.position !== "sticky") continue;
    if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity || 1) <= 0.02) continue;

    const rect = element.getBoundingClientRect();
    if (!rect || rect.width <= 1 || rect.height <= 1) continue;
    if (rect.bottom <= 0 || rect.right <= 0 || rect.top >= viewportHeight || rect.left >= viewportWidth) continue;

    const touchesEdge = Math.abs(rect.top) <= 3 || Math.abs(rect.bottom - viewportHeight) <= 3 || Math.abs(rect.left) <= 3 || Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky" && !touchesEdge) continue;

    const visibleWidth = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
    const visibleHeight = Math.max(0, Math.min(viewportHeight, rect.bottom) - Math.max(0, rect.top));
    if ((visibleWidth * visibleHeight) / viewportArea >= 0.65) continue;

    element.setAttribute(marker, "1");
    element.setAttribute(valueAttr, element.style.getPropertyValue("visibility") || "");
    element.setAttribute(priorityAttr, element.style.getPropertyPriority("visibility") || "");
    element.style.setProperty("visibility", "hidden", "important");
    hiddenCount++;
  }

  return { hiddenCount };
}

function restoreHiddenCandidates() {
  const marker = "data-longcapture-browser-agent-v01-hidden";
  const valueAttr = "data-longcapture-browser-agent-v01-visibility";
  const priorityAttr = "data-longcapture-browser-agent-v01-visibility-priority";
  let restoredCount = 0;

  for (const element of document.querySelectorAll(`[${marker}="1"]`)) {
    const value = element.getAttribute(valueAttr) || "";
    const priority = element.getAttribute(priorityAttr) || "";
    if (value) element.style.setProperty("visibility", value, priority);
    else element.style.removeProperty("visibility");
    element.removeAttribute(marker);
    element.removeAttribute(valueAttr);
    element.removeAttribute(priorityAttr);
    restoredCount++;
  }
  return { restoredCount };
}

async function setBadge(text, title) {
  try {
    await chrome.action.setBadgeText({ text });
    await chrome.action.setTitle({ title });
  } catch (_) { }
}

function clampNumber(value, min, max, fallback) {
  const number = Number(value);
  if (!Number.isFinite(number)) return fallback;
  return Math.min(max, Math.max(min, number));
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
