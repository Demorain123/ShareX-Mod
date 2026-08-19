# LongCapture Browser Agent v0.1.5 — Adaptive Quality

Browser Agent remains integrated into the normal `LongCapture.exe` main window. Normal Long Capture / RC6 remains available; Browser Agent is an optional browser-aware backend, not a replacement for LongCapture.

## Why v0.1.5 exists

The latest Linux.do run exposed two real problems:

1. one fixed capture cadence is a poor trade-off: fast settings are desirable on stable content, but lazy loading / layout shifts need temporary slowdown and possible local re-capture;
2. some circular avatars remain stationary in screen space while document text moves. They are sticky UI with a non-zero CSS inset, so an old `touches viewport edge` heuristic could miss them.

v0.1.5 adds named speed presets, Browser adaptive risk control, precision-based local repair, layout-shift evidence, stronger diagnostics, and inset-aware sticky suppression.

## Capture speed — visible in every mode

A **Capture speed** selector is mounted in the main GUI for every capture mode.

For Normal / Smart Web / Teach / Run Recipe, v0.1.5 exposes five fixed presets and maps them to that engine's existing visible controls:

| Fixed preset | Start delay | Scroll settle | Scroll amount |
|---|---:|---:|---:|
| Very Low | 500 ms | 1200 ms | 1 |
| Low | 400 ms | 850 ms | 2 |
| Medium | 300 ms | 550 ms | 3 |
| High | 150 ms | 350 ms | 4 |
| Very High | 0 ms | 220 ms | 5 |

The preset changes the normal controls rather than replacing them, so the actual parameters remain visible and editable.

Browser Assisted Capture also exposes the same five fixed levels, with browser-specific values:

| Browser fixed preset | Start delay | Page settle | Overlap |
|---|---:|---:|---:|
| Very Low | 500 ms | 1800 ms | 45% |
| Low | 350 ms | 1350 ms | 40% |
| Medium | 200 ms | 950 ms | 34% |
| High | 100 ms | 700 ms | 27% |
| Very High | 0 ms | 520 ms | 20% |

Chrome currently limits `captureVisibleTab()` to two calls per second, so the Browser Very High gear intentionally stays near that practical ceiling instead of trying to capture dozens of frames per second.

## Adaptive mode — Browser Assisted Capture

Browser Assisted Capture adds three genuinely adaptive targets:

- **Adaptive · Robust** — target Medium;
- **Adaptive · Balanced** — target High;
- **Adaptive · High speed** — target Very High.

The controller starts at the selected target. Medium risk downshifts one gear; strong risk can downshift two. After three clean frames it recovers one gear toward the target, never above it.

The adaptive score reuses lightweight evidence already needed for reliable capture rather than running OCR or a heavy model on every frame:

- stability timeout / unusually long settle;
- DOM mutation / resize activity;
- pending images;
- scrollHeight growth / lazy-load warm-up;
- post-load layout-shift score;
- capture-state changes;
- PNG overlap MAE / strong-difference ratio;
- unusually large visual correction relative to DOM-predicted motion.

## Repair precision

Adaptive Browser mode exposes **Repair precision**:

- **Low** — higher risk threshold; up to 3 post-review candidates; 1 repair attempt each;
- **Medium** — up to 8 candidates; 2 attempts each;
- **High** — more sensitive marking; up to 16 candidates; 3 attempts each and a slower repair gear.

Strong risk can trigger immediate recent-window recovery during capture. At a natural page end, marked sections are reviewed before final stitching. A repair returns to the recorded logical Y, waits with conservative settings, re-captures the selected region, and verifies both neighbouring overlaps. If marked sections remain unresolved, the result becomes **Partial / quality-review-unresolved** instead of silently claiming Complete.

`session.json` stores risk score/reasons, effective speed/overlap, repair-candidate flag, attempts/status, and session repair totals. Logs add `[BA_ADAPT]` and `[BA_REPAIR]`.

## Sticky / fixed controls

Fixed elements continue to be hidden during frames after the first. Sticky detection is now inset-aware: a sticky element can be considered pinned when its screen-space position matches its computed CSS `top`, `bottom`, `left`, or `right` inset even if it does not literally touch the viewport edge.

This targets forum controls such as avatars that stick below a header. A sticky element that is still moving with its document content is not hidden merely because its CSS `position` is `sticky`.

## Layout-shift evidence

When Chromium supports the Layout Instability API, Browser Agent observes `layout-shift` entries without recent user input during the settle window. Count and score augment MutationObserver / ResizeObserver / image / height-growth evidence. LayoutShift is a risk signal, not the sole definition of correctness.

## Normal Browser workflow

1. Start `LongCapture.exe`.
2. Choose **Browser Assisted Capture** and attach the active Chromium/Helium tab with the extension icon or **Ctrl+Shift+L**.
3. Choose Capture speed. **Adaptive · Balanced** is the default Browser target.
4. For adaptive mode choose Repair precision; **Medium** is the default.
5. Keep **Optional gentle lazy-content pre-scan** OFF unless deliberately testing it.
6. Press **F8**, drag the exact capture region, then let the capture run. Esc cancels selection; Enter uses the full viewport.
7. Do nothing to continue until confirmed page end, or press **F8** again for a hard manual stop.

## Diagnostics and hard stop

F8 cancels both desktop waiting and the active browser operation over Native Messaging. Region selection, waits, optional pre-scan and scrolling observe the cancellation marker.

Diagnostics ZIP export always includes the active main-process log plus recent logs. Important markers are:

- `[USER_ACTION]` — button / setting changes;
- `[BA_TIMELINE]` — capture milestones and F8 stop;
- `[BA_REQ]` — Native Messaging request lifecycle and elapsed time;
- `[BA_AGENT]` — browser-side region / scroll / cancel events;
- `[BA_ADAPT]` — per-frame risk and speed changes;
- `[BA_REPAIR]` — local review / re-capture attempts and results.

## Permission boundary

The extension still declares only `activeTab`, `scripting`, and `nativeMessaging`. It does not request `<all_urls>` or `debugger` and does not attach CDP.

## Install / update

For an existing unpacked installation: replace the portable package, reload LongCapture Browser Agent in the browser extensions page, and start `LongCapture.exe`. If the extension ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.

Browser Assisted sessions remain under `BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`; Diagnostics ZIPs are under `BrowserAgentCaptures\Diagnostics\`.

## Scope boundary

The five **fixed** speed presets are available across the GUI modes in v0.1.5 and map to their existing engine controls. The **adaptive risk controller and precision-based local repair are Browser Assisted only in v0.1.5**, because Browser Assisted has the DOM/layout/loading evidence needed for those decisions. Other engines are not falsely labelled adaptive until their own evidence path is actually wired.

## CI gate

The v0.1.5 gate applies the complete RC6 + Browser Agent overlay chain, verifies adaptive/sticky/global-speed/logging invariants and permissions, publishes self-contained win-x64, runs Browser Agent deterministic self-tests including adaptive downshift/recovery, runs RC6 QUICK, packages the final ZIP, extracts it into a fresh directory, reruns Browser Agent self-test, and only then uploads the artifact.
