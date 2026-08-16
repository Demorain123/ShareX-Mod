# LongCapture Standalone v0.1.9 RC3 — Real Test 1 only

This RC exists specifically to retest the real Linux.do failure captured in v0.1.9-rc2. Do not expand the manual test scope yet. RC3 first has to prove that the core raster long-capture path no longer converts a stable ~750 px movement into a false short alias such as 253/373 px and then stops automatically.

RC3 also keeps the v0.1.8/v0.1.9 trust split: accepted document body is not destructively rewritten just to erase fixed controls. Provisional-tail repair remains allowed, and the legacy mosaic fallback remains forbidden.

## Step 0 — QUICK

Double-click:

`1-RUN-AUTOMATED-TESTS.cmd`

Continue only after:

`AUTOMATED ACCEPTANCE: PASS`

The release CI has already run QUICK, DEEP 10x capture/memory stress, the RC3 real-evidence geometry regression, packaging, and a second QUICK after extracting the final release ZIP. You do not need to run `--deep` locally before this real test.

## Step 1 — one live Linux.do Test 1

Use the same Linux.do page/layout that exposed the rc2 failure when practical. Keep the variables fixed so the result is comparable:

- Capture mode: `Normal Long Capture`
- Start delay: `500 ms`
- Scroll settle: `450 ms`
- Scroll amount: `5`
- Scroll method: `MouseWheel`
- `Whole window (skip region selection)`: unchecked
- Debug: not required; RC3 records replay evidence automatically

Procedure:

1. Bring the target Chrome/Helium Linux.do window to the foreground.
2. Press **F7** to lock the foreground target.
3. Press **F8**, then drag a capture region that intentionally includes the right-side fixed `Back / 1/312` controls. Do not crop them out; they are part of this regression test.
4. Let the capture run for at least roughly 12 viewport transitions. Do **not** stop it early just to make the test pass.
5. Confirm that it does not automatically terminate in the middle while the page can still scroll.
6. After enough transitions, press **F8** yourself to stop.

### PASS criteria

All of the following must be true:

- The capture does not automatically stop mid-page while normal scrolling is still possible.
- No obvious large document block is missing, duplicated, or jumped to a distant repeated-content alias.
- A stable normal movement is not replaced by an obviously short/incorrect geometry step that causes a later failure.
- Right-side fixed controls do not accumulate approximately once per viewport through the whole output. A safe first/final occurrence may remain; linear accumulation is a failure.
- Quality status is consistent with the visible result. A visibly broken long image reported as high-confidence PASS is itself a failure.

## Step 2 — replay the just-created capture

After the live Test 1, double-click:

`2-REPLAY-LAST-CAPTURE.cmd`

RC3 changed the launcher so it waits for the actual LongCapture replay process and reads that process's `ExitCode`; it no longer relies on a blank/null `$LASTEXITCODE`, which caused rc2 to report `Replay failed with exit code .` even when the underlying replay had completed.

Expected:

- The newest replayable capture session is discovered without PowerShell wildcard errors, including when unrelated sibling paths contain `[` or `]`.
- A successful replay prints success and creates `LongCapture-Replay-v019-*.png` plus its JSON in the session directory.
- Replay must remain fail-closed: if geometry cannot be justified, it must fail explicitly rather than falling back to the legacy mosaic matcher.

## If Test 1 fails

Stop there. Do not run Test 2, Smart Web, Capture Browser, Teach/Run Recipe, multi-monitor stress, or another parameter sweep.

Provide only the evidence from this one run:

- final long PNG;
- automatically exported `LongCapture-Diagnostics-*.zip` (or click `Export diagnostics` if the image is visibly wrong but no ZIP was created);
- replay PNG/JSON if replay produced them;
- optionally one short note saying whether the first visible failure was `auto-stop`, `missing/duplicate body`, `fixed controls accumulating`, or another obvious artifact.

The Diagnostics ZIP contains the raw viewport frames and geometry evidence needed for offline iteration, so one failed live capture should be enough to continue development without repeatedly asking for new screenshots.

## Not required in RC3 handoff

The following are intentionally **not** part of the current required manual test: lazy-load Test 2, Smart Web/dedicated Capture Browser, existing daily-Chrome DOM/CDP reuse, Teach/Run Recipe, multi-monitor/GPU stress, and local `--deep`. Those can be tested only after this core Linux.do Test 1 is visually acceptable.
