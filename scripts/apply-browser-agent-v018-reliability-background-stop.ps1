[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$manifest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\manifest.json"
$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$selftest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"
$diagnostics = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentDiagnosticsExporter.cs"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.8] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.8 compatibility anchor missing: $Marker in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.8] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.8] applied: $Marker" -ForegroundColor Cyan
    }
}

# ---------------------------------------------------------------------------
# Main UI: add explicit requested-end controls while preserving F8 Start/Stop.
# ---------------------------------------------------------------------------
Replace-One $main `
    -Old '    private readonly BrowserAgentAdaptiveUiV015 browserAgentAdaptiveUi = new();' `
    -New @'
    private readonly BrowserAgentAdaptiveUiV015 browserAgentAdaptiveUi = new();
    private readonly BrowserAgentEndConditionUiV018 browserAgentEndUi = new();
'@ `
    -Marker 'BrowserAgentEndConditionUiV018 browserAgentEndUi'

Replace-One $main `
    -Old '            browserAgentAdaptiveUi.SetMode(true, mainBody, startDelay, scrollDelay, scrollAmount, autoScrollTop);' `
    -New @'
            browserAgentAdaptiveUi.SetMode(true, mainBody, startDelay, scrollDelay, scrollAmount, autoScrollTop);
            browserAgentEndUi.SetMode(true, mainBody);
'@ `
    -Marker 'browserAgentEndUi.SetMode(true, mainBody);'

Replace-One $main `
    -Old '        browserAgentAdaptiveUi.SetMode(false, mainBody, startDelay, scrollDelay, scrollAmount, autoScrollTop);' `
    -New @'
        browserAgentEndUi.SetMode(false, mainBody);
        browserAgentAdaptiveUi.SetMode(false, mainBody, startDelay, scrollDelay, scrollAmount, autoScrollTop);
'@ `
    -Marker 'browserAgentEndUi.SetMode(false, mainBody);'

Replace-One $main @'
            BrowserAgentCaptureOptions browserOptions = browserAgentAdaptiveUi.EnrichOptions(
                browserAgentUiAdapter.BuildOptions(
                    startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture));
            LongCaptureLog.Info(
                $"Browser Agent v0.1.5 options strategy={browserOptions.SpeedStrategy} precision={browserOptions.RepairPrecision} " +
                $"startDelay={browserOptions.StartDelayMs} settle={browserOptions.StableWindowMs} overlap={browserOptions.OverlapRatio:P0} " +
                $"preload={browserOptions.PreloadDynamicContent} regionSelect={browserOptions.RequireRegionSelection}");
'@ @'
            BrowserAgentCaptureOptions browserOptions = browserAgentEndUi.EnrichOptions(
                browserAgentAdaptiveUi.EnrichOptions(
                    browserAgentUiAdapter.BuildOptions(
                        startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture)));
            LongCaptureLog.Info(
                $"Browser Agent v0.1.8 options strategy={browserOptions.SpeedStrategy} precision={browserOptions.RepairPrecision} " +
                $"startDelay={browserOptions.StartDelayMs} settle={browserOptions.StableWindowMs} overlap={browserOptions.OverlapRatio:P0} " +
                $"preload={browserOptions.PreloadDynamicContent} regionSelect={browserOptions.RequireRegionSelection} " +
                $"stopMode={browserOptions.StopMode} stopValue={browserOptions.StopValue} backgroundWindow={browserOptions.AllowBackgroundWindow}");
'@ 'Browser Agent v0.1.8 options strategy='

