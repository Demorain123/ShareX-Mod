# LongCapture Browser Agent v0.1.4 — Hard Stop + Timeline

Browser Agent remains integrated into the normal `LongCapture.exe` main window. Normal Long Capture / RC6 remains available and unchanged.

## Why v0.1.4 exists

Real v0.1.3 Linux.do diagnostics exposed a pre-frame control-flow bug rather than a stitching failure:

- F8 region selection succeeded;
- the selected region was stored correctly;
- the optional global preload was enabled;
- **zero capture frames** had been accepted when the user saw the page rapidly moving;
- the v0.1.3 preload implementation jumped to the currently known document bottom to trigger lazy/infinite loading;
- pressing F8 cancelled the desktop request, but the already-running asynchronous browser-side preload continued scrolling.

v0.1.4 therefore separates **optional pre-scan**, **real capture**, and **hard cancellation**, and adds timestamped telemetry for each boundary.

## Normal workflow

1. Start `LongCapture.exe`.
2. Choose **Browser Assisted Capture**.
3. Attach the active Chromium/Helium tab with the extension icon or **Ctrl+Shift+L**.
4. Press **F8**.
5. Drag the exact capture region. **Esc** cancels; **Enter** uses the full browser viewport.
6. By default, LongCapture starts the real incremental capture from the top. The old global bottom-seeking preload is **OFF by default**.
7. Do nothing to continue until confirmed page end, or press **F8** again for a hard manual stop.

## F8 hard stop

v0.1.4 does not rely on desktop `CancellationToken` cancellation alone.

When F8 is pressed during Browser Assisted Capture:

- LongCapture records the user-stop timestamp;
- the desktop request wait is cancelled immediately;
- an explicit `cancel` command is also sent to the extension over the existing native-messaging Port;
- the extension sets an in-page cancellation marker and emits a cancel event;
- region selection, page-stability waits, pre-scan, and scrolling functions check that marker and abort;
- a cancellation before frame 1 is reported as **cancelled**, not as a generic capture failure.

This is required because a script injected with `chrome.scripting.executeScript()` may itself be awaiting a Promise; abandoning the desktop-side request does not inherently terminate that browser-side Promise.

## Optional gentle pre-scan

The Browser-mode checkbox is now labelled:

**Optional gentle lazy-content pre-scan (visible, slower)**

It is OFF by default.

When explicitly enabled, v0.1.4 no longer jumps directly to `scrollHeight - viewportHeight`. It walks forward in bounded viewport-sized steps, waits for stability, emits a timestamped `preload-step` event after every step, remains cancellable, and returns to the top before the real pass only if it was not cancelled.

Per-frame lazy-boundary warm-up and true-end confirmation remain active even when this optional global pre-scan is OFF.

## Debug mode in Browser Assisted Capture

**Debug: GUI + raw replay evidence** is user-selectable in Browser Assisted Capture again. The internal-helper debug option follows the parent Debug checkbox. This allows real screenshots of the LongCapture GUI while preserving the existing debug/window-affinity behavior.

## Timestamped diagnostics timeline

v0.1.4 upgrades the normal LongCapture log with Browser Agent phase telemetry. Diagnostics ZIP export already includes the overlapping LongCapture logs, so no separate manual log-copy step is required.

Important markers include:

- `[USER_ACTION]` — capture-button clicks and Browser-mode setting changes;
- `[BA_TIMELINE]` — capture/connection milestones, status text and F8 stop;
- `[BA_REQ]` — native request id, command type, create/send/complete/cancel/error phase, elapsed milliseconds;
- `[BA_AGENT]` — extension-side timestamped events such as region selection, optional pre-scan steps, real capture scroll steps and cancel acknowledgement.

This makes it possible to reconstruct a run chronologically: attach → settings → F8 → region selected → optional pre-scan → frame/scroll requests → recovery/end checks → F8 stop/automatic completion.

## Browser quality controls

In Browser Assisted Capture:

- **Start delay (ms)** — delay before the real pass;
- **Page settle (ms)** — DOM/layout quiet window;
- **Overlap (%)** — retained frame overlap;
- **DOM + visual verification** — required scroll engine;
- **Optional gentle lazy-content pre-scan** — OFF by default;
- **Full browser viewport** — skips manual region selection when enabled;
- **Debug: GUI + raw replay evidence** — user-selectable.

## Visual geometry retained from v0.1.3

The browser reports DOM scroll positions as a prediction. LongCapture also resolves visual movement from adjacent PNGs and records expected DOM delta, resolved PNG delta, correction, alignment score and confidence. The streaming compositor uses cumulative resolved movement rather than treating absolute DOM `scrollY` as sufficient placement proof.

## Automatic completion

There is no normal 200-frame completion limit. Browser Agent continues until repeated stable-bottom confirmation succeeds. A high internal frame limit remains only as a runaway guard and is reported as Partial.

Visible DOM counters such as `319 / 322` are supporting evidence, not the sole completion rule.

## Permission boundary

The extension still declares only:

- `activeTab`
- `scripting`
- `nativeMessaging`

It does not request `<all_urls>` or `debugger` and does not attach CDP.

## Install / update

For an existing unpacked installation:

1. Replace the portable package.
2. Open the browser extensions page and press **Reload** on LongCapture Browser Agent.
3. If the extension ID stays the same, native-host registration normally remains valid.
4. If the ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again with the new ID.
5. Start `LongCapture.exe` normally.

## Output / diagnostics

Browser Assisted sessions remain beside the portable app under:

`BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`

Diagnostics ZIPs are written under:

`BrowserAgentCaptures\Diagnostics\`

For a failed or suspicious real run, one Diagnostics ZIP is normally enough; v0.1.4 is designed to preserve the Browser Agent request/event timeline inside it.
