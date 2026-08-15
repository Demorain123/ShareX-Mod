# LongCapture Standalone v0.1.7 RC1 — Offline-first fixed/stitch validation

This build exists to fix the real v0.1.6 Linux.do failure, not to add more manual test work. The important change is **anchor continuity**: when the strict multi-anchor matcher rejects a transition, LongCapture no longer falls through to the old ShareX mosaic compositor. A validated temporal/vertical fallback must keep that transition on the same raw-frame delayed compositor; otherwise the capture stops as partial instead of silently manufacturing a corrupted long image.

## Step 0 — QUICK

Double-click `1-RUN-AUTOMATED-TESTS.cmd`.

Continue only after `AUTOMATED ACCEPTANCE: PASS`. `--deep` remains optional and is not required.

QUICK now deliberately forces several direct-anchor failures in a changing fixed-control fixture. Those transitions must still use the delayed compositor, must keep the fixed control to at most one tail instance, and must also replay correctly from PNG-only diagnostic raw frames.

## Step 1 — first replay the real capture you already made

Before doing another live Linux.do screenshot, double-click:

`2-REPLAY-LAST-CAPTURE.cmd`

The script searches the current install and sibling LongCapture install folders for the newest `ShareX-Mod\CaptureSessions\*\raw-frames-v016` session. This is specifically intended to reuse the raw BMP frames from the previous v0.1.6 Debug capture if that old folder is still present.

If automatic search cannot find it, you can drag the old capture-session folder onto `2-REPLAY-LAST-CAPTURE.cmd` or pass its path as the first argument.

Expected:
- replay must not use the old legacy mosaic matcher;
- if a transition cannot be safely resolved, replay fails explicitly instead of creating a misleading result;
- a successful replay creates `LongCapture-Replay-v017-*.png` and a matching `.json` beside the old raw session;
- compare this replay PNG with the old broken v0.1.6 result, especially the repeated right-side `Back / 1/312` fixed controls and the large seam/duplicate blocks.

**If the replay is still visibly bad, stop there and send the replay PNG/JSON. Do not repeat the website capture.**

## Step 2 — only if offline replay is clearly improved, repeat one live Test 1

1. Open the same Linux.do page/window used for the previous failure.
2. Open LongCapture and select `Normal Long Capture`.
3. Keep `Whole window (skip region selection)` **unchecked**.
4. Enable `Debug: GUI + raw replay evidence`.
5. Normally keep `Debug: include internal helper/selector/Avalonia windows` **unchecked**. The LongCapture main GUI is Debug-visible; internal transient capture windows remain excluded. Enable the internal-window option only when specifically diagnosing those windows.
6. Bring the browser to the foreground and press **F7** to lock it.
7. Press **F8**, then drag a region that intentionally includes the right-side fixed `Back / counter` controls so the defect is actually exercised.
8. Let it scroll roughly 8–12 viewport transitions, then press **F8** again.

Expected:
- no repeated fixed control stamped once per transition;
- no large duplicated/missing document blocks;
- direct-anchor failures, if any, are resolved by `validated-prior`, `near-prior-fallback`, or the initial bootstrap fallback and still use the delayed compositor;
- once a temporal prior exists, LongCapture does not select a far-away repeated-content alias;
- if no safe transition can be validated, the run stops as partial/low confidence rather than falling into legacy stitching;
- Debug keeps the intended main GUI capturable while internal helper/selector/Avalonia windows remain excluded unless you explicitly enable the second Debug checkbox.

## Diagnostics are different in v0.1.7

The v0.1.6 diagnostics exporter accidentally skipped every raw `frame-XXXX.bmp` as an unsupported type. v0.1.7 fixes this: raw BMP frames are converted to lossless PNG during diagnostic export and included under `engine-evidence\raw-frames-v016` so an uploaded diagnostics ZIP is actually replayable offline.

A failure package should therefore contain:
- output long PNG;
- `LongCapture-Diagnostics-*.zip`;
- `LongCapture-DebugState_*.json/.png` when Debug was enabled;
- inside diagnostics: `engine-evidence/raw-frames-v016/frame-*.png`, `anchors.jsonl`, and `resolutions-v017.jsonl` when available.

## Do not run Test 2 yet

Do **not** increase Scroll amount or run the lazy-load stress Test 2 until Step 1 replay and the single live Test 1 above are both visually acceptable. Smart Web, Capture Browser, Teach/Run Recipe and 10× memory stress are not required for this core handoff.
