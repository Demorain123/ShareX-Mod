# LongCapture Standalone v0.1.1-dev

`LongCapture.exe` is the independent long-screenshot application. It reuses ShareX's capture libraries and the ShareX-Mod overlay, but it does **not** launch or require `ShareX.exe` as the user entry point.

## v0.1.1 UI hardening

This patch release keeps the capture engine unchanged and focuses on release-quality Windows UI behavior:

- Per-monitor high-DPI scaling is explicitly enabled for the WinForms executable.
- Fixed-height settings rows are converted to content-sized rows at runtime, so controls are not cut off by DPI/font scaling.
- Readiness, output-path, quality and status text wrap instead of silently extending beyond their containers.
- The settings grid can scroll when the monitor/window is too small to show every row at once.
- Recipe action buttons are allowed to wrap as a group and keep a minimum touch/click height.
- The packaged `--self-test` now runs responsive-layout checks at 840×720, 960×800 and 1100×900 and fails the build if readiness text or button captions are clipped.

The layout hardening is intentionally isolated from capture/stitching logic so the patch is low-risk and easy to carry forward.

## Modes

- **Normal Long Capture** — ordinary window/app/browser scrolling capture with manual F8 Start/Stop, adaptive settle, robust stitching and quality guard.
- **Smart Web Capture** — uses the dedicated Capture Browser and Chrome/CDP-enhanced background capture, semantic map, quality repair and image appendix pipeline.
- **Teach Capture** — records a semantic Capture Recipe from the dedicated Capture Browser for later replay.
- **Run Recipe** — replays a reviewed/approved Capture Recipe. The standalone GUI includes a fail-closed review screen; required checkpoints cannot be disabled.

## Quick start

1. Extract the portable ZIP to a new folder.
2. Run `LongCapture.exe`.
3. For Normal mode, click **Start long capture** (or press F8), select the scrolling window/region, and press F8 again whenever you want to stop.
4. For Smart Web / Teach / Run Recipe, click **Open Capture Browser**, navigate/sign in in that dedicated browser profile, wait for **Ready**, then start capture and select that browser window/region.
5. Results are saved under `Pictures\LongCapture`. The GUI also surfaces the latest quality status produced by the ShareX-Mod quality pipeline.

## Capture Recipe safety

Run Recipe remains blocked until the exact recipe version has a valid approval. Open **Review / approve**, inspect the recorded steps, disable optional actions if desired, then approve. If the recipe changes afterward, the old approval no longer authorizes replay.

## Build verification

The dedicated `Build LongCapture Standalone` workflow applies the current ShareX-Mod overlay chain, publishes self-contained `win-x64`, verifies that the package contains `LongCapture.exe` and not `ShareX.exe`, runs the packaged engine + responsive-GUI self-test, runs the ShareX-Mod semantic regression suite, rebuilds the ZIP, extracts it, and repeats the packaged self-test before upload.
