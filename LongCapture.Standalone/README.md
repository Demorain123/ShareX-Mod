# LongCapture Standalone v0.1.3-rc1

`LongCapture.exe` is the independent long-screenshot application. It reuses ShareX's capture libraries and the ShareX-Mod overlay, but it does **not** launch or require `ShareX.exe` as the user entry point.

## v0.1.3 RC1 reliability gate

RC1 is not produced merely because the project compiles. The dedicated workflow must pass the packaged `LongCapture.exe` self-test, deterministic scrolling fixtures, target-loss/recovery checks, 100/125/150/200% layout pressure, ten repeated real capture smoke runs with memory limits, and then repeat the critical self-test/stress checks from the extracted final ZIP.

The portable RC1 also contains `TESTING.md` and a machine-readable `RC-READINESS.json`. The readiness manifest is generated only after the pre-package mandatory gates pass and records the conservative automated-scope score and known real-world limitations.

The current generic visual path adds deferred mosaic-tail repair for fixed right/bottom controls: when the next scroll reveals the document pixels that were previously hidden behind a fixed control, those pixels are written back into the previous viewport already stored in the mosaic. This complements immediate sticky/header cleanup instead of deleting document bands.

## v0.1.3 title-locked targets

v0.1.3 starts the planned outer-layer move away from an interruptive ShareX-only target-selection flow without rewriting the proven scrolling engine.

- The standalone window exposes a **Capture target** list built from visible top-level Windows windows.
- Targets are shown by window title plus process/PID, and the selected HWND is refreshed immediately before capture so moving/resizing the target does not leave a stale rectangle.
- The capture rectangle prefers the target client area and falls back to the full window rectangle when necessary.
- A small standalone bridge injects the locked HWND/rectangle into the existing ShareX scrolling manager, preserving the current scrolling/stitching engine instead of forking it.
- **ShareX region/window picker (fallback)** remains available. If a locked target disappears or the compatibility bridge cannot bind it, LongCapture records the reason and falls back to the existing picker rather than failing silently.
- The packaged self-test creates a real Win32 target window, resolves it through the same target service, binds it through the same bridge, starts a real scrolling capture and stops it through the same path used by F8.

This is intentionally an outer-shell change. The ShareX scrolling core remains the engine while LongCapture takes ownership of target discovery and diagnostics.

## v0.1.3 diagnostic logging

Persistent diagnostics are a first-class part of the standalone app so capture failures can be diagnosed from evidence instead of screenshots and guesses.

- A new per-launch UTF-8 log is written under `%LOCALAPPDATA%\LongCapture\Logs`.
- Click **Open logs folder** beside the Capture target selector to open that directory directly.
- Logs cover startup/runtime details, F8 registration, mode/readiness changes, Capture Browser launch, recipe selection/review, visible-target refresh, selected HWND/title/process, capture options, locked-target binding/fallback, start/stop lifecycle, result dimensions/path, quality summary and caught exceptions.
- WinForms UI-thread exceptions, AppDomain unhandled exceptions and unobserved Task exceptions are also recorded on a best-effort basis.
- A log rolls to another segment after roughly 8 MB, and the app keeps the newest 20 `LongCapture-*.log` files so diagnostics do not grow without bound.
- Logging is deliberately dependency-free and lives in the standalone layer; a logging failure is swallowed so it cannot become a second capture failure.

**Privacy note:** diagnostic logs can contain local file paths and the titles/process names of windows you selected. Review a log before posting it publicly.

## v0.1.2 capture bootstrap fix retained

v0.1.1 exposed a standalone-host integration bug during the first real F8 capture test: region selection could complete, but the ShareX scrolling engine then created its Avalonia `ScrollingCaptureRegionWindow` from a WinForms-only host that had never initialized Avalonia. Calling `Window.Show()` therefore failed with `InvalidOperationException: The window has not been initialized.`

v0.1.2 fixed the host boundary instead of disabling the region overlay:

- LongCapture explicitly initializes ShareX's Avalonia stack through `AvaloniaBootstrapper.EnsureInitialized()` before the WinForms main loop starts.
- This uses the bootstrapper's existing `SetupWithoutStarting()` path, which is intended for a legacy host that owns its own application lifetime/message loop.
- The standalone project declares the ShareX Avalonia project as an explicit dependency rather than relying only on a transitive reference from `ShareX.ScreenCaptureLib`.
- `ShowRegion = true` remains enabled, so the scrolling-region outline is preserved rather than hidden as a workaround.
- The packaged `--self-test` constructs the same `ScrollingCaptureRegionWindow`, calls `Show()`, verifies it became visible, and closes it.

## v0.1.1 UI hardening retained

- Per-monitor high-DPI scaling is explicitly enabled for the WinForms executable.
- Fixed-height settings rows are converted to content-sized rows at runtime, so controls are not cut off by DPI/font scaling.
- Readiness, output-path, quality and status text wrap instead of silently extending beyond their containers.
- The settings grid can scroll when the monitor/window is too small to show every row at once.
- Recipe action buttons are allowed to wrap as a group and keep a minimum click height.
- RC1 additionally derives button minimum size from the actual scaled font so newly added buttons do not reintroduce clipping.

## Modes

- **Normal Long Capture** — ordinary window/app/browser scrolling capture with manual F8 Start/Stop, adaptive settle, robust stitching and quality guard.
- **Smart Web Capture** — uses the dedicated Capture Browser and Chrome/CDP-enhanced background capture, semantic map, quality repair and image appendix pipeline.
- **Teach Capture** — records a semantic Capture Recipe from the dedicated Capture Browser for later replay.
- **Run Recipe** — replays a reviewed/approved Capture Recipe. The standalone GUI includes a fail-closed review screen; required checkpoints cannot be disabled.

## Quick start

1. Extract the portable ZIP to a new folder and run `LongCapture.exe`.
2. Click **Refresh targets** if needed, then choose the scrolling app/window by title in **Capture target**. Keep **ShareX region/window picker (fallback)** when you deliberately want free region selection.
3. Click **Start long capture** or press F8. With a title target selected, LongCapture locks that window directly; with fallback selected, the existing ShareX selector opens.
4. Press F8 again whenever you want to stop.
5. For Smart Web / Teach / Run Recipe, click **Open Capture Browser**, navigate/sign in in that dedicated browser profile, wait for **Ready**, refresh the target list, then select that browser window before starting capture.
6. Results are saved under `Pictures\LongCapture`. The GUI surfaces the latest quality status produced by the ShareX-Mod quality pipeline.
7. If anything behaves unexpectedly, click **Open logs folder** and keep the newest log together with the failing screenshot/output when reporting the problem; `Export diagnostics` packages the latest capture evidence.
8. Read the bundled `TESTING.md` before the first real-world RC test.

## Capture Recipe safety

Run Recipe remains blocked until the exact recipe version has a valid approval. Open **Review / approve**, inspect the recorded steps, disable optional actions if desired, then approve. If the recipe changes afterward, the old approval no longer authorizes replay.

## Build verification

The dedicated `Build LongCapture Standalone` workflow applies the current ShareX-Mod overlay chain, verifies the real capture integration, title-target bridge, persistent logging, capture exclusion and Avalonia legacy-host bootstrap, publishes self-contained `win-x64`, checks that the package contains `LongCapture.exe`/`LongCapture.dll` and required libraries but not `ShareX.exe`, runs the packaged overlay/locked-target/engine/responsive-GUI self-test, runs semantic and scrolling reliability fixtures, runs the repeated capture/recovery/memory gate, builds the RC1 ZIP, extracts it, and repeats the packaged self-test plus stress gate before upload.
