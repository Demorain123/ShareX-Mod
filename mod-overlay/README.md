# ShareX-Mod overlay

This directory contains ShareX-Mod code and patch assets that are intentionally kept separate from upstream ShareX sources.

## Goals

- Keep upstream ShareX source changes minimal and reviewable.
- Make the mod layer removable and replayable after upstream updates.
- Fail loudly when an upstream change breaks a hook instead of silently carrying an invalid merge.
- Keep Robust Scrolling Capture diagnostics and fallback logic outside upstream implementation files wherever practical.

## Layout

- `scrolling-capture/` — Robust Scrolling Capture implementation and notes.
- `patches/` — small, explicit upstream hook patches when a zero-touch hook is not possible.
- `config/` — defaults owned by ShareX-Mod.
- `VERSION` — overlay version, independent from upstream ShareX version.

The first implementation target is Robust Scrolling Capture v0.1: do not abort an entire scrolling session just because one combine attempt fails; preserve raw frames and diagnostics for recovery.
