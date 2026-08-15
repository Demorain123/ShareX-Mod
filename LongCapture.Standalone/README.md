# LongCapture Standalone v0.1.2-dev

`LongCapture.exe` is the independent long-screenshot application. It reuses ShareX's capture libraries and the ShareX-Mod overlay, but it does **not** launch or require `ShareX.exe` as the user entry point.

## v0.1.2 capture bootstrap fix

v0.1.1 exposed a standalone-host integration bug during the first real F8 capture test: region selection could complete, but the ShareX scrolling engine then created its Avalonia `ScrollingCaptureRegionWindow` from a WinForms-only host that had never initialized Avalonia. Calling `Window.Show()` therefore failed with `InvalidOperationException: The window has not been initialized.`

v0.1.2 fixes the host boundary instead of disabling the region overlay:

- LongCapture explicitly initializes ShareX's Avalonia stack through `AvaloniaBootstrapper.EnsureInitialized()` before the WinForms main loop starts.
- This uses the bootstrapper's existing `SetupWithoutStarting()` path, which is specifically intended for a legacy host that owns its own application lifetime/message loop.
- The standalone project now declares the ShareX Avalonia project as an explicit dependency rather than relying only on a transitive reference from `ShareX.ScreenCaptureLib`.
- `ShowRegion = true` remains enabled, so the scrolling-region outline is preserved rather than hidden as a workaround.
- The packaged `--self-test` now constructs the same `ScrollingCaptureRegionWindow`, calls `Show()`, verifies it became visible, and closes it. This directly executes the window-bootstrap path that v0.1.1 failed to test.
- The same self-test is run once on the published directory and again after extracting the final portable ZIP, so a package missing the Avalonia runtime/host dependency is rejected.

## v0.1.1 UI hardening retained

- Per-monitor high-DPI scaling is explicitly enabled for the WinForms executable.
- Fixed-height settings rows are converted to content-sized rows at runtime, so controls are not cut off by DPI/font scaling.
- Readiness, output-path, quality and status text wrap instead of silently extending beyond their containers.
- The settings grid can scroll when the monitor/window is too small to show every row at once.
- Recipe action buttons are allowed to wrap as a group and keep a minimum click height.
- Responsive layout checks run at 840×720, 980×900 and 1100×900 and reject clipped readiness text, important labels or button captions.

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

The dedicated `Build LongCapture Standalone` workflow applies the current ShareX-Mod overlay chain, verifies the real capture integration and Avalonia legacy-host bootstrap, publishes self-contained `win-x64`, checks that the package contains `LongCapture.exe`, `ShareX.ScreenCaptureLib.dll` and `ShareX.Avalonia.dll` but not `ShareX.exe`, runs the packaged F8-overlay/engine/responsive-GUI self-test, runs the ShareX-Mod semantic regression suite, builds the ZIP, extracts it, and repeats the packaged self-test before upload.
