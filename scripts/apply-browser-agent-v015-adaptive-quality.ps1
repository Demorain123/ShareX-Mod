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
        Write-Host "[BrowserAgent-v0.1.5] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.5 compatibility anchor missing: $Marker in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.5] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.5] applied: $Marker" -ForegroundColor Cyan
    }
}

# ---------------------------------------------------------------------------
# Integrated UI: a named capture-speed strategy plus adaptive repair precision.
# The underlying numeric controls stay visible so a preset is never a black box.
# ---------------------------------------------------------------------------
Replace-One $main @'
    private readonly BrowserAgentUiModeAdapterV013 browserAgentUiAdapter = new();
    private TableLayoutPanel mainBody = null!;
'@ @'
    private readonly BrowserAgentUiModeAdapterV013 browserAgentUiAdapter = new();
    private readonly BrowserAgentAdaptiveUiV015 browserAgentAdaptiveUi = new();
    private TableLayoutPanel mainBody = null!;
'@ 'BrowserAgentAdaptiveUiV015 browserAgentAdaptiveUi'

Replace-One $main @'
        BrowserAgentUiModeAdapterV013.Polish(this, body);
        Controls.Add(body);
'@ @'
        BrowserAgentUiModeAdapterV013.Polish(this, body);
        browserAgentAdaptiveUi.AttachHandlers(startDelay, scrollDelay, scrollAmount, autoScrollTop);
        Controls.Add(body);
