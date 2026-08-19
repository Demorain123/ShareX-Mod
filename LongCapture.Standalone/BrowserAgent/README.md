# LongCapture Browser Agent v0.1.8 — Integrity Repair / Background Window / Requested End

Browser Assisted remains an optional backend inside the normal `LongCapture.exe` product. Normal Long Capture / RC6 stays independent. The browser component supplies capture/data/quality evidence; it does not replace LongCapture's Start/Stop, stitch, Quality Guard or output pipeline.

## Why v0.1.8 exists

A real 203-frame Linux.do / Discourse diagnostic capture exposed two weaknesses that the previous adaptive score did not handle strongly enough:

1. center/body text could look aligned while a sticky avatar in the far-left rail changed screen-space position;
2. the first pass already marked multiple lazy/settle-risk frames, but a manual F8 stop could bypass post-capture repair entirely.

v0.1.8 therefore separates **fast acquisition** from **integrity review**. A first-pass frame is allowed to be provisional. Suspicious evidence is recorded in the Capture Map, then the post-pass Quality Guard revisits only the marked logical positions before the final mosaic is trusted.

This is the internal equivalent of a “transition / warning marker”. It is deliberately metadata, not a visible blank warning page inserted into the final image, because changing the document mosaic merely to show an error would corrupt coordinates and content. Debug/Diagnostics exposes the marked segments explicitly.

## Suspect-frame / Capture Map evidence

A frame can become `suspect` from several independent signals, including:

- unexpected actual-vs-expected scroll delta;
- robust scroll-delta outlier evidence using recent median/MAD history;
- failed/unverified visual overlap;
- left/right rail contamination;
- semantic DOM-anchor document-position drift;
- capture state changing while the screenshot was taken;
- stability timeout / pending images;
- layout-shift evidence;
- lazy/end-boundary document growth.

The Browser provider samples visible structural DOM anchors and hashes the identifying text/attributes **inside the page**. `session.json` receives only the hash plus document/viewport Y and bounds; raw anchor text is not copied into the repair ledger.

Important per-frame fields include `QualityState`, `IntegrityRiskScore`, `IntegrityRiskReasons`, actual/expected scroll delta, robust Z score, semantic anchors, anchor drift, logical `ScrollYCss`, visual overlap evidence and repair status. `[BA_MARK]` records when a first-pass frame becomes provisional/suspect.

## Post-pass repair and Repair precision

F8 now has two separate meanings in the pipeline:

1. **stop the forward scrolling pass immediately**;
2. **do not bypass Quality Guard** — freeze the acquired range, reset only the repair channel, revisit marked positions, re-capture them more conservatively, verify them and then stitch/save.

Therefore the browser may move again *after* F8 while targeted repair is running. That movement is no longer forward capture; it is local repair at recorded logical Y positions.

Repair precision now has four levels:

- **Low** — up to 1 attempt per selected suspect segment; default repair time budget 30 seconds;
- **Medium** — up to 2 attempts per selected suspect segment; default 2 minutes;
- **High** — up to 4 attempts per selected suspect segment, broader suspect budget and conservative re-capture timing; default 5 minutes;
- **Perfect** — **no attempt-count limit** and no suspect-count limit. It keeps retrying unresolved marked sections until they verify or the separate repair-time limit is reached. Default time budget is 15 minutes.

The repair-time selector is independent from precision: `Time · Auto`, `1 min`, `3 min`, `5 min`, `15 min`, or `Unlimited`. `Perfect + Unlimited` therefore has no retry-count or time limit; use it only when correctness matters more than completion time.

A successfully repaired frame becomes `repaired`. A segment that cannot be verified before its budget ends becomes `unresolved`; LongCapture must not silently label that range verified.

## Rail-aware seam / sticky verification

The PNG overlap verifier samples roughly the 2%–98% horizontal range and retains separate left / center / right metrics:

- band MAE;
- strong-difference ratio;
- edge-contamination flags;
- expected/resolved visual delta.

