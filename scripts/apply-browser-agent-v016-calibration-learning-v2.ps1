[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$manifest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\manifest.json"
$diagnostics = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentDiagnosticsExporter.cs"
$selftest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.6] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.6 compatibility anchor missing: $Marker in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.6] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.6] applied: $Marker" -ForegroundColor Cyan
    }
}

# Main UI: real calibration button + no-activate live monitor.
Replace-One $main @'
        browserAgentAdaptiveUi.AttachHandlers(startDelay, scrollDelay, scrollAmount, autoScrollTop);
        Controls.Add(body);
'@ @'
        browserAgentAdaptiveUi.AttachHandlers(startDelay, scrollDelay, scrollAmount, autoScrollTop);
        browserAgentAdaptiveUi.AttachCalibrationHandler(RunBrowserCalibrationFromUiAsync);
        Controls.Add(body);
'@ 'browserAgentAdaptiveUi.AttachCalibrationHandler(RunBrowserCalibrationFromUiAsync);'

Replace-One $main @'
    private async Task RunBrowserAssistedCaptureAsync()
    {
'@ @'
    private async Task<BrowserAgentCalibrationResult> RunBrowserCalibrationFromUiAsync()
    {
        if (!BrowserAgentSelected)
        {
            throw new InvalidOperationException("Browser benchmark is only available in Browser Assisted Capture.");
        }
        if (!await RefreshReadinessAsync(showDialogOnFailure: true))
        {
            throw new InvalidOperationException("Attach the target Chromium/Helium tab before calibration.");
        }

        statusLabel.Text = "Browser calibration: activating the attached tab and measuring the real capture pipeline...";
        Hide();
        await Task.Delay(240);
        try
        {
            return await browserAgentController.RunCalibrationAsync(message => LongCaptureLog.Info($"[BA_CALIB] {LongCaptureLog.OneLine(message)}"));
        }
        finally
        {
            ShowMainWindow();
        }
    }

    private async Task RunBrowserAssistedCaptureAsync()
    {
'@ 'private async Task<BrowserAgentCalibrationResult> RunBrowserCalibrationFromUiAsync()'

Replace-One $main @'
        trayStopItem.Enabled = true;
        trayIcon.Visible = true;

        try
'@ @'
        trayStopItem.Enabled = true;
        trayIcon.Visible = true;
        browserAgentAdaptiveUi.BeginCaptureMonitor();

        try
'@ 'browserAgentAdaptiveUi.BeginCaptureMonitor();'

Replace-One $main @'
        finally
        {
            captureBusy = false;
'@ @'
        finally
        {
            browserAgentAdaptiveUi.EndCaptureMonitor();
            captureBusy = false;
'@ 'browserAgentAdaptiveUi.EndCaptureMonitor();'

# Session: bounded local calibration, per-frame learning, visible effective values.
Replace-One $session @'
        BrowserAgentCaptureOptions options = (captureOptions ?? BrowserAgentCaptureOptions.Default).Normalize();
        var adaptive = new BrowserAgentAdaptiveController(options.SpeedStrategy, options.RepairPrecision);
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
'@ @'
        BrowserAgentCaptureOptions options = (captureOptions ?? BrowserAgentCaptureOptions.Default).Normalize();
        BrowserAgentCalibrationProfile? calibration = options.UseLocalCalibration ? BrowserAgentCalibrationStore.Current : null;
        var adaptive = new BrowserAgentAdaptiveController(options.SpeedStrategy, options.RepairPrecision, calibration);
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
'@ 'BrowserAgentCalibrationProfile? calibration = options.UseLocalCalibration'

Replace-One $session @'
            RepairPrecision = options.RepairPrecision.ToString(),
            RegionSelectionRequired = options.RequireRegionSelection,
'@ @'
            RepairPrecision = options.RepairPrecision.ToString(),
            ProtocolVersion = "0.1.6",
            CalibrationEnabled = calibration is not null,
            CalibrationConfidenceStart = calibration?.Confidence ?? 0,
            CalibrationBenchmarkSamples = calibration?.BenchmarkSamples ?? 0,
            CalibrationFrameSamplesStart = calibration?.FrameSamples ?? 0,
            RegionSelectionRequired = options.RequireRegionSelection,
'@ 'CalibrationConfidenceStart = calibration?.Confidence ?? 0'

Replace-One $session @'
                record.EffectiveStableWindowMs = tuning.StableWindowMs;
                record.EffectiveOverlapRatio = tuning.OverlapRatio;

                ValidateProgress(manifest.Frames, record);
'@ @'
                record.EffectiveStartDelayMs = tuning.StartDelayMs;
                record.EffectiveStableWindowMs = tuning.StableWindowMs;
                record.EffectiveMaxWaitMs = tuning.MaxWaitMs;
                record.EffectiveOverlapRatio = tuning.OverlapRatio;
                record.CalibrationConfidence = calibration?.Confidence ?? 0;

                ValidateProgress(manifest.Frames, record);
'@ 'record.EffectiveStartDelayMs = tuning.StartDelayMs;'

Replace-One $session @'
                record.RepairCandidate = adaptiveDecision.RepairCandidate;
                if (record.RepairCandidate) manifest.AdaptiveRepairCandidates++;
                LongCaptureLog.Info(
'@ @'
                record.RepairCandidate = adaptiveDecision.RepairCandidate;
                if (record.RepairCandidate) manifest.AdaptiveRepairCandidates++;

                if (calibration is not null)
                {
                    calibration.ObserveFrame(record);
                    record.CalibrationConfidence = calibration.Confidence;
                    if (record.Sequence % 8 == 0) BrowserAgentCalibrationStore.Save();
                }

                BrowserAgentFrameTuning liveTuning = adaptive.Current;
                BrowserAgentAdaptiveTelemetryHub.Publish(new BrowserAgentAdaptiveRuntimeSnapshot(
                    record.Sequence,
                    BrowserAgentAdaptiveProfiles.ForGear(adaptive.TargetGear).Name,
                    liveTuning.Name,
                    liveTuning.StartDelayMs,
                    liveTuning.StableWindowMs,
                    liveTuning.MaxWaitMs,
                    liveTuning.OverlapRatio,
                    record.StabilityWaitMs,
                    record.StabilityActivityMs,
                    record.CaptureVisibleTabMs,
                    record.AdaptiveRiskScore,
                    record.AdaptiveRiskReasons,
                    calibration?.Confidence ?? 0,
                    (calibration?.BenchmarkSamples ?? 0) + (calibration?.FrameSamples ?? 0)));

                LongCaptureLog.Info(
'@ 'BrowserAgentAdaptiveTelemetryHub.Publish(new BrowserAgentAdaptiveRuntimeSnapshot('

Replace-One $session @'
            CaptureStateChanged = ReadBool(response, "captureStateChanged"),
            CapturedUtc = DateTime.UtcNow,
'@ @'
            CaptureStateChanged = ReadBool(response, "captureStateChanged"),
            CaptureVisibleTabMs = ReadInt(response, "captureDurationMs"),
            CaptureThrottleWaitMs = ReadInt(response, "captureThrottleWaitMs"),
            CapturedUtc = DateTime.UtcNow,
'@ 'CaptureVisibleTabMs = ReadInt(response, "captureDurationMs")'

Replace-One $session @'
        frame.CaptureStateChanged = ReadBool(response, "captureStateChanged");
        frame.PageCounterCurrent = ReadInt(before, "pageCounterCurrent");
'@ @'
        frame.CaptureStateChanged = ReadBool(response, "captureStateChanged");
        frame.CaptureVisibleTabMs = ReadInt(response, "captureDurationMs");
        frame.CaptureThrottleWaitMs = ReadInt(response, "captureThrottleWaitMs");
        frame.PageCounterCurrent = ReadInt(before, "pageCounterCurrent");
'@ 'frame.CaptureVisibleTabMs = ReadInt(response, "captureDurationMs");'

Replace-One $session @'
        record.StabilityWaitMs = ReadInt(stability, "waitedMs");
        record.StabilityTimedOut = !ReadBool(stability, "stable") || ReadBool(stability, "timedOut");
'@ @'
        record.StabilityWaitMs = ReadInt(stability, "waitedMs");
        record.StabilityQuietMs = ReadInt(stability, "quietMs");
        record.StabilityActivityMs = Math.Max(0, record.StabilityWaitMs - record.StabilityQuietMs);
        record.StabilityMaxFalseQuietMs = ReadInt(stability, "maxFalseQuietMs");
        record.StabilityTimedOut = !ReadBool(stability, "stable") || ReadBool(stability, "timedOut");
'@ 'record.StabilityMaxFalseQuietMs = ReadInt(stability, "maxFalseQuietMs");'

Replace-One $session @'
        status?.Invoke("Stitching verified frames using browser scroll geometry...");
'@ @'
        if (calibration is not null)
        {
            manifest.CalibrationConfidenceEnd = calibration.Confidence;
            manifest.CalibrationFrameSamplesEnd = calibration.FrameSamples;
            BrowserAgentCalibrationStore.Save();
            LongCaptureLog.Info($"[BA_CALIB] session-learned {calibration.Summary()}");
            SaveManifest(manifestPath, manifest);
        }
        BrowserAgentAdaptiveTelemetryHub.Reset();

        status?.Invoke("Stitching verified frames using browser scroll geometry...");
'@ 'manifest.CalibrationConfidenceEnd = calibration.Confidence;'

# Extension: real benchmark, hard 2/s rate guard, numeric telemetry only.
Replace-One $worker @'
let nativePort = null;
let target = null;
'@ @'
let nativePort = null;
let target = null;
let lastCaptureStartMs = 0;
'@ 'let lastCaptureStartMs = 0;'

Replace-One $worker @'
      case "probe":
        result = await collectState();
        break;
      case "captureAndScroll":
'@ @'
      case "probe":
        result = await collectState();
        break;
      case "benchmark":
        result = await benchmarkCapturePipeline(payload);
        break;
      case "captureAndScroll":
'@ 'case "benchmark":'

Replace-One $worker @'
async function captureCurrentViewport(payload, stabilityBefore, hideFixed) {
'@ @'
async function captureVisibleTabMeasured() {
  const elapsed = lastCaptureStartMs > 0 ? Date.now() - lastCaptureStartMs : Number.POSITIVE_INFINITY;
  const throttleWaitMs = Number.isFinite(elapsed) ? Math.max(0, MIN_CAPTURE_INTERVAL_MS - elapsed) : 0;
  if (throttleWaitMs > 0) await sleep(throttleWaitMs);

  await assertTargetIsActive();
  const started = performance.now();
  lastCaptureStartMs = Date.now();
  const pngDataUrl = await chrome.tabs.captureVisibleTab(target.windowId, { format: "png" });
  return {
    pngDataUrl,
    captureDurationMs: Math.max(0, Math.round(performance.now() - started)),
    throttleWaitMs: Math.max(0, Math.round(throttleWaitMs))
  };
}

async function benchmarkCapturePipeline(payload) {
  const count = Math.round(clampNumber(payload.samples, 2, 4, 3));
  const options = stabilityOptions(payload);
  const samples = [];
  for (let index = 0; index < count; index++) {
    const stability = await waitForStabilityOnTarget(options);
    const measured = await captureVisibleTabMeasured();
    samples.push({
      captureDurationMs: measured.captureDurationMs,
      captureThrottleWaitMs: measured.throttleWaitMs,
      waitedMs: Number(stability.waitedMs || 0),
      quietMs: Number(stability.quietMs || 0),
      activityMs: Math.max(0, Number(stability.waitedMs || 0) - Number(stability.quietMs || 0)),
      maxFalseQuietMs: Number(stability.maxFalseQuietMs || 0),
      mutationCount: Number(stability.mutationCount || 0),
      resizeCount: Number(stability.resizeCount || 0),
      pendingImages: Number(stability.pendingImages || 0),
      layoutShiftScore: Number(stability.layoutShiftScore || 0)
    });
  }
  return { samples, minCaptureIntervalMs: MIN_CAPTURE_INTERVAL_MS };
}

async function captureCurrentViewport(payload, stabilityBefore, hideFixed) {
'@ 'async function benchmarkCapturePipeline(payload)'

Replace-One $worker @'
  let hiddenCount = 0;
  let pngDataUrl;
'@ @'
  let hiddenCount = 0;
  let pngDataUrl;
  let captureDurationMs = 0;
  let captureThrottleWaitMs = 0;
'@ 'let captureDurationMs = 0;'

Replace-One $worker @'
    await assertTargetIsActive();
    pngDataUrl = await chrome.tabs.captureVisibleTab(target.windowId, { format: "png" });
'@ @'
    const measured = await captureVisibleTabMeasured();
    pngDataUrl = measured.pngDataUrl;
    captureDurationMs = measured.captureDurationMs;
    captureThrottleWaitMs = measured.throttleWaitMs;
'@ 'captureDurationMs = measured.captureDurationMs;'

# v0.1.3 already adds captureRegion to this return object; preserve it.
Replace-One $worker @'
    hiddenCount,
    captureStateChanged,
    captureRegion,
    pngDataUrl
'@ @'
    hiddenCount,
    captureStateChanged,
    captureRegion,
    captureDurationMs,
    captureThrottleWaitMs,
    pngDataUrl
'@ 'captureThrottleWaitMs,'

Replace-One $worker @'
  let layoutShiftCount = 0;
  let layoutShiftScore = 0;

  const readMetrics = () => {
'@ @'
  let layoutShiftCount = 0;
  let layoutShiftScore = 0;
  let maxFalseQuietMs = 0;

  const readMetrics = () => {
'@ 'let maxFalseQuietMs = 0;'

Replace-One $worker @'
  const markChange = () => {
    lastChange = performance.now();
  };
'@ @'
  const markChange = () => {
    const now = performance.now();
    maxFalseQuietMs = Math.max(maxFalseQuietMs, Math.max(0, now - lastChange));
    lastChange = now;
  };
'@ 'maxFalseQuietMs = Math.max(maxFalseQuietMs'

Replace-One $worker @'
            layoutShiftCount,
            layoutShiftScore,
            initialScrollHeight,
'@ @'
            layoutShiftCount,
            layoutShiftScore,
            maxFalseQuietMs: Math.round(maxFalseQuietMs),
            initialScrollHeight,
'@ 'maxFalseQuietMs: Math.round(maxFalseQuietMs)'

# The timeout return has a different indentation but the same unique trailing shape.
$text = [IO.File]::ReadAllText($worker)
$timeoutMarker = 'maxFalseQuietMs: Math.round(maxFalseQuietMs),'
if (([regex]::Matches($text, [regex]::Escape($timeoutMarker))).Count -lt 2) {
    $oldTimeout = @'
          layoutShiftCount,
          layoutShiftScore,
          initialScrollHeight,
'@
    $newTimeout = @'
          layoutShiftCount,
          layoutShiftScore,
          maxFalseQuietMs: Math.round(maxFalseQuietMs),
          initialScrollHeight,
'@
    if (-not $text.Contains($oldTimeout)) {
        throw "Browser Agent v0.1.6 compatibility anchor missing: timeout maxFalseQuietMs"
    }
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($worker, $text.Replace($oldTimeout, $newTimeout), [Text.UTF8Encoding]::new($true))
    }
}

if (-not $CheckOnly) {
    $workerText = [IO.File]::ReadAllText($worker)
    $workerText = $workerText.Replace('protocolVersion: "0.1.5"', 'protocolVersion: "0.1.6"')
    $workerText = $workerText.Replace('LongCapture Browser Agent v0.1.5 attached to this tab', 'LongCapture Browser Agent v0.1.6 attached to this tab')
    [IO.File]::WriteAllText($worker, $workerText, [Text.UTF8Encoding]::new($true))

    $manifestJson = Get-Content $manifest -Raw | ConvertFrom-Json
    $manifestJson.name = "LongCapture Browser Agent v0.1.6"
    $manifestJson.version = "0.1.6"
    $manifestJson.description = "Active-tab helper with local calibration, live adaptive telemetry, risk-aware repair, sticky suppression and visual overlap verification."
    $manifestJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifest -Encoding UTF8

    $diagText = [IO.File]::ReadAllText($diagnostics)
    $diagText = $diagText.Replace('LongCapture Browser Agent v0.1.5 diagnostics — adaptive quality/timeline logging enabled', 'LongCapture Browser Agent v0.1.6 diagnostics — calibration/adaptive/timeline logging enabled')
    $diagText = $diagText.Replace('[BA_ADAPT], [BA_REPAIR].', '[BA_ADAPT], [BA_REPAIR], [BA_CALIB], [BA_LIVE].')
    [IO.File]::WriteAllText($diagnostics, $diagText, [Text.UTF8Encoding]::new($true))
}

# Deterministic policy + persistence/corrupt-profile test.
Replace-One $selftest @'
            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
                LongCaptureLog.Warn("Browser Agent v0.1.5 self-test failed adaptive speed/recovery policy");
                return 36;
            }

            if (!RunFrameCodecRoundTrip())
'@ @'
            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
                LongCaptureLog.Warn("Browser Agent v0.1.6 self-test failed adaptive speed/calibration policy");
                return 36;
            }

            string calibrationSelfTest = Path.Combine(Path.GetTempPath(), "LongCapture-v016-calibration-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (!BrowserAgentCalibrationStore.PersistenceSelfTest(calibrationSelfTest))
                {
                    LongCaptureLog.Warn("Browser Agent v0.1.6 self-test failed calibration persistence/corrupt-profile handling");
                    return 37;
                }
            }
            finally
            {
                try { Directory.Delete(calibrationSelfTest, recursive: true); } catch { }
            }

            if (!RunFrameCodecRoundTrip())
'@ 'self-test failed calibration persistence/corrupt-profile handling'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.6 calibration/live-learning compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.6 local calibration, rate-limit throttle, live telemetry and persistent learning applied." -ForegroundColor Green
}
