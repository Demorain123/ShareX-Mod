# LongCapture Browser Agent v0.1.8 — Integrity Repair Real Test Guide

Version scope: v0.1.7 responsive UI + v0.1.6 calibrated adaptive behavior + v0.1.8 suspect-frame Capture Map, post-pass local repair, rail/sticky integrity evidence, Browser background-window capture, and requested capture endpoints.

## Step 0 — update

1. Extract the new portable package to a **fresh directory**.
2. Reload the unpacked LongCapture Browser Agent extension in Chromium/Helium.
3. If the extension ID changed, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` once.
4. Start `LongCapture.exe`.
5. `1-RUN-AUTOMATED-TESTS.cmd` should still end with `AUTOMATED ACCEPTANCE: PASS`.

Do not change many parameters after the first failure. Export one Diagnostics ZIP and preserve the final PNG.

## What changed in this build

The fast scrolling pass is now allowed to mark uncertain frames instead of pretending every captured frame is final-quality. Suspect frames are recorded with logical/document position evidence and `[BA_MARK]`. After the forward pass ends — including a manual F8 stop — Quality Guard revisits the marked positions, re-captures them with conservative settings and records `[BA_REPAIR]` results before stitching/saving.

`Repair precision` now supports:

- Low — up to 1 attempt/selected suspect segment; Auto time budget 30s;
- Medium — up to 2 attempts; Auto 2 min;
- High — up to 4 attempts and a broader suspect budget; Auto 5 min;
- Perfect — **unlimited repair attempts and unlimited suspect count**; Auto 15 min.

The separate repair-time selector can override Auto with `1 min`, `3 min`, `5 min`, `15 min`, or `Unlimited`. `Perfect + Unlimited` can therefore wait indefinitely if a live page never reaches a verifiable state; use it deliberately.

## Test 1 — F8 stop must enter repair, not bypass it

Recommended settings:

- Capture mode: Browser Assisted Capture
- Capture speed: Adaptive · High speed
- Repair precision: Perfect
- Repair time: 3 min for this short test
- Use local calibration: ON
- Live params: ON
- pre-scan: OFF
- Capture end: Auto · page end / F8

Steps:

1. Attach the same Linux.do topic.
2. Press F8 and select the usual region, including the left avatar rail.
3. Let the first pass run for about 25–40 frames.
4. Press F8 once.

PASS:

- forward scrolling stops promptly;
- status changes to Quality Guard / repair review instead of immediately saving;
- the browser **may move again** to recorded logical Y positions while repair is running — this is targeted repair, not resumed forward capture;
- Diagnostics contains `[BA_MARK]` for suspect frames when risk was detected and `[BA_REPAIR]` for actual repair attempts;
- `session.json` records `RepairReviewPasses > 0` and repair start/completion timestamps;
- if all marked frames verify, the manual-stop result stays Partial because the requested document range was intentionally cut short, but quality state should not contain unresolved segments;
- if repair cannot verify a segment before the selected time limit, result is explicitly unresolved/Partial rather than silently “verified”.

STOP HERE and send Diagnostics + final PNG if the browser resumes ordinary forward scrolling after F8, repair does not start despite marked frames, or the saved image is visibly wrong.

## Test 2 — suspect-frame / long-run integrity regression

Use the same Linux.do topic and keep the full left avatar rail in the F8 region.

Recommended:

- Adaptive · High speed
- Repair precision: High first, then Perfect after the short test is clean
- Repair time: 5 min or 15 min
- local calibration ON
- pre-scan OFF
- Capture end: `DOM progress ≥ 240` or a nearby controlled target

Expected:

- a fast first pass is allowed to accumulate suspect markers;
- suspicious scroll-delta outliers, edge contamination, lazy growth, unstable capture state, anchor drift, pending images/layout shift, or failed overlap contribute to the integrity ledger;
- post-pass repair goes back only to marked logical positions instead of re-running the whole long page;
- normal moving avatars remain present;
- sticky/pinned rail elements are not stamped repeatedly;
- body text remains continuous after repair;
- requested range ends as **Requested range complete**, not a false whole-page claim.

Useful `session.json` evidence includes `QualityState`, `IntegrityRiskScore`, `IntegrityRiskReasons`, actual/expected scroll delta, robust Z, semantic-anchor count/drift, repair attempts/status and unresolved count.

## Test 3 — Perfect + Unlimited repair semantics

This is an optional stress test; do it only after Test 1 is correct.

1. Set `Repair precision = Perfect`.
2. Set repair time to `Unlimited`.
3. Use a controlled forward endpoint such as `Frames ≥ 50`.
4. Start capture and allow the automatic endpoint to stop the first pass.

PASS:

- there is no attempt-count cap such as `1/3` or `3/3` for Perfect; logs show `attempt=N/∞` when repeated repair is needed;
- already-clean/repaired segments do not consume pointless retries;
- unresolved sections keep being retried until they verify or the user manually terminates the program/capture workflow;
- final output is not labelled fully verified while unresolved repair evidence remains.

Because truly dynamic content can change forever, `Perfect + Unlimited` is intentionally capable of waiting indefinitely. For normal use, `Perfect + Time · Auto` (15 min) is the safer default.

## Test 4 — background-window Browser Assisted capture

This test checks “browser behind another desktop app”, not minimized/background-tab capture.

1. Attach the target Linux.do tab.
2. Set Capture end to `Frames ≥ 40`.
3. Press F8 and select the normal region.
4. After about 5 frames, put Notepad / Explorer / another desktop application in front of the browser.
5. Leave the browser window open/non-minimized and leave the attached Linux.do tab selected inside that browser window.
6. Do not bring the browser back until the forward endpoint and repair review finish.

PASS:

- scrolling/capture continues while another application is foreground;
- output contains the browser page, not the foreground application;
- `session.json` has background-window evidence and `BackgroundWindowFrames > 0`;
- the endpoint stops automatically, then Quality Guard can revisit marked positions.

Negative checks:

- switching to another tab in the same browser window must stop/fail clearly rather than capture the wrong tab;
- minimizing the browser must stop/fail clearly on this backend rather than silently create bad frames.

## Test 5 — DOM progress endpoint

Use a page whose visible fixed counter looks like `current / total`, such as the Linux.do topic.

1. Choose `Capture end: DOM progress ≥`.
2. Set a nearby target such as 225 or 230.
3. Start with F8 region selection.
4. Do not press F8 after capture starts.

PASS:

- capture continues until a captured frame reports current progress >= target;
- marked segments receive post-pass review/repair;
- final state is `completed-requested-range` / **Requested range complete** when quality review resolves;
- it does not claim the whole topic was completed;
- `[BA_END]` records the target match.

If the site has no usable DOM progress counter, this version does not guess from pixels. Use Frames / Elapsed / F8. OCR endpoint recognition remains experimental future scope.

## Test 6 — true full-page challenge

Only after Tests 1–5 are clean:

- Capture end: Auto · page end / F8
- Capture speed: Adaptive · High speed (Balanced if the site is especially unstable)
- Repair precision: Perfect
- Repair time: Auto (15 min) first
- local calibration ON
- pre-scan OFF

Let it run to the real page end without F8.

PASS:

- fast first pass reaches the real page end;
- Quality Guard then reviews marked sections;
- repaired segments are verified locally rather than forcing a complete re-run;
- no unresolved integrity segments remain;
- final status is full `completed` only after true page end + repair review.

## Test 7 — Normal Long Capture regression

Return to Normal Long Capture and perform a short F7/F8 capture.

PASS: Normal capture remains independent from Browser Agent, Browser calibration, Browser repair ledger and requested Browser endpoints.

## Experimental roadmap — not claimed by v0.1.8

The following are deliberately **not** presented as completed in this build:

- OCR fallback for repair verification or pixel/canvas-only endpoint recognition;
- “click Next N times” / “stop on page N” semantic page-loop automation;
- true minimized browser or inactive-tab screenshot capture;
- arbitrary Windows app background/minimized capture.

OCR should be a small-region/post-capture fallback only when DOM/semantic/visual evidence is unavailable, not a full-frame per-scroll hot-path dependency. Page-loop automation belongs to the Recipe model: semantic Next locator + max pages + per-page checkpoint + fail-closed behavior.

## Evidence to send after first failure

For Browser Assisted, send:

1. newest `BrowserAgentDiagnostics-*.zip`;
2. final PNG if the problem is visual;
3. screenshot of the GUI if the problem is settings/layout.

Useful markers: `[USER_ACTION]`, `[BA_TIMELINE]`, `[BA_REQ]`, `[BA_AGENT]`, `[BA_ADAPT]`, `[BA_MARK]`, `[BA_REPAIR]`, `[BA_CALIB]`, `[BA_LIVE]`, `[BA_EDGE]`, `[BA_END]`.
