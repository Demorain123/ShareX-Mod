# LongCapture Browser Agent v0.1.6 — Calibrated Adaptive

Browser Agent remains integrated into the normal `LongCapture.exe` main window. Normal Long Capture / RC6 remains available; Browser Agent is an optional browser-aware backend, not a replacement for LongCapture.

## What v0.1.6 changes

v0.1.5 introduced fixed speed presets, Browser adaptive speed, precision-based local repair, layout-shift evidence and inset-aware sticky suppression. v0.1.6 addresses the next problem: one preset table cannot be assumed optimal for every PC, browser build, page, network condition or GPU/compositor timing.

The new design therefore treats the five fixed gears as **safe bounded reference points**, adds a **real local Browser benchmark**, learns a small persistent timing profile from successful real frames, and exposes the **effective parameters live** while Browser Assisted Capture is running.

It deliberately does **not** put an LLM, OCR model or neural network into the per-frame screenshot hot path. The first learning layer is deterministic, measurable, bounded and auditable. The stored evidence can later support offline contextual-bandit/cost-model experiments without risking uncontrolled exploration during a real capture.

## Capture speed

The main GUI still exposes **Capture speed** in every mode.

For Normal / Smart Web / Teach / Run Recipe the fixed presets map to the existing visible controls:

| Fixed preset | Start delay | Scroll settle | Scroll amount |
|---|---:|---:|---:|
| Very Low | 500 ms | 1200 ms | 1 |
| Low | 400 ms | 850 ms | 2 |
| Medium | 300 ms | 550 ms | 3 |
| High | 150 ms | 350 ms | 4 |
| Very High | 0 ms | 220 ms | 5 |

Browser Assisted uses browser-specific safe reference gears:

| Browser gear | Start delay | Page settle | Overlap |
|---|---:|---:|---:|
| Very Low | 500 ms | 1800 ms | 45% |
| Low | 350 ms | 1350 ms | 40% |
| Medium | 200 ms | 950 ms | 34% |
| High | 100 ms | 700 ms | 27% |
| Very High | 0 ms | 520 ms | 20% |

Chrome documents `captureVisibleTab()` as expensive and limits it to two calls per second. v0.1.6 therefore enforces a 520 ms minimum capture-start interval even if calibration or a future preset tries to go faster.

## Browser benchmark / local calibration

In **Browser Assisted Capture**, attach the target Chromium/Helium tab first and press **Browser benchmark**.

The benchmark measures the actual attached-tab pipeline instead of benchmarking synthetic CPU/GPU arithmetic:

- Native Messaging / DOM probe round-trip time;
- real `captureVisibleTab()` duration;
- page-stability wait/activity time;
- the longest observed false-quiet interval before later DOM/layout activity resumes.

The profile uses exponentially smoothed observations plus observed variation. Its output is always clamped back into the existing safe gear envelope. Current hard bounds include Page settle 450–3000 ms, Overlap 20–50%, and Max wait 3000–12000 ms.

The profile is stored locally at:

`BrowserAgentCaptures\Calibration\browser-agent-calibration-v016.json`

Writes are atomic. A malformed/corrupt profile is quarantined and ignored rather than trusted.

**Use local calibration** is user-selectable. Turning it off returns Browser Assisted to the reference gear values.

## Continuous local learning

A benchmark is only the starting point. When local calibration is enabled, accepted real Browser Assisted frames update the same small profile using measured capture/stability/seam evidence.

The controller still obeys the fixed safe ladder. Risk can reduce the current gear immediately; clean frames recover gradually toward the selected target and never above it. Learning changes the bounded timing/overlap values inside a gear; it does not gain permission to invent arbitrary settings.

The calibration confidence increases as benchmark and accepted-frame evidence accumulates. `session.json` records the confidence at start/end and the effective values actually used for each frame.

## Live adaptive parameters

Enable **Live params** in Browser Assisted Capture. During the capture a small no-activate, capture-excluded monitor shows:

- frame number;
- selected target gear and current gear;
- effective Start delay / Page settle / Max wait / Overlap;
- actual settle/activity/capture duration measured for the latest frame;
- adaptive risk score and reasons;
- local calibration confidence and sample count.

The monitor does not take keyboard focus and is excluded from capture, so it is intended to make adaptive decisions observable without contaminating the screenshot.

## Adaptive targets and repair precision

Browser Assisted retains:

- **Adaptive · Robust** — target Medium;
- **Adaptive · Balanced** — target High;
- **Adaptive · High speed** — target Very High.

Risk evidence includes stability timeout/long settle, mutation/resize activity, pending images, height growth/lazy loading, post-load layout shifts, capture-state changes, PNG overlap differences, visual correction and false-quiet behavior. Strong evidence can downshift two gears; moderate evidence downshifts one. Clean frames recover one gear at a time.

Repair precision remains **Low / Medium / High**. It controls risk sensitivity, post-review candidate budget and repair attempts. A natural full-page completion can selectively revisit marked logical Y positions and re-verify both neighboring overlaps. Unresolved marked sections force **Partial / quality-review-unresolved** rather than a false Complete.

## Sticky / fixed controls and hard stop

Inset-aware sticky suppression from v0.1.5 remains. A `position: sticky` element can be recognized as pinned when its screen-space position matches its computed CSS `top`, `bottom`, `left` or `right` inset; it does not have to touch viewport coordinate zero.

F8 hard cancellation also remains. LongCapture cancels both its desktop wait and the active browser operation over Native Messaging.

## Diagnostics

Important Browser markers now include:

- `[USER_ACTION]` — UI actions/settings;
- `[BA_TIMELINE]` — capture milestones and F8 stop;
- `[BA_REQ]` — Native Messaging request lifecycle;
- `[BA_AGENT]` — browser-side region/scroll/cancel events;
- `[BA_ADAPT]` — frame risk and gear changes;
- `[BA_REPAIR]` — local review/re-capture attempts;
- `[BA_CALIB]` — benchmark/profile learning;
- `[BA_LIVE]` — live-monitor lifecycle.

The diagnostics exporter includes the active main-process log plus recent logs so the calibration/adaptive timeline can be reconstructed.

## Permission boundary

The extension still declares exactly `activeTab`, `scripting`, and `nativeMessaging`. It does not request `<all_urls>` or `debugger`, does not attach CDP, and adds no ML/OCR hot-path dependency.

## Quality gate

v0.1.6 encodes the release loop in CI. A deterministic 100-point **PRE-BUILD QUALITY SCORE** runs before restore/publish. A build is forbidden below 95/100 or with any critical failure. Only after that gate may Windows publish run; then Browser deterministic tests and RC6 QUICK must pass, including packaged Start/Stop and 100/125/150/200% layout-pressure tests. Packaging happens only after those tests pass, and the final ZIP is extracted into a fresh directory and self-tested again.

This score is a release-process gate, not proof of final pixels on every real website. Real signed-in pages, GPU composition, animations, lazy/infinite feeds and machine-specific timing still require controlled user testing.

## Install / update

Replace the portable package, reload the unpacked LongCapture Browser Agent extension, and start `LongCapture.exe`. If the extension ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.

Browser Assisted sessions remain under `BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`; diagnostics are under `BrowserAgentCaptures\Diagnostics\`; local calibration is under `BrowserAgentCaptures\Calibration\`.
