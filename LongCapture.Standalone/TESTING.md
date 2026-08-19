# LongCapture Browser Agent v0.1.6 — Calibrated Adaptive Real Test Guide

Version scope: global fixed speed presets; Browser Assisted benchmark/local calibration, live adaptive parameters, persistent bounded learning, precision repair, inset-sticky suppression, hard F8 stop and diagnostics completeness.

## Step 0 — update and automated gate

1. Replace the portable package.
2. Reload the unpacked LongCapture Browser Agent extension in Chromium/Helium.
3. If the extension ID changed, run `BrowserAgent\INSTALL-BROWSER-AGENT.cmd` again.
4. Start `LongCapture.exe` normally.
5. Double-click `1-RUN-AUTOMATED-TESTS.cmd` and continue only after `AUTOMATED ACCEPTANCE: PASS`.

The release pipeline itself already requires PRE-BUILD QUALITY SCORE >=95 with zero critical failures before publish, then Browser deterministic tests + RC6 QUICK, final ZIP packaging, clean extraction and a second Browser self-test.

## Test 1 — GUI and fixed presets

Switch among Normal Long Capture, Browser Assisted Capture, Smart Web, Teach and Run Recipe. **Capture speed** must remain visible.

Non-Browser fixed presets must update their existing visible controls: Very Low = 500/1200/1, Low = 400/850/2, Medium = 300/550/3, High = 150/350/4, Very High = 0/220/5.

Browser fixed reference presets must show: Very Low = 500/1800/45%, Low = 350/1350/40%, Medium = 200/950/34%, High = 100/700/27%, Very High = 0/520/20%.

PASS: no clipped/overlapping controls at the user's real Windows scaling; underlying values stay visible/editable; adaptive choices only appear in Browser Assisted.

## Test 2 — local Browser benchmark

Use the same Chromium/Helium profile and representative Linux.do page that will be captured.

1. Select **Browser Assisted Capture**.
2. Attach the active tab with the extension icon / Ctrl+Shift+L.
3. Leave **Use local calibration** checked.
4. Press **Browser benchmark** once and do not interact with the page while it runs.

PASS:

- benchmark completes without moving to another page or producing a final screenshot;
- the completion dialog reports local confidence/sample information;
- `BrowserAgentCaptures\Calibration\browser-agent-calibration-v016.json` exists;
- the GUI hint changes to show learned/local confidence;
- no rapid capture loop violates Chrome's two-calls-per-second limit.

If this fails, send the latest Diagnostics/log information and the calibration JSON if it exists. Do not repeatedly benchmark as a workaround.

## Test 3 — live adaptive parameters + manual F8 stop

Settings:

- Capture speed: `Adaptive · High speed`;
- Repair precision: `Medium`;
- Use local calibration: ON;
- Live params: ON;
- Optional gentle lazy-content pre-scan: OFF.

Procedure:

1. F8 and select the usual Linux.do content region.
2. Let it run for roughly 20–40 frames.
3. Watch the **Adaptive live** monitor while normal content and slower/lazy sections pass.
4. Press F8 once to stop.

PASS:

- monitor shows target/current gear, effective Start/Settle/MaxWait/Overlap, measured settle/activity/capture time, risk reasons and calibration confidence;
- the monitor does not steal focus and does not appear in the captured pixels;
- normal sections tend back toward the selected high-speed target;
- risky loading/layout sections may downshift and later recover;
- F8 stops browser movement promptly;
- usable frames save a Partial/manual-stop result;
- `session.json` contains effective and calibration fields;
- Diagnostics includes `[BA_CALIB]`, `[BA_LIVE]`, `[BA_ADAPT]` and current-run logs.

## Test 4 — sticky circular avatar regression

Use `Adaptive · Balanced`, precision `Medium`, calibration ON, and select a region wide enough to include Linux.do's left avatar column and body text. Run at least about 15 frames, then F8 stop.

PASS: an avatar that becomes pinned below the header is not stamped repeatedly through the final mosaic; normal avatars still moving with their posts are not globally deleted; body text remains continuous around those transitions.

## Test 5 — natural full-page calibrated adaptive completion

Only continue after Tests 1–4 pass.

Use `Adaptive · High speed` (or `Adaptive · Balanced` for a more conservative run), **Repair precision High**, calibration ON, pre-scan OFF, live params optional. Let it reach confirmed page end without pressing F8.

PASS:

- the controller can slow at suspicious lazy/layout sections and recover later;
- marked sections may be selectively re-captured at page end;
- Complete is claimed only when confirmed page end plus adaptive review are clean;
- unresolved marked areas become `Partial / quality-review-unresolved`;
- no large missing/duplicated document block;
- later-page quality does not progressively degrade;
- the local calibration profile's frame sample count/confidence increases after successful real frames.

## Test 6 — calibration OFF comparison

Without deleting the profile, uncheck **Use local calibration** and run a short comparison on the same page/region with the same Adaptive target.

PASS: capture still works using the reference gear behavior; the GUI clearly indicates local calibration is off. This is the control run that lets us compare learned timing against the fixed reference without destroying accumulated evidence.

## Test 7 — Normal Long Capture regression

Return to **Normal Long Capture**, choose a fixed speed preset, and perform a short F7/F8 Start/Stop run.

PASS: fixed preset changes the existing Normal controls; target/region selection, output save, RC6 overlap/fixed handling and diagnostics still work; Normal capture remains independent from the Browser extension and Browser calibration profile.

## Evidence after the first failure

Stop after the first clear failure instead of sweeping parameters. Send one newest Browser Agent Diagnostics ZIP plus the final PNG when the failure is visual. If the problem concerns learning/benchmarking, also send `BrowserAgentCaptures\Calibration\browser-agent-calibration-v016.json`. The useful markers are `[USER_ACTION]`, `[BA_TIMELINE]`, `[BA_REQ]`, `[BA_AGENT]`, `[BA_ADAPT]`, `[BA_REPAIR]`, `[BA_CALIB]` and `[BA_LIVE]`.

## Scope honesty

The five fixed speed presets remain available across the GUI modes. Dynamic page-aware adaptive learning and Repair precision are Browser Assisted only because that path has DOM/loading/layout and capture timing evidence. A later model-based tuner should be evaluated offline against accumulated real evidence before it is ever allowed to advise a bounded safe action set in live capture.
