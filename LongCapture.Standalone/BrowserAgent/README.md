# LongCapture Browser Agent v0.1.5 — Adaptive Quality

Browser Agent remains integrated into the normal `LongCapture.exe` main window. Normal Long Capture / RC6 remains available; the Browser Agent is an optional browser-aware capture backend, not a replacement for LongCapture.

## Why v0.1.5 exists

The latest Linux.do run showed two different real-world problems:

1. a fixed capture cadence is a poor trade-off for long dynamic pages — fast settings are desirable most of the time, but lazy loading / layout shifts need a temporary slowdown;
2. some Linux.do circular avatars remain stationary in screen space while document text moves. They are sticky UI with a non-zero CSS inset, so the earlier `touches viewport edge` heuristic could fail to suppress them.

v0.1.5 therefore adds a named speed strategy, adaptive risk control, selective post-capture repair, layout-shift evidence, and inset-aware sticky suppression.

## Capture speed

Browser Assisted Capture now exposes **Capture speed** presets. The selected preset also writes the underlying Start delay / Page settle / Overlap controls so the behavior is visible rather than hidden.

Fixed presets are constant for the whole run:

| Preset | Start delay | Settle | Overlap |
|---|---:|---:|---:|
| Very Low | 500 ms | 1800 ms | 45% |
| Low | 350 ms | 1350 ms | 40% |
| Medium | 200 ms | 950 ms | 34% |
| High | 100 ms | 700 ms | 27% |
| Very High | 0 ms | 520 ms | 20% |

Adaptive presets use the same gears but can change gear while capturing:

- **Adaptive · Robust** — target Medium;
- **Adaptive · Balanced** — target High;
- **Adaptive · High speed** — target Very High.

The adaptive controller starts at the selected target. Risk >= medium downshifts one gear; strong risk can downshift two gears. After three clean frames it recovers one gear toward the target. It never accelerates above the selected target.

The risk score reuses evidence already collected by the capture path instead of adding OCR or a heavyweight model to every frame:

- stability timeout / long settle;
- DOM mutation / resize activity;
- pending images;
- scrollHeight growth / lazy-load warm-up;
- post-load layout-shift score;
- capture-state changes;
- PNG overlap MAE / strong-difference ratio;
- unusually large visual correction relative to DOM-predicted motion.

## Repair precision

Adaptive modes expose **Repair precision**:

- **Low** — mark only higher-risk sections; up to 3 post-review candidates; 1 repair attempt each;
- **Medium** — up to 8 candidates; 2 attempts each;
- **High** — more sensitive marking, up to 16 candidates; 3 attempts each and a slower repair gear.

High-risk sections can also trigger immediate recent-window recovery during capture. On a natural full-page completion, marked sections are reviewed before final stitching. A repair repositions the existing tab to the recorded logical Y, waits with conservative settings, re-captures the exact selected region, and verifies both neighbouring overlaps. If suspicious sections remain unresolved, the result is reported as **Partial / quality-review-unresolved** rather than silently claiming Complete.

`session.json` records each frame's risk score/reasons, effective speed/overlap, repair-candidate flag, repair attempts/status, and session repair totals. Logs add `[BA_ADAPT]` and `[BA_REPAIR]` markers.

## Sticky / fixed controls

Fixed elements continue to be hidden during frames after the first. Sticky detection is now inset-aware: a sticky element can be considered pinned when its screen-space position matches its computed CSS `top`, `bottom`, `left`, or `right` inset, even when it does not literally touch the viewport edge.

This is specifically intended for controls such as forum avatars that stick below a header. A sticky element that has not yet reached its sticky inset is not hidden merely because its CSS `position` is `sticky`.

## Layout-shift evidence

When the Chromium engine supports the Layout Instability API, Browser Agent observes `layout-shift` entries without recent user input during the settle window. Count and score are added to the existing MutationObserver / ResizeObserver / pending-image / height-growth evidence. This is an extra risk signal, not the sole definition of correctness.

## Normal workflow

1. Start `LongCapture.exe`.
2. Choose **Browser Assisted Capture**.
3. Attach the active Chromium/Helium tab with the extension icon or **Ctrl+Shift+L**.
4. Choose a Capture speed. **Adaptive · Balanced** is the default.
5. For adaptive modes choose Repair precision. **Medium** is the default.
6. Keep **Optional gentle lazy-content pre-scan** OFF unless deliberately testing it.
7. Press **F8**, then drag the exact capture region. Esc cancels; Enter uses the full browser viewport.
8. Do nothing to continue until confirmed page end, or press **F8** again for a hard manual stop.

## F8 hard stop retained from v0.1.4

F8 cancels both the desktop-side wait and the browser-side active operation over Native Messaging. Region selection, stability waits, optional pre-scan and scrolling all observe the cancellation marker. A cancellation before frame 1 is reported as cancelled, not as a generic capture failure.

## Diagnostics

Diagnostics ZIP export now always includes the active main-process log plus the most recent LongCapture logs, in addition to the session-window selection. This closes the case where filesystem timestamp filtering could omit the log that contains the current run.

Important markers:

- `[USER_ACTION]` — button / setting changes;
- `[BA_TIMELINE]` — capture milestones and F8 stop;
- `[BA_REQ]` — Native Messaging request lifecycle and elapsed time;
- `[BA_AGENT]` — browser-side region / scroll / cancel events;
- `[BA_ADAPT]` — per-frame risk and speed changes;
- `[BA_REPAIR]` — local review / re-capture attempts and results.

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
3. If the extension ID stays the same, Native Host registration normally remains valid.
4. If the ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again with the new ID.
5. Start `LongCapture.exe` normally.

## Output

Browser Assisted sessions remain beside the portable app under:

`BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`

Diagnostics ZIPs are written under:

`BrowserAgentCaptures\Diagnostics\`

## Deliberate scope limit

v0.1.5 makes the fixed/adaptive speed controller and precision-based local repair real in **Browser Assisted Capture** first, because that mode has DOM/layout/loading evidence needed for safe adaptation. It does not pretend that the same adaptive controller is already wired into Normal/Smart Web/Teach/Recipe engines. Their existing speed controls and capture paths remain available and must get mode-specific integration instead of a cosmetic shared dropdown.

## CI gate

The v0.1.5 gate applies the complete RC6 + Browser Agent overlay chain, verifies the adaptive/sticky/logging source invariants and permission boundary, publishes self-contained win-x64, runs the Browser Agent deterministic self-test (including adaptive downshift/recovery), runs the existing RC6 QUICK regression suite, packages the final ZIP, extracts it into a fresh directory, reruns the Browser Agent self-test, and only then uploads the artifact.
