# LongCapture Standalone v0.1.3 RC — Real-world test guide

This guide is shipped with the portable build only after the automated release-candidate gates pass. The automated suite is responsible for basic development QA; the tests below are for real Windows/browser differences that a GitHub runner cannot faithfully reproduce.

## What changed in v0.1.3

- Independent `LongCapture.exe` remains the only user entry point; `ShareX.exe` is not required.
- Capture target can be locked to an existing visible top-level window by title/HWND, with the ShareX region/window picker retained as fallback.
- LongCapture windows are excluded from capture where Windows supports `WDA_EXCLUDEFROMCAPTURE`, and capture start uses a quiet period so LongCapture UI/notifications do not contaminate the first frame.
- Generic visual scrolling now combines immediate sticky/fixed cleanup with deferred mosaic-tail repair for right-bottom/fixed controls whose underlying pixels become visible only after the next scroll.
- Lazy-load settle watches low-resolution visual stability and blank lower regions before accepting the next frame.
- Persistent launch logs, per-capture evidence, quality summaries and `Export diagnostics` are available for failures.
- The RC gate repeats the actual published capture path, recovery cases, layout pressure and memory checks before packaging and repeats critical checks after ZIP extraction.

## Test 1 — Normal Long Capture, existing Chrome window, manual Start/Stop

Purpose: verify the core non-extension path and partial-range workflow.

1. Open an ordinary Chrome window and navigate to a long page with a sticky header or floating control.
2. Open `LongCapture.exe`.
3. Click `Refresh targets` and select that existing Chrome window by title.
4. Select `Normal Long Capture`.
5. Scroll Chrome to the exact point where you want the capture to begin.
6. Press F8 (or click Start).
7. Let several screens be captured, then press F8 again before the page ends.

Expected:
- LongCapture does not launch another browser.
- Capture begins from the chosen current page position unless `Scroll selected target to the top` is enabled.
- The result contains the requested partial range and is saved under `Pictures\LongCapture`.
- LongCapture's own window, dialogs and status UI do not appear in the image.
- Sticky/fixed controls should not appear once per scroll step; meaningful fixed UI may remain once where appropriate.
- Quality is `PASS` or an explicit warning/failure is shown instead of silently claiming a bad result succeeded.

If it fails, keep:
- the output PNG;
- a screenshot of the LongCapture status/quality area;
- the newest log from `Open logs folder`;
- an `Export diagnostics` ZIP.

## Test 2 — Slow/lazy-loading page

Purpose: verify that LongCapture does not outrun content loading.

1. Use a page with lazy images, skeleton cards or content that appears after scrolling.
2. Run Normal Long Capture over at least 8–10 scroll steps.
3. Watch for blank/skeleton areas that later fill in on the live page.

Expected:
- scrolling pauses longer only when the newly exposed area is suspicious/unstable;
- loaded content should be captured rather than a repeated blank placeholder;
- a timeout or unresolved region should produce a quality warning/failure and evidence, not a false clean PASS.

## Test 3 — Fixed header/footer/right-bottom controls

Purpose: compare the v0.1.3 visual compositor against the v0.1.2 repeated-control defect.

1. Choose a page with at least one sticky header and one floating right/bottom button or toolbar.
2. Capture enough content for the control to have remained fixed through at least 5 scroll steps.
3. Inspect the final long image at every seam.

Expected:
- the same fixed control is not stamped repeatedly down the document;
- document pixels that were temporarily covered by a fixed control are recovered when later frames expose them;
- no obvious missing horizontal bands or duplicated text are introduced by the cleanup.

## Test 4 — Target lifecycle and recovery

Purpose: verify safe failure and next-run recovery.

1. Start with a title-locked target.
2. Before a new capture, resize the target and start capture; LongCapture should use the refreshed client rectangle.
3. Minimize the target and attempt capture.
4. Restore it and try again.
5. Close the target, then refresh targets and select another window for a fresh capture.

Expected:
- resize does not leave a stale rectangle;
- minimized/closed targets fail clearly or fall back safely rather than crashing;
- after a failed attempt, a new capture can start normally without restarting LongCapture.

## Test 5 — Smart Web / Capture Browser

Purpose: regress the existing browser-enhanced backend without changing the v0.1.3 architecture.

1. Select `Smart Web Capture`.
2. Click `Open Capture Browser`, navigate/sign in if needed and wait for readiness.
3. Refresh targets and select the Capture Browser window.
4. Start capture.

Expected:
- browser readiness is explicit;
- browser-enhanced fixed/sticky handling, semantic evidence and quality pipeline remain available;
- browser enhancement does not replace Normal Long Capture as the core screenshot path.

### Existing daily Chrome session

Chrome 144+ now has an official permission-based auto-connect mechanism for DevTools agents, but native LongCapture integration with that permission bridge is not declared complete in v0.1.3. Reusing a normal existing Chrome window for **Normal Long Capture** is supported through HWND targeting; reusing its authenticated DOM/CDP state for Smart Web remains a later provider feasibility item, not a hidden extension dependency.

## Test 6 — Teach Capture / Run Recipe

1. Open the Capture Browser and select `Teach Capture`.
2. Record a small workflow that includes scrolling and at least one semantic action/checkpoint.
3. Switch to `Run Recipe` and open `Review / approve`.
4. Approve the exact recipe version and run it.
5. Modify the recipe and confirm old approval no longer authorizes the changed version.

Expected:
- required safety checkpoints cannot be silently disabled;
- replay is fail-closed when approval/version evidence is invalid;
- the screenshot result still goes through the normal quality/evidence pipeline.

## Regression checklist

- F8 starts and stops globally.
- `LongCapture.exe` launches without `ShareX.exe`.
- No clipped important text/buttons at normal Windows scaling.
- `Open output folder`, `Export diagnostics` and Quality are all visible and usable.
- A failed/minimized/closed target does not poison the next capture.
- Multiple sequential captures do not show obvious unbounded memory growth.
- Very long output still uses the existing segmented/oversized-image path rather than relying on one ever-growing in-memory bitmap when the overlay engine selects segmented output.
- Image Appendix continues to use original browser resources when available; it must not upscale a small screenshot crop and call it the original image.

## Report format

For a failing case, report: page/app name, selected LongCapture mode, Start/Stop method, approximate number of scrolls, Windows scaling, whether the target was resized/minimized, output filename, newest log and exported diagnostic ZIP. If the page is private, review logs before sharing because window titles and local paths may appear in diagnostics.
