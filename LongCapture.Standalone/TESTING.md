# LongCapture Standalone v0.1.4 RC1 — Core reliability real test

v0.1.4 deliberately narrows the manual handoff. The automated QUICK gate still runs first, but **your only required real tests for this build are Test 1 and Test 2 below**. Target lifecycle is automated; Smart Web / Capture Browser and Teach / Run Recipe are Advanced/optional and are not part of the v0.1.4 core pass.

## Step 0 — run QUICK first

Double-click `1-RUN-AUTOMATED-TESTS.cmd`.

Continue only when it ends with `AUTOMATED ACCEPTANCE: PASS`. `--deep` remains optional and is not required before the real test.

v0.1.4 QUICK additionally includes a multi-frame compositor fixture with a **changing fixed right-side counter/control**, not only a byte-identical fixed-element unit test.

## What changed for this test

- **F7 foreground target:** bring the window you want to capture to the front and press F7. The target list is retained as fallback and now draws application icons.
- **Locked window + region is the default:** F7/window selection identifies which HWND must be scrolled; pressing F8 then opens the ShareX region selector so you drag only the exact content area inside that window. `Whole window (skip region selection)` is opt-in.
- **Anchor-first compositor:** once multi-anchor matching establishes the scroll delta, LongCapture appends exactly that newly exposed delta instead of allowing the legacy exact-row compositor to guess another match/crop position.
- **Debug mode:** `Debug: GUI capturable + settings snapshot` removes LongCapture's capture exclusion and writes `LongCapture-DebugState_*.png/.json` into the output folder. The JSON records both displayed GUI values and the actual engine options, including Scroll amount.
- **Failure evidence:** a standalone Quality `FAIL`, low confidence or unresolved engine result automatically exports a diagnostics ZIP next to the screenshot; the status line also shows the current log path.
- Smart Web / Teach / Run Recipe remain present as Advanced modes but are not required for this core test.

## Test 1 — Linux.do / ordinary long page, F7 + region + F8 Start/Stop

1. Open the same normal browser window/page that showed the repeated fixed-side controls in RC2.
2. Open LongCapture and leave `Normal Long Capture` selected.
3. Make sure `Whole window (skip region selection)` is **unchecked**.
4. Either click `Foreground target (F7)` and follow the prompt, or simply activate the browser and press **F7**.
5. Verify LongCapture reports that the intended browser window is locked.
6. Press **F8**. Drag the capture rectangle **inside that locked browser window**, around the content you actually want. Exclude browser tabs/address bar and, when practical, exclude unrelated static side chrome.
7. Let it capture several screens, then press **F8** again.

Expected:
- F7 identifies the browser without hunting through a long dropdown list.
- F8 still gives you a real region-selection step; selecting an HWND no longer forces whole-window capture.
- The region stays associated with the locked browser HWND while that HWND receives scrolling input.
- The result should not show the RC2 pattern where a changing right-side fixed counter/control is stamped once per frame.
- LongCapture UI is absent in normal mode.
- If Quality fails, a diagnostics ZIP should appear automatically in `Pictures\LongCapture`, and the UI should clearly say `QUALITY FAIL` rather than presenting the result as a clean success.

### Optional Debug check

Before repeating Test 1, enable `Debug: GUI capturable + settings snapshot`. Verify that LongCapture itself can now be captured by an ordinary screenshot tool and that the output folder contains `LongCapture-DebugState_*.png` and `.json`. Send those files with a failure report; the JSON lets us compare the GUI Scroll amount with the actual engine `ScrollAmount`.

## Test 2 — same page, faster scroll / lazy-load stress

Repeat Test 1 on the same page with the faster Scroll amount you want to stress (for example the value you previously intended as 12). Keep the capture region identical or very similar.

Expected:
- No large seam jumps, duplicated page blocks or repeated changing fixed controls.
- Lazy/skeleton content should not be accepted before it settles when it is visibly still loading.
- The value in a Debug-state JSON under `Displayed.ScrollAmount` must equal `EngineOptions.ScrollAmount`.
- If the engine cannot resolve the page reliably, it should return an explicit quality failure with diagnostics rather than a visually broken image presented as success.

## What you do NOT need to test in v0.1.4

- Target resize/minimize/close recovery — covered by QUICK automation.
- Test 3 as a separate fixed-element case — Test 1/2 already exercise the real failure layout.
- Smart Web / dedicated Capture Browser — Advanced/optional for this build.
- Teach Capture / Run Recipe — Advanced/optional until the later existing-window/HUD UX pass.
- 10× memory stress — `--deep` only if a stability problem appears.

## If Test 1 or Test 2 fails

Send the output PNG and the automatically generated diagnostics ZIP. If Debug mode was used, also send `LongCapture-DebugState_*.png/.json`. The full application log is under `%LOCALAPPDATA%\LongCapture\Logs`; the status line now surfaces that path as well.
