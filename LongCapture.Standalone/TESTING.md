# LongCapture Browser Agent v0.1.5 — Real Test Guide

Version scope: fixed capture-speed presets across the main GUI modes; Browser Assisted adaptive speed, precision-based repair, inset-sticky suppression, hard F8 stop, diagnostics completeness, and regression safety.

## Step 0 — update and QUICK

1. Replace the portable package.
2. Reload the unpacked LongCapture Browser Agent extension in Chromium/Helium.
3. If the extension ID changed, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.
4. Start `LongCapture.exe` normally.
5. Double-click `1-RUN-AUTOMATED-TESTS.cmd` and continue only after `AUTOMATED ACCEPTANCE: PASS`.

The release CI publishes win-x64, runs Browser Agent deterministic tests including adaptive downshift/recovery, runs RC6 QUICK, packages the final ZIP, extracts it into a new folder, and reruns Browser Agent self-test.

## Test 1 — Capture speed exists in every mode

The top GUI must show **Capture speed** while switching between Normal Long Capture, Browser Assisted Capture, Smart Web, Teach and Run Recipe.

For non-Browser modes, switch through the fixed presets and confirm the existing underlying controls change:

- Very Low → 500 ms start / 1200 ms settle / scroll amount 1
- Low → 400 / 850 / 2
- Medium → 300 / 550 / 3
- High → 150 / 350 / 4
- Very High → 0 / 220 / 5

For Browser Assisted Capture, fixed presets use browser-specific values:

- Very Low → 500 ms start / 1800 ms settle / 45% overlap
- Low → 350 / 1350 / 40%
- Medium → 200 / 950 / 34%
- High → 100 / 700 / 27%
- Very High → 0 / 520 / 20%

PASS: the selector remains visible after mode changes; underlying values update and stay visible/editable. Adaptive choices appear only in Browser Assisted Capture, where their runtime controller is actually wired.

## Test 2 — adaptive high speed + manual F8 stop

Use Browser Assisted Capture and attach the same Linux.do tab.

Settings:

- Capture speed: `Adaptive · High speed`
- Repair precision: `Medium`
- Optional gentle lazy-content pre-scan: OFF
- Debug: optional

Procedure:

1. Press F8.
2. Drag the same content-column region used for prior Linux.do comparisons.
3. Let it run for roughly 20–40 frames.
4. Normal areas should run near the high-speed target; if loading/layout evidence becomes risky, LongCapture may temporarily slow down.
5. Press F8 once to stop.

PASS:

- no v0.1.3-style pre-capture jump-to-bottom;
- F8 stops browser movement promptly;
- Partial/manual-stop result is saved if usable frames exist;
- `session.json` contains `AdaptiveRiskScore`, `AdaptiveRiskReasons`, `AdaptiveSpeedBefore/After`, `EffectiveStableWindowMs`, `EffectiveOverlapRatio`;
- exported Diagnostics contains `[BA_ADAPT]` and the current-run LongCapture log.

Failure evidence: send one latest Browser Agent Diagnostics ZIP plus the final PNG if one was produced.

## Test 3 — sticky circular avatar

Use the Linux.do region that visibly contains the left-side circular user avatars.

1. Use `Adaptive · Balanced`, precision `Medium`.
2. F8 and select a region wide enough to include the avatar column and body text.
3. Let it run for at least ~15 frames, then F8 stop.

PASS:

- an avatar that becomes pinned below the header is not stamped repeatedly as a stationary screen-space object through the final mosaic;
- normal avatars that are still moving with their posts are not globally deleted;
- body text remains continuous around avatar transitions.

Failure evidence: Diagnostics ZIP + final PNG; identify roughly which avatar/post looks duplicated if obvious.

## Test 4 — natural full-page adaptive completion and repair

Only run after Tests 1–3 pass.

Settings:

- Capture speed: `Adaptive · High speed` for speed-oriented torture testing, or `Adaptive · Balanced` for the default recommendation;
- Repair precision: `High`;
- pre-scan: OFF.

Procedure:

1. F8 and select the normal content region.
2. Do not press any key after capture begins.
3. Let Browser Agent continue to confirmed page end.
4. Near suspicious loading/layout sections it may downshift; after three clean frames it recovers one gear at a time toward the selected target.
5. At natural page end, marked suspicious sections may cause controlled local re-capture/review before final stitch.

PASS:

- Complete is claimed only when page end is confirmed and adaptive review leaves no unresolved marked section;
- unresolved review becomes Partial / `quality-review-unresolved`;
- diagnostics records `[BA_REPAIR]` attempts and session repair counters when candidates exist;
- no large missing/duplicated document block;
- later-page quality does not progressively degrade because of slow loading.

## Test 5 — Normal Long Capture regression

Switch back to **Normal Long Capture**, choose any fixed Capture speed preset, and perform a short Start/Stop capture.

PASS: speed preset changes the existing Normal controls; F7 target selection, F8 region selection/start/stop, output save, existing RC6 overlap/fixed handling and diagnostics still work. Normal must not depend on the Browser extension.

## Important scope note

The five **fixed** Capture speed presets are wired across the GUI modes by changing each mode's existing capture controls. The **adaptive risk controller and Repair precision are Browser Assisted only in v0.1.5**, because that path currently has the DOM/layout/loading evidence required for safe dynamic decisions. Other modes are not falsely labelled adaptive yet.

## If any test fails

Stop after the first clear failure. Do not run parameter sweeps. Export the latest Browser Agent Diagnostics ZIP and send it with the final PNG when the failure is visual. The ZIP should include current-run logs and `[USER_ACTION]`, `[BA_TIMELINE]`, `[BA_REQ]`, `[BA_AGENT]`, `[BA_ADAPT]`, and `[BA_REPAIR]` evidence.
