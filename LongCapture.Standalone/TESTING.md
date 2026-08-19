# LongCapture Browser Agent v0.1.8 — Real Test Guide

Version scope: v0.1.7 responsive UI + v0.1.6 calibrated adaptive behavior + v0.1.8 rail-aware sticky/seam detection, Browser background-window capture, and requested capture endpoints.

## Step 0 — update

1. Extract the new portable package to a fresh directory.
2. Reload the unpacked LongCapture Browser Agent extension in Chromium/Helium.
3. If the extension ID changed, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` once.
4. Start `LongCapture.exe`.
5. `1-RUN-AUTOMATED-TESTS.cmd` should still end with `AUTOMATED ACCEPTANCE: PASS`.

Do not change many parameters after the first failure. Export one Diagnostics ZIP and preserve the final PNG.

## Test 1 — short rail/sticky regression first

Use the same Linux.do topic and include the full left avatar rail in the selected F8 region.

Recommended settings:

- Capture mode: Browser Assisted Capture
- Capture speed: Adaptive · Balanced
- Repair precision: Medium
- Use local calibration: ON
- Live params: ON
- pre-scan: OFF
- Capture end: Frames ≥ 35

Expected:

- normal post avatars that move with their posts remain present;
- the avatar that becomes pinned near the header is not repeatedly stamped through the mosaic;
- body text remains continuous;
- automatic end is reported as **Requested range complete**, not “true page end”.

Diagnostics should contain left/center/right overlap fields. If rail contamination is detected, `[BA_EDGE]` and repair/recovery evidence should explain it.

STOP HERE and send Diagnostics + PNG if this test is visually wrong.

## Test 2 — background-window Browser Assisted capture

This test checks “browser behind another desktop app”, not minimized/background-tab capture.

1. Attach the target Linux.do tab.
2. Set Capture end to `Frames ≥ 40`.
3. Press F8 and select the normal region.
4. After about 5 frames, put Notepad / Explorer / another desktop application in front of the browser.
5. Leave the browser window open/non-minimized and leave the attached Linux.do tab selected inside that browser window.
6. Do not bring the browser back until the capture stops automatically.

PASS:

- scrolling/capture continues while another application is foreground;
- output contains the browser page, not the foreground application;
- `session.json` has `BackgroundWindowCapture=true` on background frames and `BackgroundWindowFrames > 0`;
- the endpoint stops automatically at the requested frame count.

Negative checks:

- switching to another tab in the same browser window must stop/fail clearly rather than capture the wrong tab;
- minimizing the browser must stop/fail clearly on this backend rather than silently create bad frames.

## Test 3 — DOM progress endpoint

Use a page whose visible fixed counter looks like `current / total`, such as the Linux.do topic.

1. Choose `Capture end: DOM progress ≥`.
2. Set a nearby target such as 225 or 230 so the test is not excessively long.
3. Start from the normal Browser Assisted flow with F8 region selection.
4. Do not press F8 after capture starts.

PASS:

- the program continues until the captured frame reports current progress >= the requested value;
- it performs the normal adaptive review for marked segments;
- final state is `completed-requested-range` / **Requested range complete**;
- it does not claim that the whole 320-post page was completed;
- `[BA_END]` records the target match.

If the site has no usable DOM progress counter, this mode should not guess from pixels. Use Frames / Elapsed / F8 for v0.1.8; OCR fallback is future experimental scope.

## Test 4 — F8 hard stop regression

1. Capture end: Auto · page end / F8.
2. Run for about 20 frames.
3. Press F8 exactly once.

PASS:

- browser movement stops promptly;
- no page-moving post-review begins after F8;
- existing frames stitch as `partial-manual-stop`;
- Diagnostics shows the user stop timestamp.

## Test 5 — longer adaptive run

Only do this if Tests 1–4 pass.

Recommended:

- Adaptive · Balanced first; High speed only after Balanced is clean;
- Repair precision High;
- local calibration ON;
- pre-scan OFF;
- Capture end `DOM progress ≥ 260` or `Frames ≥ 250` for a controlled long run.

PASS:

- center text and side rails remain stable in later frames;
- page loading can downshift adaptive speed and later recover;
- no progressively increasing duplicate/missing seams;
- requested end triggers selective post-review before output;
- unresolved quality is surfaced instead of falsely reporting success.

## Test 6 — true full-page challenge

Only after the controlled long run is clean:

- Capture end: Auto · page end / F8
- Adaptive · Balanced or High speed
- Repair precision High

Let it run to the real page end without F8.

PASS: true page end is confirmed, adaptive review finishes clean, and final status is full `completed` rather than requested-range or Partial.

## Test 7 — Normal Long Capture regression

Return to Normal Long Capture and perform a short F7/F8 capture.

PASS: Normal capture remains independent from Browser Agent, Browser calibration and requested Browser endpoints.

## Experimental roadmap — not claimed by v0.1.8

The following are deliberately not presented as completed in this build:

- OCR-only endpoint recognition when a site draws progress as pixels/canvas;
- “click Next N times” / “stop on page N” semantic page-loop automation;
- true minimized browser or inactive-tab screenshot capture;
- arbitrary Windows app background/minimized capture.

The next-page family belongs to the Recipe/Page Loop model: semantic Next locator + maximum pages + per-page checkpoint + fail-closed behavior. OCR should be a small-region fallback only when DOM/semantic evidence is unavailable, not a full-frame per-scroll hot-path dependency.

## Evidence to send after first failure

For Browser Assisted, send:

1. newest `BrowserAgentDiagnostics-*.zip`;
2. final PNG if the problem is visual;
3. screenshot of the GUI if the problem is settings/layout.

Useful markers: `[USER_ACTION]`, `[BA_TIMELINE]`, `[BA_REQ]`, `[BA_AGENT]`, `[BA_ADAPT]`, `[BA_REPAIR]`, `[BA_CALIB]`, `[BA_LIVE]`, `[BA_EDGE]`, `[BA_END]`.
