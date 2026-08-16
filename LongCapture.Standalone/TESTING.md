# LongCapture Standalone v0.1.9 RC4 — Real Test 1 only

RC4 exists specifically for the new RC3 Linux.do failure. The RC3 run established three good ~750 px transitions, then a no-direct transition had a strongly validated ~752 px temporal prior while a larger ~1068 px full-range repeated-content alias scored slightly better because it had much less overlap. RC3 incorrectly let that unsafe larger candidate veto the valid prior and stopped immediately.

RC4 makes that decision asymmetric: an uncorroborated larger full-range candidate is neither accepted nor allowed to veto a strongly validated prior. A materially different same-size/shorter candidate can still veto the prior, preserving the earlier ambiguous-short fail-closed regression. RC4 also adds a regression for bottom-right fixed controls revealed over quiet/blank document pixels, which caused RC3 provisional-tail repair to skip later repairs and leave an extra `Back / counter` copy.

## Step 0 — QUICK

Double-click `1-RUN-AUTOMATED-TESTS.cmd` and continue only after `AUTOMATED ACCEPTANCE: PASS`.

The release CI already runs QUICK, DEEP 10x capture/memory stress, all RC2/RC3/RC4 geometry regressions, fixed-control long-run regressions, packaging, and a second QUICK after extracting the final release ZIP. Do not run `--deep` locally unless specifically requested.

## Step 1 — one live Linux.do Test 1

Keep the same variables for direct comparison:

- Capture mode: `Normal Long Capture`
- Start delay: `500 ms`
- Scroll settle: `450 ms`
- Scroll amount: `5`
- Scroll method: `MouseWheel`
- `Whole window (skip region selection)`: unchecked
- Debug: optional; replay evidence is recorded automatically

Procedure:

1. Open the same Linux.do page/layout when practical.
2. Press **F7** to lock the browser target.
3. Press **F8** and select a region that intentionally includes the right-side fixed `Back / counter` controls.
4. Let it continue for at least roughly 12 viewport transitions; do not stop early merely to obtain a PASS.
5. The program must not terminate by itself while the page still scrolls normally.
6. Then press **F8** yourself to stop.

PASS requires all of the following:

- no premature automatic stop;
- no large missing/duplicated document blocks or far repeated-content jump;
- stable ~normal movement is not displaced by a false short or large alias;
- right-side fixed controls do not accumulate once per viewport; a bounded first/final occurrence is acceptable;
- reported quality agrees with what is visibly present.

## Step 2 — replay

Double-click `2-REPLAY-LAST-CAPTURE.cmd`.

Expected: it discovers the newest raw-frame session safely, waits for the actual replay process, reports its real exit code, and either creates a valid `LongCapture-Replay-v019-*.png` + JSON or fails explicitly without legacy mosaic fallback.

## If Test 1 fails

Stop there. Do not run Test 2 or parameter sweeps. Send the final PNG plus the automatically exported `LongCapture-Diagnostics-*.zip`; include replay PNG/JSON only if replay produced them. That single package contains the raw frames and geometry evidence for the next offline iteration.

## Not required yet

Lazy-load Test 2, Smart Web/Capture Browser, daily-Chrome DOM/CDP reuse, Teach/Run Recipe, multi-monitor/GPU stress, and local `--deep` remain outside this handoff until core Linux.do Test 1 is visually acceptable.