'@ 'browserAgentAdaptiveUi.AttachHandlers('

Replace-One $main @'
            browserAgentUiAdapter.SetMode(
                true, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
                autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
            browserButton.Text = "Open Browser Agent folder";
'@ @'
            browserAgentUiAdapter.SetMode(
                true, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
                autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
            browserAgentAdaptiveUi.SetMode(true, mainBody, startDelay, scrollDelay, scrollAmount, autoScrollTop);
            browserButton.Text = "Open Browser Agent folder";
'@ 'browserAgentAdaptiveUi.SetMode(true'

Replace-One $main @'
        browserAgentUiAdapter.SetMode(
            false, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
            autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
        targetSelector.Enabled = !captureBusy;
'@ @'
        browserAgentAdaptiveUi.SetMode(false, mainBody, startDelay, scrollDelay, scrollAmount, autoScrollTop);
        browserAgentUiAdapter.SetMode(
            false, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
            autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
        targetSelector.Enabled = !captureBusy;
'@ 'browserAgentAdaptiveUi.SetMode(false'

Replace-One $main @'
            BrowserAgentCaptureOptions browserOptions = browserAgentUiAdapter.BuildOptions(
                startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture);
            LongCaptureLog.Info(
                $"Browser Agent v0.1.3 options startDelay={browserOptions.StartDelayMs} settle={browserOptions.StableWindowMs} overlap={browserOptions.OverlapRatio:P0} preload={browserOptions.PreloadDynamicContent} regionSelect={browserOptions.RequireRegionSelection}");
'@ @'
            BrowserAgentCaptureOptions browserOptions = browserAgentAdaptiveUi.EnrichOptions(
                browserAgentUiAdapter.BuildOptions(
                    startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture));
            LongCaptureLog.Info(
                $"Browser Agent v0.1.5 options strategy={browserOptions.SpeedStrategy} precision={browserOptions.RepairPrecision} " +
                $"startDelay={browserOptions.StartDelayMs} settle={browserOptions.StableWindowMs} overlap={browserOptions.OverlapRatio:P0} " +
                $"preload={browserOptions.PreloadDynamicContent} regionSelect={browserOptions.RequireRegionSelection}");
'@ 'Browser Agent v0.1.5 options strategy='

# ---------------------------------------------------------------------------
# Capture loop: fixed presets remain constant; adaptive profiles use the same
# lightweight evidence already collected for settle/overlap and change the NEXT
# frame's gear. Risky areas are marked and selectively re-captured.
# ---------------------------------------------------------------------------
Replace-One $session @'
        BrowserAgentCaptureOptions options = (captureOptions ?? BrowserAgentCaptureOptions.Default).Normalize();
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
'@ @'
        BrowserAgentCaptureOptions options = (captureOptions ?? BrowserAgentCaptureOptions.Default).Normalize();
        var adaptive = new BrowserAgentAdaptiveController(options.SpeedStrategy, options.RepairPrecision);
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
'@ 'new BrowserAgentAdaptiveController(options.SpeedStrategy'

Replace-One $session @'
            OverlapRatio = options.OverlapRatio,
            RegionSelectionRequired = options.RequireRegionSelection,
'@ @'
            OverlapRatio = options.OverlapRatio,
            SpeedStrategy = options.SpeedStrategy.ToString(),
            RepairPrecision = options.RepairPrecision.ToString(),
            RegionSelectionRequired = options.RequireRegionSelection,
'@ 'SpeedStrategy = options.SpeedStrategy.ToString()'

Replace-One $session @'
            for (int sequence = 1; sequence <= safetyFrameLimit; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status?.Invoke($"Capturing browser frame {sequence} — Auto until page end (F8 stops early)...");

                JsonElement response = await bridge.SendRequestAsync(
'@ @'
            for (int sequence = 1; sequence <= safetyFrameLimit; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrowserAgentFrameTuning tuning = adaptive.Current;
                status?.Invoke($"Capturing browser frame {sequence} — {tuning.Name} speed, Auto until page end (F8 stops early)...");

                JsonElement response = await bridge.SendRequestAsync(
'@ 'BrowserAgentFrameTuning tuning = adaptive.Current;'

Replace-One $session @'
                        stableWindowMs = options.StableWindowMs,
                        maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
                        sampleMs = StabilitySampleMs,
                        overlapRatio = options.OverlapRatio,
'@ @'
                        stableWindowMs = tuning.StableWindowMs,
                        maxWaitMs = tuning.MaxWaitMs,
                        sampleMs = StabilitySampleMs,
                        overlapRatio = tuning.OverlapRatio,
'@ 'stableWindowMs = tuning.StableWindowMs'

Replace-One $session @'
                    response,
                    options.OverlapRatio,
                    cancellationToken).ConfigureAwait(false);

                ValidateProgress(manifest.Frames, record);
'@ @'
                    response,
                    tuning.OverlapRatio,
                    cancellationToken).ConfigureAwait(false);
                record.EffectiveStableWindowMs = tuning.StableWindowMs;
                record.EffectiveOverlapRatio = tuning.OverlapRatio;

                ValidateProgress(manifest.Frames, record);
'@ 'record.EffectiveStableWindowMs = tuning.StableWindowMs;'

Replace-One $session @'
                SaveManifest(manifestPath, manifest);
                BrowserAgentFrameRecord accepted = manifest.Frames[^1];
'@ @'
                BrowserAgentAdaptiveDecision adaptiveDecision = adaptive.Observe(record);
                record.AdaptiveRiskScore = adaptiveDecision.RiskScore;
                record.AdaptiveRiskReasons = adaptiveDecision.Reasons;
                record.AdaptiveSpeedBefore = adaptiveDecision.BeforeSpeed;
                record.AdaptiveSpeedAfter = adaptiveDecision.AfterSpeed;
                record.RepairCandidate = adaptiveDecision.RepairCandidate;
                if (record.RepairCandidate) manifest.AdaptiveRepairCandidates++;
                LongCaptureLog.Info(
                    $"[BA_ADAPT] frame={record.Sequence} risk={record.AdaptiveRiskScore} reasons={LongCaptureLog.OneLine(record.AdaptiveRiskReasons)} " +
                    $"speed={record.AdaptiveSpeedBefore}->{record.AdaptiveSpeedAfter} repairCandidate={record.RepairCandidate}");

                if (adaptiveDecision.RequestImmediateRecovery && record.Sequence > 2 && record.RecoveryGeneration == 0)
                {
                    manifest.AdaptiveRepairAttempts++;
                    record.RepairAttempts++;
                    status?.Invoke($"Adaptive quality guard: risky area near frame {record.Sequence}; slowing down and re-capturing the recent window...");
                    bool adaptiveRecovered = await RecoverRecentWindowAsync(
                        sessionDirectory,
                        manifest,
                        status,
                        cancellationToken).ConfigureAwait(false);
                    if (adaptiveRecovered)
                    {
                        manifest.AdaptiveRepairSuccesses++;
                        record.RepairStatus = "immediate-recovery-ok";
                    }
                    else
                    {
                        manifest.AdaptiveRepairFailures++;
                        record.RepairStatus = "immediate-recovery-unresolved";
                    }
                }

                SaveManifest(manifestPath, manifest);
                BrowserAgentFrameRecord accepted = manifest.Frames[^1];
'@ '[BA_ADAPT] frame='

Replace-One $session @'
                    $"Frame {sequence}: y={accepted.ScrollYCss:F0}, stable={accepted.StabilityWaitMs}ms, " +
                    $"grow={accepted.LazyWarmupGrowthCss + accepted.EndConfirmationGrowthCss:F0}px, " +
                    $"overlap={accepted.OverlapMeanAbsoluteError:F2}{pageHint}{remainingHint}");
'@ @'
                    $"Frame {sequence}: y={accepted.ScrollYCss:F0}, stable={accepted.StabilityWaitMs}ms, " +
                    $"grow={accepted.LazyWarmupGrowthCss + accepted.EndConfirmationGrowthCss:F0}px, " +
                    $"overlap={accepted.OverlapMeanAbsoluteError:F2}, risk={accepted.AdaptiveRiskScore}, speed={accepted.AdaptiveSpeedAfter}{pageHint}{remainingHint}");
'@ 'risk={accepted.AdaptiveRiskScore}, speed={accepted.AdaptiveSpeedAfter}'

Replace-One $session @'
        status?.Invoke("Stitching verified frames using browser scroll geometry...");
        string outputPath = Path.Combine(sessionDirectory, $"LongCapture-BrowserAgent-v013-{stamp}.png");
'@ @'
        if (!cancelled &&
            string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal) &&
            adaptive.IsAdaptive)
        {
            status?.Invoke($"Adaptive quality review ({options.RepairPrecision}) — checking marked sections before final stitch...");
            int unresolvedRepairs = await RunAdaptivePostReviewAsync(
                sessionDirectory,
                manifest,
                options,
                status,
                cancellationToken).ConfigureAwait(false);
            if (unresolvedRepairs > 0)
            {
                stopReason = "quality-review-unresolved";
                status?.Invoke($"Adaptive quality review left {unresolvedRepairs} suspicious section(s); result will be marked Partial.");
            }
            SaveManifest(manifestPath, manifest);
        }

        status?.Invoke("Stitching verified frames using browser scroll geometry...");
        string outputPath = Path.Combine(sessionDirectory, $"LongCapture-BrowserAgent-v015-{stamp}.png");
'@ 'RunAdaptivePostReviewAsync('

Replace-One $session @'
    private static void ApplyStability(BrowserAgentFrameRecord record, JsonElement response)
    {
'@ @'
    private async Task<int> RunAdaptivePostReviewAsync(
        string sessionDirectory,
        BrowserAgentSessionManifest manifest,
        BrowserAgentCaptureOptions options,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        int budget = BrowserAgentAdaptiveProfiles.PostReviewBudget(options.RepairPrecision);
        int attempts = BrowserAgentAdaptiveProfiles.PostReviewAttempts(options.RepairPrecision);
        List<int> candidates = manifest.Frames
            .Select((frame, index) => new { frame, index })
            .Where(item =>
                item.index > 1 &&
                item.index < manifest.Frames.Count - 1 &&
                item.frame.RepairCandidate &&
                !item.frame.RepairStatus.EndsWith("-ok", StringComparison.Ordinal))
            .OrderByDescending(item => item.frame.AdaptiveRiskScore)
            .ThenBy(item => item.frame.Sequence)
            .Take(budget)
            .Select(item => item.index)
            .OrderBy(index => index)
            .ToList();

        if (candidates.Count == 0)
        {
            LongCaptureLog.Info("[BA_REPAIR] post-review candidates=0");
            return 0;
        }

        double resumeY = manifest.Frames[^1].ScrollYAfterCss;
        int unresolved = 0;
        BrowserAgentFrameTuning repairTuning = BrowserAgentAdaptiveProfiles.ForGear(
            options.RepairPrecision == BrowserAgentRepairPrecision.High ? 0 :
            options.RepairPrecision == BrowserAgentRepairPrecision.Medium ? 1 : 2);

        LongCaptureLog.Info($"[BA_REPAIR] post-review candidates={candidates.Count} attemptsPerCandidate={attempts} precision={options.RepairPrecision}");

        foreach (int index in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrowserAgentFrameRecord frame = manifest.Frames[index];
            bool repaired = false;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                manifest.AdaptiveRepairAttempts++;
                frame.RepairAttempts++;
                status?.Invoke($"Repair review: frame {frame.Sequence}, attempt {attempt}/{attempts}...");
                LongCaptureLog.Info($"[BA_REPAIR] frame={frame.Sequence} phase=recapture attempt={attempt}/{attempts} y={frame.ScrollYCss:F0}");

                try
                {
                    JsonElement response = await bridge.SendRequestAsync(
                        "captureAt",
                        new
                        {
                            scrollY = frame.ScrollYCss,
                            stableWindowMs = repairTuning.StableWindowMs,
                            maxWaitMs = repairTuning.MaxWaitMs,
                            sampleMs = StabilitySampleMs,
                            hideFixed = true
                        },
                        TimeSpan.FromSeconds(40),
                        cancellationToken).ConfigureAwait(false);

                    await ReplaceFrameFromRecaptureAsync(
                        sessionDirectory,
                        frame,
                        response,
                        cancellationToken).ConfigureAwait(false);
                    frame.RecoveryGeneration++;

                    BrowserAgentOverlapCheck left = VerifyAndStoreOverlap(
                        sessionDirectory,
                        manifest.Frames[index - 1],
                        frame);
                    BrowserAgentOverlapCheck right = VerifyAndStoreOverlap(
                        sessionDirectory,
                        frame,
                        manifest.Frames[index + 1]);

                    repaired = left.Acceptable && right.Acceptable &&
                        !frame.StabilityTimedOut && !frame.CaptureStateChanged;
                    LongCaptureLog.Info(
                        $"[BA_REPAIR] frame={frame.Sequence} phase=verify attempt={attempt} ok={repaired} " +
                        $"leftMae={left.MeanAbsoluteError:F3} rightMae={right.MeanAbsoluteError:F3} stateChanged={frame.CaptureStateChanged}");
                    if (repaired) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LongCaptureLog.Warn($"[BA_REPAIR] frame={frame.Sequence} attempt={attempt} error={LongCaptureLog.OneLine(ex.Message)}");
                }
            }

            if (repaired)
            {
                frame.RepairStatus = "post-review-ok";
                manifest.AdaptiveRepairSuccesses++;
            }
            else
            {
                frame.RepairStatus = "post-review-unresolved";
                manifest.AdaptiveRepairFailures++;
                unresolved++;
            }
        }

        try
        {
            await bridge.SendRequestAsync(
                "moveTo",
                new
                {
                    scrollY = resumeY,
                    stableWindowMs = Math.Max(650, options.StableWindowMs),
                    maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
                    sampleMs = StabilitySampleMs
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"[BA_REPAIR] could not restore final page position: {LongCaptureLog.OneLine(ex.Message)}");
        }

        return unresolved;
    }

    private static void ApplyStability(BrowserAgentFrameRecord record, JsonElement response)
    {
'@ 'private async Task<int> RunAdaptivePostReviewAsync('

Replace-One $session @'
        record.StabilityHeightChangeCount = ReadInt(stability, "heightChangeCount");
        record.StabilityPendingImages = ReadInt(stability, "pendingImages");
'@ @'
        record.StabilityHeightChangeCount = ReadInt(stability, "heightChangeCount");
        record.StabilityPendingImages = ReadInt(stability, "pendingImages");
        record.StabilityLayoutShiftCount = ReadInt(stability, "layoutShiftCount");
        record.StabilityLayoutShiftScore = ReadDoubleOrDefault(stability, "layoutShiftScore");
'@ 'record.StabilityLayoutShiftScore = ReadDoubleOrDefault'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($session)
    $text = $text.Replace('Browser Agent v0.1.3 capture aborted', 'Browser Agent v0.1.5 capture aborted')
    $text = $text.Replace('Browser Agent v0.1.3 capture ended', 'Browser Agent v0.1.5 capture ended')
    [IO.File]::WriteAllText($session, $text, [Text.UTF8Encoding]::new($true))
}

# ---------------------------------------------------------------------------
# Browser evidence: observe post-load LayoutShift entries and detect sticky
# controls against their CSS inset rather than only literal viewport edges.
# This catches Linux.do-style inset sticky avatars without hiding every sticky
# element before it actually becomes pinned.
# ---------------------------------------------------------------------------
Replace-One $worker @'
  let pendingImages = 0;

  const readMetrics = () => {
'@ @'
  let pendingImages = 0;
  let layoutShiftCount = 0;
  let layoutShiftScore = 0;

  const readMetrics = () => {
'@ 'let layoutShiftScore = 0;'

Replace-One $worker @'
  const mutationObserver = new MutationObserver((records) => {
    mutationCount += records.length;
    markChange();
  });

  mutationObserver.observe(document.documentElement, {
'@ @'
  const mutationObserver = new MutationObserver((records) => {
    mutationCount += records.length;
    markChange();
  });

  let layoutShiftObserver = null;
  try {
    if (typeof PerformanceObserver !== "undefined" &&
        PerformanceObserver.supportedEntryTypes?.includes("layout-shift")) {
      layoutShiftObserver = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          if (entry.hadRecentInput) continue;
          layoutShiftCount++;
          layoutShiftScore += Number(entry.value || 0);
          markChange();
        }
      });
      layoutShiftObserver.observe({ type: "layout-shift", buffered: false });
    }
  } catch (_) {
    layoutShiftObserver = null;
  }

  mutationObserver.observe(document.documentElement, {
'@ 'layoutShiftObserver.observe({ type: "layout-shift"'

Replace-One $worker @'
            pendingImages: finalMetrics.pendingImages,
            initialScrollHeight,
            finalScrollHeight: finalMetrics.scrollHeight
'@ @'
            pendingImages: finalMetrics.pendingImages,
            layoutShiftCount,
            layoutShiftScore,
            initialScrollHeight,
            finalScrollHeight: finalMetrics.scrollHeight
'@ 'layoutShiftCount,`r`n            layoutShiftScore,'

Replace-One $worker @'
          pendingImages,
          initialScrollHeight,
          finalScrollHeight: current.scrollHeight
'@ @'
          pendingImages,
          layoutShiftCount,
          layoutShiftScore,
          initialScrollHeight,
          finalScrollHeight: current.scrollHeight
'@ 'pendingImages,`r`n          layoutShiftCount,'

Replace-One $worker @'
  } finally {
    mutationObserver.disconnect();
    resizeObserver?.disconnect();
  }
}
'@ @'
  } finally {
    mutationObserver.disconnect();
    resizeObserver?.disconnect();
    layoutShiftObserver?.disconnect();
  }
}
'@ 'layoutShiftObserver?.disconnect();'

# collectDocumentState sticky activation test.
Replace-One $worker @'
    const touchesTop = Math.abs(rect.top) <= 3;
    const touchesBottom = Math.abs(rect.bottom - viewportHeight) <= 3;
    const touchesLeft = Math.abs(rect.left) <= 3;
    const touchesRight = Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky" && !(touchesTop || touchesBottom || touchesLeft || touchesRight)) continue;
'@ @'
    const touchesTop = Math.abs(rect.top) <= 3;
    const touchesBottom = Math.abs(rect.bottom - viewportHeight) <= 3;
    const touchesLeft = Math.abs(rect.left) <= 3;
    const touchesRight = Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky") {
      const inset = name => {
        const raw = style.getPropertyValue(name);
        if (!raw || raw === "auto") return null;
        const value = Number.parseFloat(raw);
        return Number.isFinite(value) ? value : null;
      };
      const topInset = inset("top");
      const bottomInset = inset("bottom");
      const leftInset = inset("left");
      const rightInset = inset("right");
      const pinned =
        (topInset !== null && Math.abs(rect.top - topInset) <= 5) ||
        (bottomInset !== null && Math.abs(rect.bottom - (viewportHeight - bottomInset)) <= 5) ||
        (leftInset !== null && Math.abs(rect.left - leftInset) <= 5) ||
        (rightInset !== null && Math.abs(rect.right - (viewportWidth - rightInset)) <= 5);
      if (!pinned) continue;
    }
'@ 'const topInset = inset("top");'

# hideSafeFixedCandidates sticky activation test. This intentionally uses the
# same computed-inset evidence as collectDocumentState.
Replace-One $worker @'
    const touchesEdge = Math.abs(rect.top) <= 3 || Math.abs(rect.bottom - viewportHeight) <= 3 || Math.abs(rect.left) <= 3 || Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky" && !touchesEdge) continue;

    const visibleWidth = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
'@ @'
    const touchesEdge = Math.abs(rect.top) <= 3 || Math.abs(rect.bottom - viewportHeight) <= 3 || Math.abs(rect.left) <= 3 || Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky") {
      const inset = name => {
        const raw = style.getPropertyValue(name);
        if (!raw || raw === "auto") return null;
        const value = Number.parseFloat(raw);
        return Number.isFinite(value) ? value : null;
      };
      const topInset = inset("top");
      const bottomInset = inset("bottom");
      const leftInset = inset("left");
      const rightInset = inset("right");
      const pinned =
        (topInset !== null && Math.abs(rect.top - topInset) <= 5) ||
        (bottomInset !== null && Math.abs(rect.bottom - (viewportHeight - bottomInset)) <= 5) ||
        (leftInset !== null && Math.abs(rect.left - leftInset) <= 5) ||
        (rightInset !== null && Math.abs(rect.right - (viewportWidth - rightInset)) <= 5);
      if (!pinned) continue;
    }

    const visibleWidth = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
'@ 'const pinned =`r`n        (topInset !== null'

if (-not $CheckOnly) {
    $workerText = [IO.File]::ReadAllText($worker)
    $workerText = $workerText.Replace('protocolVersion: "0.1.4"', 'protocolVersion: "0.1.5"')
    $workerText = $workerText.Replace('LongCapture Browser Agent v0.1.4 attached to this tab', 'LongCapture Browser Agent v0.1.5 attached to this tab')
    [IO.File]::WriteAllText($worker, $workerText, [Text.UTF8Encoding]::new($true))

    $manifestJson = Get-Content $manifest -Raw | ConvertFrom-Json
    $manifestJson.name = "LongCapture Browser Agent v0.1.5"
    $manifestJson.version = "0.1.5"
    $manifestJson.description = "Active-tab helper with adaptive speed, risk-aware local repair, sticky suppression, region capture and visual overlap verification."
    $manifestJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifest -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Diagnostics: never omit the currently active main-process log just because
# its filesystem timestamp falls outside a coarse session-window test.
# ---------------------------------------------------------------------------
Replace-One $diagnostics @'
            DateTime logStart = startedUtc.AddMinutes(-5);
            DateTime logEnd = completedUtc.AddMinutes(5);
            if (Directory.Exists(LongCaptureLog.LogDirectory))
            {
                foreach (string log in Directory.EnumerateFiles(LongCaptureLog.LogDirectory, "LongCapture-*.log", SearchOption.TopDirectoryOnly))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(log);
                    if (modified >= logStart && modified <= logEnd)
                    {
                        archive.CreateEntryFromFile(log, Path.Combine("logs", Path.GetFileName(log)), CompressionLevel.Optimal);
                    }
                }
            }
'@ @'
            DateTime logStart = startedUtc.AddMinutes(-5);
            DateTime logEnd = completedUtc.AddMinutes(5);
            if (Directory.Exists(LongCaptureLog.LogDirectory))
            {
                string currentLog = LongCaptureLog.CurrentLogPath;
                HashSet<string> selectedLogs = new(StringComparer.OrdinalIgnoreCase);
                foreach (string log in Directory.EnumerateFiles(LongCaptureLog.LogDirectory, "LongCapture-*.log", SearchOption.TopDirectoryOnly))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(log);
                    if (modified >= logStart && modified <= logEnd)
                    {
                        selectedLogs.Add(log);
                    }
                }
                if (File.Exists(currentLog)) selectedLogs.Add(currentLog);
                foreach (string recent in Directory
                    .EnumerateFiles(LongCaptureLog.LogDirectory, "LongCapture-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(4))
                {
                    selectedLogs.Add(recent);
                }
                foreach (string log in selectedLogs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    archive.CreateEntryFromFile(log, Path.Combine("logs", Path.GetFileName(log)), CompressionLevel.Optimal);
                }
            }
'@ 'HashSet<string> selectedLogs = new(StringComparer.OrdinalIgnoreCase);'

if (-not $CheckOnly) {
    $diagText = [IO.File]::ReadAllText($diagnostics)
    $diagText = $diagText.Replace('LongCapture Browser Agent v0.1.4 diagnostics — request/timeline logging enabled', 'LongCapture Browser Agent v0.1.5 diagnostics — adaptive quality/timeline logging enabled')
    $diagText = $diagText.Replace('v0.1.4 timeline markers: [USER_ACTION], [BA_TIMELINE], [BA_REQ], [BA_AGENT].', 'v0.1.5 timeline markers: [USER_ACTION], [BA_TIMELINE], [BA_REQ], [BA_AGENT], [BA_ADAPT], [BA_REPAIR].')
    [IO.File]::WriteAllText($diagnostics, $diagText, [Text.UTF8Encoding]::new($true))
}

# Deterministic policy test is part of the existing packaged Browser Agent self-test.
Replace-One $selftest @'
            if (!RunFrameCodecRoundTrip())
            {
'@ @'
            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
                LongCaptureLog.Warn("Browser Agent v0.1.5 self-test failed adaptive speed/recovery policy");
                return 36;
            }

            if (!RunFrameCodecRoundTrip())
            {
'@ 'self-test failed adaptive speed/recovery policy'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.5 adaptive-quality compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.5 adaptive speed, selective repair, sticky and layout-shift evidence applied." -ForegroundColor Green
}
