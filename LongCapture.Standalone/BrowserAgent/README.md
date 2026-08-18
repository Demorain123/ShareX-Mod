# LongCapture Browser Agent v0.1.1 — Dynamic Page Stability

Browser Agent v0.1.1 is still a deliberately small proof-of-concept. It **does not replace Normal Long Capture** and it does not modify the RC6 raster capture/matching path.

The first real v0.1 Linux.do run reached 155 frames / 2552×173612, proving the active-tab transport, fixed/sticky hiding and bounded-memory stitch path. It also exposed the next problem: the page's loaded `scrollHeight` grew from about 15.8k CSS px to 60.6k CSS px while capture was running, so a viewport could be captured before lazy/dynamic content had finished materializing.

v0.1.1 targets exactly that failure mode.

## What changed in v0.1.1

### 1. DOM/layout stability gate

Before a viewport becomes evidence, the extension now waits for a quiet window using:

- `MutationObserver` for subtree/text and lazy-source mutations;
- `ResizeObserver` for root/body size changes;
- repeated `scrollHeight` / layout-height samples;
- nearby incomplete image checks;
- `document.fonts.status`;
- two final animation frames before accepting the page as stable.

The default quiet window is 900 ms with a 7 s maximum wait.

### 2. Lazy-boundary warm-up

When capture approaches the currently loaded document bottom, Browser Agent temporarily looks ahead without capturing, waits for the lazy/infinite loader to materialize the next chunk, then returns to the intended viewport and waits again.

This is generic; there is still no Linux.do-specific selector or recipe.

### 3. Raster overlap verification

LongCapture no longer trusts DOM geometry alone. From frame 3 onward it samples the real PNG overlap between adjacent viewports.

A frame records:

- overlap mean absolute error;
- strong-difference ratio;
- overlap pixel count;
- whether the overlap passed;
- recovery generation.

If a dynamic page changes under capture, the last three viewports are re-captured at their absolute `scrollY` positions and checked again. If they still disagree, v0.1.1 **stops safely instead of silently writing a visually broken long image**.

### 4. Better evidence

`session.json` now records per-frame:

- stability wait time;
- mutation / resize / height-change counts;
- pending images;
- stability-time `scrollHeight` growth;
- lazy warm-up activation/growth;
- whether page state changed during `captureVisibleTab`;
- overlap score;
- recovery count.

### 5. Shortcuts and diagnostics

- **F8**: global Start / Stop Browser Assisted Capture.
- **Ctrl+Shift+L**: browser-extension shortcut to attach the active tab. This can be remapped in the browser's extension-shortcuts page.
- **Open logs** button.
- **Export diagnostics ZIP** button.
- Capture failures automatically attempt to export a diagnostics ZIP containing the session evidence and LongCapture logs overlapping the capture time.

## Permission boundary

The extension still declares only:

- `activeTab`
- `scripting`
- `nativeMessaging`

v0.1.1 does **not** add:

- `<all_urls>`
- `debugger`
- CDP attachment
- extension-side stitching

## Install / update

If v0.1 was already loaded unpacked:

1. Replace the old package with the v0.1.1 package.
2. Open the browser extensions page and press **Reload** on LongCapture Browser Agent.
3. The extension ID should stay the same when the same unpacked path is reused. If it changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again with the new 32-character ID.
4. Start `START-BROWSER-AGENT-POC.cmd`.

Fresh install:

1. Open `chrome://extensions` (or your Chromium browser's extensions page).
2. Enable Developer mode.
3. Load unpacked: `BrowserAgent\Extension`.
4. Copy the extension ID.
5. Run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd`.
6. Start `START-BROWSER-AGENT-POC.cmd`.

## Run

1. Activate the target tab.
2. Click the Browser Agent icon or press **Ctrl+Shift+L**.
3. Confirm `Connection: native host connected`.
4. Press **F8** (or Start).
5. Keep the attached tab active.
6. Press **F8** again to stop early.

Output:

`BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`

contains raw frames, `session.json`, and the final long PNG.

Diagnostics ZIPs are written below:

`BrowserAgentCaptures\Diagnostics\`

## v0.1.1 real-test target

Use the same Linux.do torture page again.

Pass is now stricter than v0.1:

- 100+ viewports can run without giant-bitmap memory growth;
- fixed/sticky controls do not stamp repeatedly down the body;
- the final image remains continuous across dynamic-load boundaries;
- `session.json` shows stability/overlap/recovery telemetry;
- an unstable overlap is recovered or causes a clear safe stop — never a silent corrupt success.

Still intentionally out of scope:

- inner scroll-container discovery;
- iframe traversal;
- PDF/OCR/editor;
- site-specific recipes;
- debugger/CDP.