# v0.1.7 progressive disclosure hid row 3 outside Recipe mode. Browser v0.1.8
# reuses that row for the Capture end policy.
Replace-One $ui `
    -Old '        SetRowVisible(body, 3, runRecipe);' `
    -New '        SetRowVisible(body, 3, browserAgent || runRecipe);' `
    -Marker 'SetRowVisible(body, 3, browserAgent || runRecipe);'

# ---------------------------------------------------------------------------
# Capture session: requested endpoint, background-window evidence and rail quality.
# ---------------------------------------------------------------------------
Replace-One $session @'
            ProtocolVersion = "0.1.6",
            CalibrationEnabled = calibration is not null,
'@ @'
            ProtocolVersion = "0.1.8",
            RequestedStopMode = options.StopMode.ToString(),
            RequestedStopValue = options.StopValue,
            BackgroundWindowAllowed = options.AllowBackgroundWindow,
            CalibrationEnabled = calibration is not null,
'@ 'RequestedStopMode = options.StopMode.ToString()'

Replace-One $session @'
        bool cancelled = false;
        string stopReason = "unknown";

        try
'@ @'
        bool cancelled = false;
        string stopReason = "unknown";
        DateTime actualCaptureStartedUtc = DateTime.UtcNow;

        try
'@ 'DateTime actualCaptureStartedUtc = DateTime.UtcNow;'

# The primary live capture request gets the user's background policy.
Replace-One $session @'
                        overlapRatio = tuning.OverlapRatio,
                        hideFixed = sequence > 1
'@ @'
                        overlapRatio = tuning.OverlapRatio,
                        hideFixed = sequence > 1,
                        allowBackgroundWindow = options.AllowBackgroundWindow
'@ 'hideFixed = sequence > 1,`r`n                        allowBackgroundWindow = options.AllowBackgroundWindow'

# Recovery and post-review are allowed to continue behind another desktop app too.
# A minimized browser remains a hard failure in the extension.
Replace-One $session @'
                    sampleMs = StabilitySampleMs,
                    hideFixed = frame.Sequence > 1
'@ @'
                    sampleMs = StabilitySampleMs,
                    hideFixed = frame.Sequence > 1,
                    allowBackgroundWindow = true
'@ 'hideFixed = frame.Sequence > 1,`r`n                    allowBackgroundWindow = true'

Replace-One $session @'
                            sampleMs = StabilitySampleMs,
                            hideFixed = true
'@ @'
                            sampleMs = StabilitySampleMs,
                            hideFixed = true,
                            allowBackgroundWindow = options.AllowBackgroundWindow
'@ 'hideFixed = true,`r`n                            allowBackgroundWindow = options.AllowBackgroundWindow'

# Persist background-window evidence returned by the extension.
Replace-One $session @'
            CaptureThrottleWaitMs = ReadInt(response, "captureThrottleWaitMs"),
            CapturedUtc = DateTime.UtcNow,
'@ @'
            CaptureThrottleWaitMs = ReadInt(response, "captureThrottleWaitMs"),
            TargetWindowFocused = ReadBool(response, "targetWindowFocused"),
            TargetWindowState = ReadOptionalString(response, "targetWindowState"),
            BackgroundWindowCapture = ReadBool(response, "backgroundWindowCapture"),
            CapturedUtc = DateTime.UtcNow,
'@ 'BackgroundWindowCapture = ReadBool(response, "backgroundWindowCapture")'

Replace-One $session @'
        frame.CaptureThrottleWaitMs = ReadInt(response, "captureThrottleWaitMs");
        frame.PageCounterCurrent = ReadInt(before, "pageCounterCurrent");
'@ @'
        frame.CaptureThrottleWaitMs = ReadInt(response, "captureThrottleWaitMs");
        frame.TargetWindowFocused = ReadBool(response, "targetWindowFocused");
        frame.TargetWindowState = ReadOptionalString(response, "targetWindowState");
        frame.BackgroundWindowCapture = ReadBool(response, "backgroundWindowCapture");
        frame.PageCounterCurrent = ReadInt(before, "pageCounterCurrent");
'@ 'frame.BackgroundWindowCapture = ReadBool(response, "backgroundWindowCapture");'

# Store all three overlap rails so diagnostics can explain a side-only failure.
Replace-One $session @'
        current.OverlapPixels = check.OverlapPixels;
        current.OverlapStatus = check.Detail;
'@ @'
        current.OverlapPixels = check.OverlapPixels;
        current.OverlapLeftBandMae = check.LeftBandMeanAbsoluteError;
        current.OverlapCenterBandMae = check.CenterBandMeanAbsoluteError;
        current.OverlapRightBandMae = check.RightBandMeanAbsoluteError;
        current.OverlapLeftBandStrongRatio = check.LeftBandStrongDiffRatio;
        current.OverlapCenterBandStrongRatio = check.CenterBandStrongDiffRatio;
        current.OverlapRightBandStrongRatio = check.RightBandStrongDiffRatio;
        current.OverlapLeftEdgeContamination = check.LeftEdgeContamination;
        current.OverlapRightEdgeContamination = check.RightEdgeContamination;
        current.OverlapStatus = check.Detail;
'@ 'current.OverlapLeftBandMae = check.LeftBandMeanAbsoluteError;'

# Count background frames and edge contamination immediately after adaptive observation.
Replace-One $session @'
                record.RepairCandidate = adaptiveDecision.RepairCandidate;
                if (record.RepairCandidate) manifest.AdaptiveRepairCandidates++;

                if (calibration is not null)
'@ @'
                record.RepairCandidate = adaptiveDecision.RepairCandidate ||
                    record.OverlapLeftEdgeContamination || record.OverlapRightEdgeContamination;
                if (record.RepairCandidate) manifest.AdaptiveRepairCandidates++;
                if (record.BackgroundWindowCapture) manifest.BackgroundWindowFrames++;
                if (record.OverlapLeftEdgeContamination || record.OverlapRightEdgeContamination)
                {
                    manifest.EdgeContaminationFrames++;
                    LongCaptureLog.Warn(
                        $"[BA_EDGE] frame={record.Sequence} leftMae={record.OverlapLeftBandMae:F3} centerMae={record.OverlapCenterBandMae:F3} " +
                        $"rightMae={record.OverlapRightBandMae:F3} status={LongCaptureLog.OneLine(record.OverlapStatus)}");
                }

                if (calibration is not null)
'@ '[BA_EDGE] frame='

# User-requested endpoints are checked after the current frame is accepted but before
# full-document bottom logic. The captured frame remains in the output; the page may
# already have advanced one scroll step, which does not change the stitched range.
Replace-One $session @'
                if (accepted.AtBottom && accepted.EndConfirmed)
                {
'@ @'
                if (BrowserAgentStopPolicyV018.TryMatch(
                    options,
                    accepted,
                    manifest.Frames.Count,
                    DateTime.UtcNow - actualCaptureStartedUtc,
                    out string requestedStopReason))
                {
                    stopReason = requestedStopReason;
                    status?.Invoke(
                        $"Requested capture end reached ({BrowserAgentStopPolicyV018.Describe(options.StopMode, options.StopValue)}). " +
                        $"Saved through frame {accepted.Sequence}, DOM progress {accepted.PageCounterCurrent}/{accepted.PageCounterTotal}.");
                    LongCaptureLog.Info(
                        $"[BA_END] requested stop matched reason={stopReason} frame={accepted.Sequence} " +
                        $"page={accepted.PageCounterCurrent}/{accepted.PageCounterTotal} elapsedMs={(DateTime.UtcNow - actualCaptureStartedUtc).TotalMilliseconds:F0}");
                    break;
                }

                if (accepted.AtBottom && accepted.EndConfirmed)
                {
'@ '[BA_END] requested stop matched'

# Requested-range completion is a successful completion, not an error/Partial. It also gets
# the adaptive post-review because the user did not press F8; manual F8 remains no-movement.
Replace-One $session @'
        if (!cancelled &&
            string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal) &&
            adaptive.IsAdaptive)
'@ @'
        if (!cancelled &&
            (string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal) ||
             BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason)) &&
            adaptive.IsAdaptive)
'@ 'BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason)) &&'

Replace-One $session @'
        bool complete = string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal);
        manifest.Status = complete
            ? "completed"
            : cancelled
                ? "partial-manual-stop"
                : "partial-" + stopReason;
'@ @'
        bool fullDocumentComplete = string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal);
        bool requestedRangeComplete = BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason);
        bool complete = fullDocumentComplete || requestedRangeComplete;
        manifest.Status = fullDocumentComplete
            ? "completed"
            : requestedRangeComplete
                ? "completed-requested-range"
                : cancelled
                    ? "partial-manual-stop"
                    : "partial-" + stopReason;
'@ 'bool requestedRangeComplete = BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason);'

# ---------------------------------------------------------------------------
# Extension: visibleTab backend can keep running with its browser window behind another
# desktop app, as long as the attached tab remains active in that window. Minimized is
# rejected fail-closed. Keep the current 3-permission boundary.
# ---------------------------------------------------------------------------
Replace-One $worker @'
async function captureVisibleTabMeasured() {
  const elapsed = lastCaptureStartMs > 0 ? Date.now() - lastCaptureStartMs : Number.POSITIVE_INFINITY;
'@ @'
async function captureVisibleTabMeasured(allowBackgroundWindow = true) {
  const tab = await chrome.tabs.get(target.tabId);
  const browserWindow = await chrome.windows.get(target.windowId);
  if (!tab.active || tab.windowId !== target.windowId) {
    throw new Error("The attached Chromium tab is no longer active in its browser window. Keep that tab selected while background capture runs.");
  }
  if (browserWindow.state === "minimized") {
    throw new Error("Background Browser Assisted Capture cannot use a minimized browser window on the captureVisibleTab backend. Restore the browser window or use the future CDP provider.");
  }
  if (!allowBackgroundWindow && !browserWindow.focused) {
    throw new Error("The target browser window is not foreground. Enable background-window capture or bring it to the front.");
  }

  const elapsed = lastCaptureStartMs > 0 ? Date.now() - lastCaptureStartMs : Number.POSITIVE_INFINITY;
'@ 'Background Browser Assisted Capture cannot use a minimized browser window'

Replace-One $worker @'
  return {
    pngDataUrl,
    captureDurationMs: Math.max(0, Math.round(performance.now() - started)),
    throttleWaitMs: Math.max(0, Math.round(throttleWaitMs))
  };
'@ @'
  return {
    pngDataUrl,
    captureDurationMs: Math.max(0, Math.round(performance.now() - started)),
    throttleWaitMs: Math.max(0, Math.round(throttleWaitMs)),
    targetWindowFocused: browserWindow.focused === true,
    targetWindowState: String(browserWindow.state || "unknown"),
    backgroundWindowCapture: browserWindow.focused !== true
  };
'@ 'backgroundWindowCapture: browserWindow.focused !== true'

Replace-One $worker @'
    const measured = await captureVisibleTabMeasured();
    pngDataUrl = measured.pngDataUrl;
    captureDurationMs = measured.captureDurationMs;
    captureThrottleWaitMs = measured.throttleWaitMs;
'@ @'
    const measured = await captureVisibleTabMeasured(payload.allowBackgroundWindow !== false);
    pngDataUrl = measured.pngDataUrl;
    captureDurationMs = measured.captureDurationMs;
    captureThrottleWaitMs = measured.throttleWaitMs;
    targetWindowFocused = measured.targetWindowFocused;
    targetWindowState = measured.targetWindowState;
    backgroundWindowCapture = measured.backgroundWindowCapture;
'@ 'targetWindowFocused = measured.targetWindowFocused;'

Replace-One $worker @'
  let captureDurationMs = 0;
  let captureThrottleWaitMs = 0;
'@ @'
  let captureDurationMs = 0;
  let captureThrottleWaitMs = 0;
  let targetWindowFocused = true;
  let targetWindowState = "normal";
  let backgroundWindowCapture = false;
'@ 'let backgroundWindowCapture = false;'

Replace-One $worker @'
    captureDurationMs,
    captureThrottleWaitMs,
    pngDataUrl
'@ @'
    captureDurationMs,
    captureThrottleWaitMs,
    targetWindowFocused,
    targetWindowState,
    backgroundWindowCapture,
    pngDataUrl
'@ 'targetWindowFocused,`r`n    targetWindowState,`r`n    backgroundWindowCapture,'

# Discourse exposes its sticky-avatar state with a semantic class. The generic computed-inset
# guard remains in place, but this targeted rule removes the Linux.do left-rail avatar that can
# pin at an offset and otherwise evade both edge-touching and median overlap checks.
Replace-One $worker @'
  const elements = document.body ? document.body.getElementsByTagName("*") : [];
  for (const element of elements) {
'@ @'
  for (const element of document.querySelectorAll(".topic-post.sticky-avatar .topic-avatar")) {
    if (!(element instanceof HTMLElement)) continue;
    const rect = element.getBoundingClientRect();
    if (!rect || rect.width <= 1 || rect.height <= 1) continue;
    if (rect.bottom <= 0 || rect.right <= 0 || rect.top >= viewportHeight || rect.left >= viewportWidth) continue;
    if (element.getAttribute(marker) === "1") continue;
    element.setAttribute(marker, "1");
    element.setAttribute(valueAttr, element.style.getPropertyValue("visibility") || "");
    element.setAttribute(priorityAttr, element.style.getPropertyPriority("visibility") || "");
    element.style.setProperty("visibility", "hidden", "important");
    hiddenCount++;
  }

  const elements = document.body ? document.body.getElementsByTagName("*") : [];
  for (const element of elements) {
'@ 'document.querySelectorAll(".topic-post.sticky-avatar .topic-avatar")'

if (-not $CheckOnly) {
    $workerText = [IO.File]::ReadAllText($worker)
    $workerText = $workerText.Replace('protocolVersion: "0.1.6"', 'protocolVersion: "0.1.8"')
    $workerText = $workerText.Replace('LongCapture Browser Agent v0.1.6 attached to this tab', 'LongCapture Browser Agent v0.1.8 attached to this tab')
    [IO.File]::WriteAllText($worker, $workerText, [Text.UTF8Encoding]::new($true))

    $manifestJson = Get-Content $manifest -Raw | ConvertFrom-Json
    $manifestJson.name = "LongCapture Browser Agent v0.1.8"
    $manifestJson.version = "0.1.8"
    $manifestJson.description = "Active-tab provider with rail-aware quality recovery, background-window capture, requested end conditions and adaptive verification."
    $manifestJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifest -Encoding UTF8

    $sessionText = [IO.File]::ReadAllText($session)
    $sessionText = $sessionText.Replace('Browser Agent v0.1.5 capture aborted', 'Browser Agent v0.1.8 capture aborted')
    $sessionText = $sessionText.Replace('Browser Agent v0.1.5 capture ended', 'Browser Agent v0.1.8 capture ended')
    $sessionText = $sessionText.Replace('LongCapture-BrowserAgent-v015-', 'LongCapture-BrowserAgent-v018-')
    [IO.File]::WriteAllText($session, $sessionText, [Text.UTF8Encoding]::new($true))

    $diagText = [IO.File]::ReadAllText($diagnostics)
    $diagText = $diagText.Replace('v0.1.5 timeline markers:', 'v0.1.8 timeline markers:')
    [IO.File]::WriteAllText($diagnostics, $diagText, [Text.UTF8Encoding]::new($true))
}

# Deterministic endpoint policy test joins the existing Browser Agent self-test.
Replace-One $selftest @'
            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
'@ @'
            if (!BrowserAgentStopPolicyV018.SelfTest())
            {
                LongCaptureLog.Warn("Browser Agent v0.1.8 self-test failed requested-end policy");
                return 37;
            }

            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
'@ 'self-test failed requested-end policy'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.8 reliability/background/requested-end compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.8 reliability/background/requested-end overlay applied." -ForegroundColor Green
}
