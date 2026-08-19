# LongCapture Browser Agent v0.1.8 — Reliability / Background Window / Requested End

Browser Assisted remains an optional backend inside the normal `LongCapture.exe` product. Normal Long Capture / RC6 stays independent. The browser component supplies capture/data/quality evidence; it does not replace LongCapture's Start/Stop, stitch, quality guard or output pipeline.

## Why v0.1.8 exists

A real 203-frame Linux.do / Discourse diagnostic capture exposed a failure that the previous adaptive score did not see: center/body text could align perfectly while a sticky avatar in the far-left rail changed screen-space position. The old overlap verifier trimmed the outer 7% of the image and reduced three bands to a median, so a one-rail defect could disappear behind two clean bands.

v0.1.8 therefore treats page rails as first-class quality evidence, adds a semantic Discourse sticky-avatar guard, and keeps the previous adaptive/calibration logic.

It also adds two workflow capabilities requested for longer unattended jobs:

- Browser Assisted may continue while its browser window is behind another desktop application;
- the user may define an explicit capture end instead of relying only on full-page end or manual F8.

## Rail-aware seam / sticky verification

The PNG overlap verifier now samples roughly the 2%–98% horizontal range and retains separate left / center / right metrics:

- band MAE;
- strong-difference ratio;
- edge-contamination flags;
- expected/resolved visual delta.

A rail that is materially worse than the center is no longer invisible in a median score. Edge evidence is persisted in `session.json` and logged with `[BA_EDGE]`.

For Discourse-family pages (including Linux.do), the browser helper also recognizes the semantic sticky state `.topic-post.sticky-avatar .topic-avatar` and hides that pinned avatar only for the screenshot transaction. Normal document avatars are not globally removed.

The packaged Browser self-test contains a side-only mutation regression: the center and right bands remain deterministic while the left rail is changed. v0.1.8 must reject/detect that case.

## Background-window Browser Assisted capture

Enable:

`Allow target browser window behind other apps (tab stays active; not minimized)`

After F8 and region selection, you may put another desktop application in front of the browser. The attached tab must remain the active tab **inside that browser window**.

Current `captureVisibleTab()` backend limitations are explicit:

- the browser window may be behind another desktop app;
- do not switch to another tab in the same browser window;
- do not minimize the browser window;
- minimized capture fails closed with a clear error instead of pretending to work.

`session.json` records `TargetWindowFocused`, `TargetWindowState`, `BackgroundWindowCapture`, and total background-window frames.

True minimized/background-tab capture belongs to the future CDP / Existing Chrome provider, not a hidden promise in this build.

## Capture end

Browser Assisted now exposes a **Capture end** row:

- `Auto · page end / F8` — current behavior; confirmed real page end or manual F8;
- `DOM progress ≥ N` — stop after the page's DOM-visible progress counter reaches N (for example `230 / 320`);
- `Frames ≥ N` — deterministic frame budget;
- `Elapsed minutes ≥ N` — time-bounded capture.

F8 always remains available as an immediate manual stop.

A requested endpoint is reported as **Requested range complete**, not as a false full-page completion. It still receives adaptive post-review before final stitch because it was an automatic endpoint; manual F8 does not start a browser-moving post-review after the user asked it to stop.

The DOM counter is deliberately preferred over OCR when available. OCR / heavier visual recognition remains a possible experimental fallback for pages whose progress exists only as pixels, but it is not added to the per-frame hot path in v0.1.8.

## Adaptive quality / calibration retained

v0.1.6 behavior remains:

- five bounded fixed Browser gears;
- Adaptive Robust / Balanced / High speed;
- Browser benchmark and persistent local calibration;
- Live params monitor;
- Low / Medium / High repair precision;
- lazy/layout/capture/overlap evidence;
- hard `captureVisibleTab()` rate guard;
- hard F8 cancellation;
- selective repair before a natural/requested automatic completion.

## Diagnostics

Important Browser markers include:

- `[USER_ACTION]` — settings / buttons;
- `[BA_TIMELINE]` — lifecycle milestones;
- `[BA_REQ]` — Native Messaging request timing;
- `[BA_AGENT]` — browser-side scroll/cancel behavior;
- `[BA_ADAPT]` — adaptive risk / gear;
- `[BA_REPAIR]` — local repair;
- `[BA_CALIB]` — calibration / learning;
- `[BA_LIVE]` — live monitor;
- `[BA_EDGE]` — left/right-rail contamination;
- `[BA_END]` — requested endpoint match.

## Permission boundary

The extension still declares exactly:

- `activeTab`
- `scripting`
- `nativeMessaging`

No `<all_urls>` and no `debugger` permission are added by v0.1.8.

## Release gate

The final v0.1.8 pipeline is not allowed to publish until all of the following pass:

1. v0.1.6 adaptive-core score >=95;
2. v0.1.7 responsive-UI score >=95;
3. v0.1.8 reliability/background/end score >=95 with zero critical failures;
4. self-contained x64 publish;
5. responsive UI bitmap audit;
6. Browser deterministic self-test, including rail-only mutation and endpoint policy;
7. RC6 QUICK regression;
8. final ZIP clean extraction and repeated Browser/UI self-tests.

Those gates make the build eligible for a controlled real-machine test; they are not a claim that every live web page is already solved.

## Install / update

Replace the portable package, reload the unpacked Browser Agent extension, and start `LongCapture.exe`. If the extension ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.

Browser sessions remain under `BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`; Diagnostics under `BrowserAgentCaptures\Diagnostics\`; local calibration under `BrowserAgentCaptures\Calibration\`.
