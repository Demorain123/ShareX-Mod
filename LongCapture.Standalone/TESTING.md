# LongCapture Standalone v0.1.3 RC2 — Automated-first real-world test guide

## Step 0 — 必须先双击自动化测试

解压到一个全新的目录后，**先不要直接开始网页/应用真实测试**。

1. 双击 `1-RUN-AUTOMATED-TESTS.cmd`。
2. 脚本会调用这个 portable 包自己的 `LongCapture.exe --automation-test`，而不是依赖源码或 .NET SDK。
3. 等待最终结果：`AUTOMATED ACCEPTANCE: PASS` 或 `FAIL`。
4. 如果是 `FAIL`：停止，不要继续真实测试。保留最新的 `AutomationReports\<时间>\`、LongCapture log，以及需要时的 `Export diagnostics` ZIP。
5. 只有 `PASS_AUTOMATED` 才进入下面 Test 1–6。交互模式通过后会自动打开本文件。

自动化阶段会真实覆盖：portable 文件/配置完整性、LongCapture shell、Avalonia overlay、F8 风格 Start/Stop 捕获链、HWND target、LongCapture 自身 UI capture exclusion、target resize/minimize/close 恢复、100/125/150/200% 布局压力、anchor/repeated-pattern、sticky/fixed、lazy-load、Recipe/Integrity/Router/Pagination 回归、CaptureSession + Export diagnostics ZIP round-trip，以及同一最终程序连续 10 次真实 capture smoke 和内存增长门槛。

报告中 `MANUAL_REQUIRED` **不是失败**；它表示无法用 deterministic fixture 诚实替代的真实环境项，例如真实网站最终像素、Smart Web live session、Teach/Run Recipe live interaction、多显示器/GPU/动画/无限 feed，以及尚未完成的日常 Chrome 已登录 DOM/CDP Smart Web 复用。

This guide is shipped with the portable build only after the automated release-candidate gates pass. The tests below are for real Windows/browser differences that deterministic automation cannot faithfully reproduce.

## What changed in v0.1.3 RC2

- Added a package-resident double-click automated acceptance gate and machine-readable per-run reports.
- CI executes the same `1-RUN-AUTOMATED-TESTS.cmd --ci` path that the user double-clicks, including again after extracting the final ZIP.
- Independent `LongCapture.exe` remains the only user entry point; `ShareX.exe` is not required.
- Capture target can be locked to an existing visible top-level window by title/HWND, with the ShareX region/window picker retained as fallback.
- LongCapture windows are excluded from capture where Windows supports `WDA_EXCLUDEFROMCAPTURE`, and capture start uses a quiet period so LongCapture UI/notifications do not contaminate the first frame.
- Generic visual scrolling combines immediate sticky/fixed cleanup with deferred mosaic-tail repair for right-bottom/fixed controls whose underlying pixels become visible only after the next scroll.
- Lazy-load settle watches low-resolution visual stability and blank lower regions before accepting the next frame.
- Persistent launch logs, per-capture evidence, quality summaries and `Export diagnostics` are available for failures.

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

If it fails, keep the output PNG, a screenshot of the status/quality area, the newest log, the newest `AutomationReports` folder, and an `Export diagnostics` ZIP.

## Test 2 — Slow/lazy-loading page

1. Use a page with lazy images, skeleton cards or content that appears after scrolling.
2. Run Normal Long Capture over at least 8–10 scroll steps.
3. Watch for blank/skeleton areas that later fill in on the live page.

Expected: scrolling waits for suspicious/unstable newly exposed content; loaded content is captured instead of repeated blank placeholders; unresolved conditions produce evidence/warnings instead of a false clean PASS.

## Test 3 — Fixed header/footer/right-bottom controls

1. Choose a page with at least one sticky header and one floating right/bottom button or toolbar.
2. Capture enough content for the control to remain fixed through at least 5 scroll steps.
3. Inspect every seam.

Expected: the same fixed control is not stamped repeatedly; pixels temporarily covered by fixed UI are recovered when later frames expose them; cleanup introduces no missing horizontal bands or duplicated text.

## Test 4 — Target lifecycle and recovery

1. Start with a title-locked target.
2. Resize it and start capture.
3. Minimize it and attempt capture.
4. Restore it and try again.
5. Close it, refresh targets and select another window.

Expected: resized geometry refreshes; minimized/closed targets fail clearly or fall back safely; the next capture works without restarting LongCapture.

## Test 5 — Smart Web / Capture Browser

1. Select `Smart Web Capture`.
2. Click `Open Capture Browser`, navigate/sign in if needed and wait for readiness.
3. Refresh targets and select the Capture Browser window.
4. Start capture.

Expected: browser readiness is explicit; browser-enhanced fixed/sticky, semantic evidence and quality pipeline remain available; browser enhancement does not replace Normal Long Capture as the core screenshot path.

### Existing daily Chrome session

Reusing a normal existing Chrome window for **Normal Long Capture** is supported through HWND targeting. Reusing that daily Chrome window's authenticated DOM/CDP state for Smart Web is **not declared complete in RC2** and remains a later provider item rather than a hidden extension dependency.

## Test 6 — Teach Capture / Run Recipe

1. Open the Capture Browser and select `Teach Capture`.
2. Record a small workflow including scrolling and at least one semantic action/checkpoint.
3. Switch to `Run Recipe` and open `Review / approve`.
4. Approve the exact recipe version and run it.
5. Modify the recipe and confirm old approval no longer authorizes the changed version.

Expected: required safety checkpoints cannot be silently disabled; replay fails closed when approval/version evidence is invalid; the screenshot result still uses the normal quality/evidence pipeline.

## Regression checklist

- Step 0 one-click automation is PASS before manual testing.
- F8 starts and stops globally.
- `LongCapture.exe` launches without `ShareX.exe`.
- No clipped important text/buttons at normal Windows scaling.
- `Open output folder`, `Export diagnostics` and Quality are visible and usable.
- A failed/minimized/closed target does not poison the next capture.
- Multiple sequential captures do not show obvious unbounded memory growth.
- Very long output still uses the existing segmented/oversized-image path when selected by the engine.
- Image Appendix continues to use original browser resources when available; it must not upscale a screenshot crop and call it the original image.

## Report format

For a failing case, report: latest `AutomationReports` folder, page/app name, selected mode, Start/Stop method, approximate scroll count, Windows scaling, target resize/minimize state, output filename, newest log and exported diagnostic ZIP. Review logs before public sharing because window titles and local paths may appear in diagnostics.
