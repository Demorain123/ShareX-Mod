# LongCapture Browser Agent v0.1 PoC

This is deliberately a **small proof-of-concept**. It does not replace Normal Long Capture and does not modify the RC6 raster capture/matching path.

## What v0.1 proves

The Chrome extension is only a thin active-tab helper. For each viewport it reports document geometry and fixed/sticky candidates, optionally hides safe fixed/sticky candidates for that one screenshot, captures the active tab, restores the page, scrolls the document, and returns the PNG to LongCapture.

LongCapture remains responsible for:

- saving every raw PNG frame;
- validating viewport/scroll geometry;
- stitching the final long PNG;
- refusing geometry gaps instead of inventing pixels;
- saving `session.json` evidence beside the frames.

The stitcher uses browser-provided absolute `scrollY` rather than image-match guesses and writes the final PNG with bounded memory. It never allocates one giant long-image bitmap.

## Intentionally NOT in v0.1

- PDF capture/export
- OCR
- editor/annotations
- extension-side stitching
- Chrome debugger/CDP attachment
- `<all_urls>` host permission
- inner-scroll-container discovery
- iframe traversal
- special lazy-load automation
- site-specific recipes

Those only become candidates after this PoC proves the basic path on the same Linux.do torture page used for RC6 testing.

## Install once

1. Open `chrome://extensions`.
2. Enable **Developer mode**.
3. Choose **Load unpacked** and select this package's `BrowserAgent\Extension` folder.
4. Copy the 32-character extension ID Chrome shows.
5. Run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` and paste that extension ID.

The installer writes only the current user's Chrome Native Messaging registration (`HKCU`). It does not require administrator rights.

## Run the PoC

1. Run `START-BROWSER-AGENT-POC.cmd` next to `LongCapture.exe`.
2. Open the target page in normal daily Chrome and make that tab active.
3. Click the **LongCapture Browser Agent v0.1 PoC** extension icon. The PoC window should change to `native host connected` and show the attached tab.
4. Press **Start Browser Assisted Capture**.
5. Keep that tab active while capture runs. Switching to another tab is treated as an error rather than silently capturing the wrong page.
6. Press **Stop** if you want a partial capture; already-saved frames are still stitched.

Output is stored under:

`BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`

with:

- `frames\frame-001.png`, `frame-002.png`, ...
- `session.json`
- `LongCapture-BrowserAgent-v01-*.png`

## PoC pass criteria

For the first real comparison, use the same Linux.do page and viewport used for RC6.

The path is interesting only if all of these are true:

- the long image remains geometrically continuous and clear;
- the recurring `Back / xx / 312` fixed control does not repeat down the body (the first viewport may retain it intentionally);
- a long run can exceed 100 viewports without memory growth caused by a giant destination bitmap;
- raw frames and `session.json` are sufficient to reproduce/diagnose a failure;
- Normal Long Capture remains unchanged and available as the all-app fallback.

If the document itself does not scroll because the site uses a nested scroller, v0.1 stops with `document-did-not-scroll`. That is an explicit PoC boundary, not a reason to add inner-scroller complexity before the basic route is proven.
