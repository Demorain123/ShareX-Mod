# LongCapture Browser Agent v0.1.2 — Integrated Auto-End

Browser Agent is now integrated into the normal `LongCapture.exe` main window. `START-BROWSER-AGENT-POC.cmd` remains only as a diagnostic/development entry point.

Normal Long Capture / RC6 remains available and its raster path is unchanged.

## Normal daily workflow

1. Start `LongCapture.exe`.
2. Choose **Browser Assisted Capture** in Capture mode.
3. Activate the target Chromium/Helium tab.
4. Click the LongCapture Browser Agent extension icon, or press **Ctrl+Shift+L**, to attach that tab.
5. Press **F8**.
6. The browser viewport is outlined briefly, then capture starts from the document top.
7. Do nothing and Browser Agent continues until the document end is confirmed.
8. Press **F8** again at any time to stop early and save a **Partial** capture.

There is no normal user-facing 200-frame completion limit anymore. v0.1.2 uses an internal high safety limit only to prevent a broken/infinite page from consuming unbounded disk/time. Hitting that safety limit is reported as **Partial**, never Complete.

## True page-end detection

A single `scrollY + viewportHeight >= scrollHeight` sample is not enough for dynamic pages.

At an apparent bottom, v0.1.2:

- re-enters the bottom trigger zone;
- waits through repeated DOM/layout quiet windows;
- watches `MutationObserver`, `ResizeObserver`, `scrollHeight`, nearby image readiness and fonts;
- gives lazy/infinite loaders time to append more content;
- repeats bottom confirmation three times;
- continues capture if `scrollHeight` grows;
- declares Complete only after the bottom remains stable.

For pages that expose a fixed/sticky progress control such as `218 / 322`, Browser Agent also records that text directly from the DOM as a **hint**. It is not the sole stop condition because many sites have no reliable page counter.

`session.json` records the counter hint, estimated frames to the currently loaded end, end-confirmation rounds/growth/confidence, plus the v0.1.1 stability and overlap evidence.

## Dynamic-page protection retained from v0.1.1

Before accepting each viewport, the extension waits for DOM/layout stability. Near lazy boundaries it warms the upcoming region before capture. LongCapture then validates real PNG overlap between adjacent viewports and can re-capture the recent viewport window if the page changed underneath it.

The final long PNG is still stitched by LongCapture with bounded memory. The extension does not stitch the page itself.

## Shortcuts

- **F8**: start Browser Assisted Capture; press again for Partial manual stop.
- **Ctrl+Shift+L**: attach the active Chromium/Helium tab to LongCapture.

## Permission boundary

The extension still declares only:

- `activeTab`
- `scripting`
- `nativeMessaging`

It does not request `<all_urls>` or `debugger` and does not attach CDP.

## Install / update

If the extension is already loaded unpacked, replace/update the package and press **Reload** on the browser's extensions page. If the extension ID remains the same, Native Messaging registration does not need to be repeated. If the ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again with the new ID.

Fresh install:

1. Open the Chromium browser's extensions page.
2. Enable Developer mode.
3. Load unpacked: `BrowserAgent\Extension`.
4. Copy the extension ID.
5. Run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` and paste the ID.
6. Start `LongCapture.exe` normally.

## Output / diagnostics

Browser Assisted sessions are stored beside the portable app under:

`BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`

This intentionally avoids moving large raw-frame sessions into a system-drive Pictures folder.

Each session contains raw PNG frames, `session.json`, and the final stitched PNG. Diagnostics ZIPs are stored under `BrowserAgentCaptures\Diagnostics\`.

## Completion semantics

- `Complete / document-bottom-confirmed`: repeated stable-bottom confirmation passed.
- `Partial / manual-stop`: the user pressed F8 again.
- `Partial / safety-frame-limit`: the internal runaway guard was reached.
- other Partial/Failed states: capture stopped because scrolling, stability or overlap evidence was not safe enough to claim a full page.

## Intentionally still out of scope

- OCR as a default dependency (DOM progress hints are used first);
- inner scroll-container discovery;
- iframe traversal;
- PDF/OCR/editor integration;
- site-specific recipes;
- debugger/CDP.
