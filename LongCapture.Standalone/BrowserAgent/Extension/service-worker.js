const HOST_NAME = "com.longcapture.browser_agent";
const MIN_CAPTURE_INTERVAL_MS = 520;
let nativePort = null;
let target = null;

chrome.action.onClicked.addListener(async (tab) => {
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
        protocolVersion: "0.1"
      }
    });
    await setBadge("ON", "LongCapture Browser Agent attached to this tab");
  } catch (error) {
    console.error("LongCapture Browser Agent could not connect:", error);
    nativePort = null;
    await setBadge("ERR", String(error?.message || error));
  }
});

async function handleDesktopMessage(message) {
  if (!nativePort || !message || typeof message.id !== "number" || typeof message.type !== "string") {
    return;
  }

  try {
    await assertTargetIsActive();
    const payload = message.payload || {};
    let result;

    switch (message.type) {
      case "begin":
        result = await beginCapture(payload);
        break;
      case "probe":
        result = await collectState();
        break;
      case "captureAndScroll":
        result = await captureAndScroll(payload);
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

async function beginCapture(payload) {
  const settleMs = clampNumber(payload.settleMs, MIN_CAPTURE_INTERVAL_MS, 5000, 550);
  await executeInTarget(scrollDocumentToTop);
  await sleep(settleMs);
  return await collectState();
}

async function captureAndScroll(payload) {
  const settleMs = clampNumber(payload.settleMs, MIN_CAPTURE_INTERVAL_MS, 5000, 550);
  const overlapRatio = clampNumber(payload.overlapRatio, 0.05, 0.45, 0.18);
  const hideFixed = payload.hideFixed === true;

  const before = await collectState();
  const atBottom = before.scrollY + before.viewportHeight >= before.scrollHeight - 2;
  let hiddenCount = 0;
  let pngDataUrl;

  try {
    if (hideFixed) {
      const hidden = await executeInTarget(hideSafeFixedCandidates);
      hiddenCount = Number(hidden?.hiddenCount || 0);
      // Give the compositor one animation frame to apply visibility changes.
      await sleep(34);
    }

    await assertTargetIsActive();
    pngDataUrl = await chrome.tabs.captureVisibleTab(target.windowId, { format: "png" });
  } finally {
    if (hideFixed) {
      try { await executeInTarget(restoreHiddenCandidates); } catch (_) { }
    }
  }

  if (!atBottom) {
    const delta = Math.max(1, Math.floor(before.viewportHeight * (1 - overlapRatio)));
    await executeInTarget(scrollDocumentBy, [delta]);
    await sleep(settleMs);
  }

  const after = await collectState();
  return {
    before,
    after,
    hiddenCount,
    atBottom,
    pngDataUrl
  };
}

async function assertTargetIsActive() {
  if (!target || typeof target.tabId !== "number") {
    throw new Error("No Chrome tab is attached. Click the LongCapture Browser Agent extension icon on the target tab.");
  }

  const tab = await chrome.tabs.get(target.tabId);
  if (!tab.active || tab.windowId !== target.windowId) {
    throw new Error("The attached Chrome tab is no longer the active tab in its window. Reactivate it before continuing.");
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

  const fixedCandidates = [];
  const elements = document.body ? document.body.getElementsByTagName("*") : [];
  let elementCount = 0;

  for (const element of elements) {
    elementCount++;
    if (fixedCandidates.length >= 64) continue;

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
    const areaRatio = Math.max(0, Math.min(viewportWidth, rect.width)) * Math.max(0, Math.min(viewportHeight, rect.height)) / viewportArea;
    if (areaRatio >= 0.65) continue;

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

  return {
    scrollY,
    scrollHeight,
    viewportWidth,
    viewportHeight,
    devicePixelRatio: window.devicePixelRatio || 1,
    fixedCandidateCount: fixedCandidates.length,
    fixedCandidates,
    stateHash: `${Math.round(scrollY)}:${Math.round(scrollHeight)}:${elementCount}:${document.body?.childElementCount || 0}`
  };
}

async function scrollDocumentToTop() {
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
    window.scrollTo(0, 0);
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

function hideSafeFixedCandidates() {
  const marker = "data-longcapture-browser-agent-v01-hidden";
  const valueAttr = "data-longcapture-browser-agent-v01-visibility";
  const priorityAttr = "data-longcapture-browser-agent-v01-visibility-priority";
  const viewportWidth = window.innerWidth;
  const viewportHeight = window.innerHeight;
  const viewportArea = Math.max(1, viewportWidth * viewportHeight);
  let hiddenCount = 0;

  // Clean up any marker left by an interrupted previous command. This logic is
  // intentionally inlined because executeScript serializes this function alone.
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
