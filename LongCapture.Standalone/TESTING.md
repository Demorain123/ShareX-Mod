# LongCapture Browser Agent v0.1.5 — Real Test Guide

Version scope: Browser Assisted Capture adaptive speed, precision-based repair, inset-sticky suppression, hard F8 stop, diagnostics completeness, and regression safety. Normal Long Capture / RC6 remains a regression path and is not replaced by Browser Agent.

## Step 0 — update and QUICK

1. Replace the portable package.
2. Reload the unpacked LongCapture Browser Agent extension in Chromium/Helium.
3. If the extension ID changed, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.
4. Start `LongCapture.exe` normally.
5. Double-click `1-RUN-AUTOMATED-TESTS.cmd` and continue only after `AUTOMATED ACCEPTANCE: PASS`.

The release CI already publishes win-x64, runs the Browser Agent deterministic test including adaptive downshift/recovery, runs RC6 QUICK, packages the final ZIP, extracts it into a new folder, and reruns the Browser Agent self-test.

## Test 1 — speed presets are real

Use **Browser Assisted Capture** and attach the same Linux.do tab.

Switch through the fixed Capture speed presets and confirm the visible underlying values change:

- Very Low → 500 ms start / 1800 ms settle / 45% overlap
- Low → 350 / 1350 / 40%
- Medium → 200 / 950 / 34%
- High → 100 / 700 / 27%
- Very High → 0 / 520 / 20%

PASS: the values change immediately and remain editable/visible; choosing a fixed preset does not enable adaptive repair behavior.

## Test 2 — adaptive high speed + manual F8 stop

Settings:

- Capture speed: `Adaptive · High speed`
- Repair precision: `Medium`
- Optional gentle lazy-content pre-scan: OFF
- Debug: optional

Procedure:

1. Press F8.
2. Drag the same content-column region used for prior Linux.do comparisons.
3. Let it run for roughly 20–40 frames.
4. Watch that normal areas move quickly; do not intervene if LongCapture temporarily slows when a page section is still loading.
5. Press F8 once to stop.

PASS:

- no v0.1.3-style pre-capture jump-to-bottom;
- F8 stops browser movement promptly;
- Partial/manual-stop result is saved if usable frames exist;
- `session.json` contains `AdaptiveRiskScore`, `AdaptiveRiskReasons`, `AdaptiveSpeedBefore/After`, `EffectiveStableWindowMs`, `EffectiveOverlapRatio`;
- exported Diagnostics contains `[BA_ADAPT]` and the current-run LongCapture log.

Failure evidence: send exactly one latest Browser Agent Diagnostics ZIP plus the final PNG if one was produced.

## Test 3 — sticky circular avatar

Use the Linux.do region that visibly contains the left-side circular user avatars.

1. Use `Adaptive · Balanced`, precision `Medium`.
2. F8 and select a region wide enough to include the avatar column and body text.
3. Let it run for at least ~15 frames, then F8 stop.

PASS:

- an avatar that becomes pinned below the header is not stamped repeatedly as a stationary screen-space object through the final document mosaic;
- normal avatars that are still moving with their posts are not all globally deleted;
- the body text remains continuous around avatar transitions.

Failure evidence: Diagnostics ZIP + final PNG; identify roughly which avatar/post looks duplicated if obvious.

## Test 4 — natural full-page adaptive completion and repair

Only run this after Tests 1–3 pass.

Settings:

- Capture speed: `Adaptive · High speed` for speed-oriented torture testing, or `Adaptive · Balanced` for the default recommendation;
- Repair precision: `High` for this test;
- pre-scan: OFF.

Procedure:

1. F8 and select the normal content region.
2. Do not press any key after the capture begins.
3. Let Browser Agent continue to the confirmed page end.
4. Near suspicious loading/layout sections it may downshift. After three clean frames it should recover one gear at a time toward its target.
5. At natural page end, marked suspicious sections may cause controlled local re-capture/review before final stitch.

PASS:

- final status is Complete only when page end is confirmed and the adaptive quality review leaves no unresolved marked section;
- unresolved review becomes Partial / `quality-review-unresolved`, never a silent Complete;
- diagnostics records `[BA_REPAIR]` attempts and session repair counters when candidates exist;
- no large missing/duplicated document block;
- later-page quality does not progressively degrade because of slow loading.

## Test 5 — Normal Long Capture regression

Switch back to **Normal Long Capture** and perform a short Start/Stop capture with the same settings you previously used successfully.

PASS: F7 target selection, F8 region selection/start/stop, output save, existing RC6 overlap/fixed handling, and diagnostics still work. Browser Assisted changes must not make Normal capture depend on the extension.

## Important scope note

v0.1.5 wires the new fixed/adaptive strategy and repair precision into **Browser Assisted Capture**. Normal/Smart Web/Teach/Run Recipe keep their existing capture controls and engines in this version. A shared-looking dropdown without real mode-specific runtime behavior would violate the release gate, so those modes are not falsely labelled adaptive yet.

## If any test fails

Stop after the first clear failure. Do not run parameter sweeps. Export the latest Browser Agent Diagnostics ZIP and send it with the final PNG when the failure is visual. The ZIP is expected to include the current-run log and the `[USER_ACTION]`, `[BA_TIMELINE]`, `[BA_REQ]`, `[BA_AGENT]`, `[BA_ADAPT]`, and `[BA_REPAIR]` evidence needed for offline diagnosis.