A rail that is materially worse than the center is no longer invisible in a median score. Edge evidence is persisted in `session.json` and logged with `[BA_EDGE]`.

For Discourse-family pages (including Linux.do), the browser helper also recognizes the semantic sticky state `.topic-post.sticky-avatar .topic-avatar` and hides that pinned avatar only for the screenshot transaction. Normal document avatars are not globally removed.

## Background-window Browser Assisted capture

Enable:

`Allow target browser window behind other apps (tab stays active; not minimized)`

After F8 and region selection, you may put another desktop application in front of the browser. The attached tab must remain the active tab **inside that browser window**.

Current `captureVisibleTab()` backend limitations are explicit:

- the browser window may be behind another desktop app;
- do not switch to another tab in the same browser window;
- do not minimize the browser window;
- minimized capture fails closed with a clear error instead of pretending to work.

`session.json` records target-window focus/state and background-window frames. True minimized/background-tab capture belongs to a future CDP / Existing Chrome provider, not a hidden promise in this build.

## Capture end

Browser Assisted exposes a **Capture end** row:

- `Auto · page end / F8` — confirmed real page end or manual F8;
- `DOM progress ≥ N` — stop after the DOM-visible progress counter reaches N (for example `230 / 320`);
- `Frames ≥ N` — deterministic frame budget;
- `Elapsed minutes ≥ N` — time-bounded forward pass.

A requested endpoint is reported as **Requested range complete**, not as false full-page completion. Manual F8 remains a partial-range result even after all suspect frames in that requested range were repaired.

The DOM counter is deliberately preferred over OCR when available. OCR/heavier visual recognition is **not bundled into the v0.1.8 capture hot path**. It remains an experimental post-capture/small-region fallback candidate for sites whose progress or repair evidence exists only as pixels.

## Adaptive quality / calibration retained

v0.1.6 behavior remains:

- five bounded fixed Browser gears;
- Adaptive Robust / Balanced / High speed;
- Browser benchmark and persistent local calibration;
- Live params monitor;
- adaptive slow-down/recovery on risky regions;
- lazy/layout/capture/overlap evidence;
- hard `captureVisibleTab()` rate guard;
- hard F8 stop of the forward pass.

The intended high-quality workflow is now deliberately asymmetric: a fast adaptive first pass may accumulate suspect markers; repair then spends time only where evidence says it is needed.

## Diagnostics

Important Browser markers include:

- `[USER_ACTION]` — settings / buttons;
- `[BA_TIMELINE]` — lifecycle milestones;
- `[BA_REQ]` — Native Messaging request timing;
- `[BA_AGENT]` — browser-side scroll/cancel behavior;
- `[BA_ADAPT]` — adaptive risk / gear;
- `[BA_MARK]` — suspect/provisional first-pass frame;
- `[BA_REPAIR]` — local repair attempts/results/time-budget events;
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

The v0.1.8 pipeline is not allowed to publish until all of the following pass:

1. v0.1.6 adaptive-core score >=95;
2. v0.1.7 responsive-UI score >=95;
3. v0.1.8 integrity/repair/background/end score >=95 with zero critical failures;
4. self-contained x64 publish;
5. responsive UI bitmap audit;
6. Browser deterministic self-test, including integrity/Perfect repair policy;
7. RC6 QUICK regression;
8. final ZIP clean extraction and repeated Browser/UI self-tests.

Those gates make the build eligible for a controlled real-machine test; they are not a claim that every live web page is solved.

## Install / update

Extract the portable package to a fresh directory, reload the unpacked Browser Agent extension, and start `LongCapture.exe`. If the extension ID changes, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.

Browser sessions remain under `BrowserAgentCaptures\BrowserAgent-YYYYMMDD-HHMMSS\`; Diagnostics under `BrowserAgentCaptures\Diagnostics\`; local calibration under `BrowserAgentCaptures\Calibration\`.
