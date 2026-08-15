# ShareX-Mod overlay

This directory contains ShareX-Mod code and patch assets that are intentionally kept separate from upstream ShareX sources.

## Goals

- Keep upstream ShareX source changes minimal and reviewable.
- Make the mod layer removable and replayable after upstream updates.
- Fail loudly when an upstream change breaks a hook instead of silently carrying an invalid merge.
- Keep Robust Scrolling Capture diagnostics and fallback logic outside upstream implementation files wherever practical.

## Layout

- `src/` — ShareX-Mod implementation compiled into the upstream project through the overlay targets file.
- `patches/` — small, explicit upstream hook patches when a zero-touch hook is not possible.
- `config/` — defaults owned by ShareX-Mod.
- `build/` — MSBuild glue that links overlay source/config into ShareX without moving them into upstream directories.
- `VERSION` — overlay version, independent from upstream ShareX version.

## Robust Scrolling Capture

### v0.1

- Do not abort an entire scrolling session just because one combine attempt fails.
- Preserve raw frames and diagnostics for recovery.
- Add a tolerant vertical fallback matcher for dynamic pages.

### v0.2

Field testing on a lazy-loaded forum showed that ShareX could keep capturing frames after an embedded-content boundary while the stitched result stopped growing. The cause was the upstream historical `bestMatchCount/bestMatchIndex` guess being reused after exact matching had already become unreliable.

v0.2 therefore:

- keeps upstream exact matching as the first choice;
- disables the upstream stale historical best-guess branch only while Robust Scrolling is active;
- lets the overlay fallback matcher handle exact-match failures;
- raises the fallback mean-difference ceiling from 14 to 20 based on the captured failing frame;
- makes combine-failure tolerance unlimited by default when the user intends to stop the capture manually.

When ShareX-Mod is disabled, upstream ShareX scrolling behavior remains unchanged.
