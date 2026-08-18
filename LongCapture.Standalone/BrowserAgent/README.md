# LongCapture Browser Agent v0.1.3 — Quality Region

Browser Agent remains integrated into the normal `LongCapture.exe` main window. `START-BROWSER-AGENT-POC.cmd` is diagnostic/development only. Normal Long Capture / RC6 remains available and its raster path is unchanged.

## What v0.1.3 changes

v0.1.3 is based on the real v0.1.2 Linux.do evidence: the run could reach 144 verified frames and save a Partial result, but the later part could still look visually wrong even while the old sparse overlap gate passed. The next gate therefore stops treating DOM `scrollY` as sufficient proof of compositor placement.

### Real F8 region selection

The normal workflow is now:

1. Start `LongCapture.exe`.
2. Choose **Browser Assisted Capture**.
3. Attach the active Chromium/Helium tab by clicking the extension icon or pressing **Ctrl+Shift+L**.
4. Press **F8**.
5. LongCapture hides and the page shows a real drag-to-select overlay. Drag the exact capture region. **Esc** cancels; **Enter** selects the full browser viewport.
6. If dynamic-content preload is enabled, Browser Agent materializes as much lazy/infinite content as practical, returns to the document top, then begins the real capture.
7. Do nothing and capture continues until the page end is confirmed. Press **F8** again to stop early and save a **Partial** result.

The selected rectangle is applied to every `captureVisibleTab()` PNG on the LongCapture side, so the final long image has the selected width/viewport band rather than always using the full tab viewport.

### Browser-mode quality controls

The integrated main UI no longer shows all Normal-capture settings as disabled. In **Browser Assisted Capture** the relevant controls are repurposed as:

- **Start delay (ms)** — delay before the real pass begins;
- **Page settle (ms)** — DOM/layout quiet window before accepting a frame;
- **Overlap (%)** — real overlap retained between Browser Agent frames;
- **Dynamic content** — preload lazy/infinite content before the real pass;
- **Full browser viewport** — skip drag selection when explicitly enabled.

The scroll engine itself remains DOM + visual verification and is intentionally not switchable to MouseWheel/WheelMessage in Browser mode.

### Responsive integrated UI

The top target/action strip is reflowed into a responsive two-row layout instead of five fixed-width columns. The main form uses DPI-aware `TableLayoutPanel` layout and scrolling so long labels/buttons do not overlap as easily on scaled displays.

### Dynamic-content preload

Before the real capture, the default quality path repeatedly enters the currently loaded bottom trigger zone, waits for DOM/layout stability, lets lazy/infinite loaders append content, and returns to the top. This does not replace the per-frame lazy-boundary and true-end checks; it reduces how much of the page is changing underneath already accepted frames.

### DOM prediction + visual geometry

The browser still reports exact DOM scroll positions, but LongCapture now treats that as the **predicted** movement. For each adjacent PNG pair it searches a bounded window around the DOM prediction and uses multi-band visual overlap evidence to resolve small scroll-anchor/layout drift.

`session.json` records:

- expected DOM delta in physical pixels;
- resolved PNG delta;
- visual correction offset;
- alignment score/confidence;
- capture-region geometry;
- preload duration/growth/progress;
- the existing stability, lazy-load, overlap, recovery and page-counter evidence.

The streaming compositor uses cumulative **resolved** movement (falling back to DOM prediction where a visual result is intentionally unavailable, such as the first fixed-control transition) instead of placing every frame solely at absolute `scrollY × scaleY`.

## Automatic completion

There is no normal 200-frame completion limit. Browser Agent continues until repeated stable-bottom confirmation succeeds. A high internal safety limit remains only as a runaway guard and is reported as **Partial**, never Complete.

A visible DOM counter such as `142 / 322` remains supporting evidence, not the sole completion rule.

## Fixed/sticky and dynamic-page protection

The v0.1.1/v0.1.2 protections remain:

- MutationObserver / ResizeObserver stability windows;
- font/image readiness checks;
- lazy-boundary warm-up;
- fixed/sticky suppression during frames after the first;
- real PNG overlap verification;
- recent-window recapture when page state changes;
- repeated stable-bottom confirmation.

## Permission boundary

The extension still declares only:

- `activeTab`
- `scripting`
- `nativeMessaging`

It does not request `<all_urls>` or `debugger` and does not attach CDP.

## Install / update

If the extension is already loaded unpacked:

1. Replace/update the portable package.
2. Open the browser extensions page and press **Reload** on LongCapture Browser Agent.
3. If the extension ID stays the same, Native Messaging registration normally remains valid.
4. If the ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again with the new ID.
5. Start `LongCapture.exe` normally.

Fresh install:

1. Open the Chromium browser extensions page.
2. Enable Developer mode.
3. Load unpacked `BrowserAgent\Extension`.
4. Copy its extension ID.
5. Run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` and paste the ID.
6. Start `LongCapture.exe`.

## Output and diagnostics

Browser Assisted sessions stay beside the portable app under:

`BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`

Each session contains raw cropped PNG frames, `session.json`, and the stitched PNG. Diagnostics ZIPs are written under `BrowserAgentCaptures\Diagnostics\`.

## Completion semantics

- `Complete / document-bottom-confirmed`: repeated stable-bottom confirmation passed.
- `Partial / manual-stop`: F8 was pressed again.
- `Partial / safety-frame-limit`: internal runaway guard reached.
- Failed/other Partial states: stability/scroll/visual-overlap evidence was not strong enough to claim a safe complete page.

## Still intentionally out of scope

- OCR as a default dependency;
- inner scroll-container discovery;
- iframe traversal;
- PDF/editor integration;
- site-specific recipes;
- debugger/CDP.
