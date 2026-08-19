[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$adaptive = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentAdaptivePolicy.cs"
$adaptiveUi = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentAdaptiveUiV015.cs"
$options = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureOptions.cs"
$models = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentModels.cs"
$endUi = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentEndConditionUiV018.cs"
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$selftest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.8-repair-r3] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.8 repair-r3 compatibility anchor missing: $Marker in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.8-repair-r3] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.8-repair-r3] applied: $Marker" -ForegroundColor Cyan
    }
}

# ---------------------------------------------------------------------------
# Precision policy: Perfect has no attempt-count limit. A time budget remains a
# separate safety/UX constraint; Auto maps Perfect to 15 minutes and can later be
# exposed as Unlimited without changing the repair algorithm.
# ---------------------------------------------------------------------------
Replace-One $adaptive @'
internal enum BrowserAgentRepairPrecision
{
    Low,
    Medium,
    High
}
'@ @'
internal enum BrowserAgentRepairPrecision
{
    Low,
    Medium,
    High,
    Perfect
}
'@ 'BrowserAgentRepairPrecision.Perfect'

Replace-One $adaptive `
    -Old '    public static readonly string[] PrecisionDisplayNames = ["Low", "Medium", "High"];' `
    -New '    public static readonly string[] PrecisionDisplayNames = ["Low", "Medium", "High", "Perfect"];' `
    -Marker '"Perfect"]'

Replace-One $adaptive @'
        BrowserAgentRepairPrecision.High => 3,
        _ => 4
'@ @'
        BrowserAgentRepairPrecision.High => 3,
        BrowserAgentRepairPrecision.Perfect => 2,
        _ => 4
'@ 'BrowserAgentRepairPrecision.Perfect => 2'

Replace-One $adaptive @'
        BrowserAgentRepairPrecision.High => 5,
        _ => 7
'@ @'
        BrowserAgentRepairPrecision.High => 5,
        BrowserAgentRepairPrecision.Perfect => 4,
        _ => 7
'@ 'BrowserAgentRepairPrecision.Perfect => 4'

Replace-One $adaptive @'
        BrowserAgentRepairPrecision.High => 16,
        _ => 8
'@ @'
        BrowserAgentRepairPrecision.High => 24,
        BrowserAgentRepairPrecision.Perfect => int.MaxValue,
        _ => 8
'@ 'BrowserAgentRepairPrecision.Perfect => int.MaxValue'

Replace-One $adaptive @'
        BrowserAgentRepairPrecision.High => 3,
        _ => 2
    };

    public static bool SelfTest()
'@ @'
        BrowserAgentRepairPrecision.High => 4,
        BrowserAgentRepairPrecision.Perfect => int.MaxValue,
        _ => 2
    };

    public static bool UnlimitedRepairAttempts(BrowserAgentRepairPrecision precision) =>
        precision == BrowserAgentRepairPrecision.Perfect;

    public static int DefaultRepairTimeLimitSeconds(BrowserAgentRepairPrecision precision) => precision switch
    {
        BrowserAgentRepairPrecision.Low => 30,
        BrowserAgentRepairPrecision.Medium => 120,
        BrowserAgentRepairPrecision.High => 300,
        BrowserAgentRepairPrecision.Perfect => 900,
        _ => 120
    };

    public static bool SelfTest()
'@ 'DefaultRepairTimeLimitSeconds(BrowserAgentRepairPrecision precision)'

# Integrity risk discovered outside the adaptive timing controller is still allowed
# to influence speed and repair candidacy on the next frame.
Replace-One $adaptive @'
        if (Math.Abs(frame.VisualDeltaOffsetPixels) >= 18) Add(3, $"visual-correction:{frame.VisualDeltaOffsetPixels}px");
        else if (Math.Abs(frame.VisualDeltaOffsetPixels) >= 8) Add(1, $"visual-correction:{frame.VisualDeltaOffsetPixels}px");

        // AIMD-like behavior:
'@ @'
        if (Math.Abs(frame.VisualDeltaOffsetPixels) >= 18) Add(3, $"visual-correction:{frame.VisualDeltaOffsetPixels}px");
        else if (Math.Abs(frame.VisualDeltaOffsetPixels) >= 8) Add(1, $"visual-correction:{frame.VisualDeltaOffsetPixels}px");
        if (frame.IntegrityRiskScore >= 8) Add(5, "integrity-risk-high");
        else if (frame.IntegrityRiskScore >= 3) Add(2, "integrity-risk");
        if (frame.AnchorSharedCount > 0 && frame.AnchorMaxDocumentDriftCss >= 18) Add(4, $"anchor-drift:{frame.AnchorMaxDocumentDriftCss:F1}px");
        if (frame.ScrollDeltaRobustZ >= 8) Add(4, $"scroll-outlier:z{frame.ScrollDeltaRobustZ:F1}");
        else if (frame.ScrollDeltaRobustZ >= 4.5) Add(2, $"scroll-outlier:z{frame.ScrollDeltaRobustZ:F1}");

        // AIMD-like behavior:
'@ 'frame.IntegrityRiskScore >= 8'

# ---------------------------------------------------------------------------
# Capture options and UI: add a repair time budget. -1 means explicit Unlimited;
# 0 means use the precision profile default. Perfect remains unlimited by attempt.
# ---------------------------------------------------------------------------
Replace-One $options @'
    public BrowserAgentRepairPrecision RepairPrecision { get; init; } = BrowserAgentRepairPrecision.Medium;

    // v0.1.6:
'@ @'
    public BrowserAgentRepairPrecision RepairPrecision { get; init; } = BrowserAgentRepairPrecision.Medium;
    public int RepairTimeLimitSeconds { get; init; } = 0; // 0=profile default, -1=Unlimited

    // v0.1.6:
'@ 'RepairTimeLimitSeconds { get; init; }'

Replace-One $options @'
            RepairPrecision = Enum.IsDefined(RepairPrecision) ? RepairPrecision : BrowserAgentRepairPrecision.Medium,
            UseLocalCalibration = UseLocalCalibration,
'@ @'
            RepairPrecision = Enum.IsDefined(RepairPrecision) ? RepairPrecision : BrowserAgentRepairPrecision.Medium,
            RepairTimeLimitSeconds = RepairTimeLimitSeconds < 0 ? -1 : Math.Clamp(RepairTimeLimitSeconds, 0, 7200),
            UseLocalCalibration = UseLocalCalibration,
'@ 'RepairTimeLimitSeconds = RepairTimeLimitSeconds < 0'

Replace-One $adaptiveUi @'
    private readonly ComboBox precisionSelector = new();
    private readonly Label speedHint = new();
'@ @'
    private readonly ComboBox precisionSelector = new();
    private readonly ComboBox repairBudgetSelector = new();
    private readonly Label speedHint = new();
'@ 'repairBudgetSelector = new();'

Replace-One $adaptiveUi @'
        precisionSelector.Dock = DockStyle.Left;
        precisionSelector.Width = 145;

        speedHint.Text = "Fixed Medium";
'@ @'
        precisionSelector.Dock = DockStyle.Left;
        precisionSelector.Width = 145;

        repairBudgetSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        repairBudgetSelector.Items.AddRange(new object[] { "Time · Auto", "1 min", "3 min", "5 min", "15 min", "Unlimited" });
        repairBudgetSelector.SelectedIndex = 0;
        repairBudgetSelector.Width = 130;
        repairBudgetSelector.Dock = DockStyle.Left;

        speedHint.Text = "Fixed Medium";
'@ '"Time · Auto"'

Replace-One $adaptiveUi `
    -Old '        BuildPanel(precisionPanel, precisionSelector, precisionHint);' `
    -New '        BuildRepairPanel(precisionPanel, precisionSelector, repairBudgetSelector, precisionHint);' `
    -Marker 'BuildRepairPanel(precisionPanel'

Replace-One $adaptiveUi @'
    public BrowserAgentRepairPrecision Precision => (BrowserAgentRepairPrecision)Math.Clamp(precisionSelector.SelectedIndex, 0, 2);
    public bool UseLocalCalibration => browserMode && calibrationToggle.Checked;
'@ @'
    public BrowserAgentRepairPrecision Precision => (BrowserAgentRepairPrecision)Math.Clamp(precisionSelector.SelectedIndex, 0, 3);
    public int RepairTimeLimitSeconds => repairBudgetSelector.SelectedIndex switch
    {
        1 => 60,
        2 => 180,
        3 => 300,
        4 => 900,
        5 => -1,
        _ => 0
    };
    public bool UseLocalCalibration => browserMode && calibrationToggle.Checked;
'@ 'public int RepairTimeLimitSeconds => repairBudgetSelector.SelectedIndex switch'

Replace-One $adaptiveUi @'
        precisionSelector.SelectionChangeCommitted += (_, _) =>
        {
            LongCaptureLog.Info($"[USER_ACTION] browser-setting repairPrecision={Precision}");
        };
        calibrationToggle.CheckedChanged += (_, _) =>
'@ @'
        precisionSelector.SelectionChangeCommitted += (_, _) =>
        {
            UpdatePrecisionHint();
            LongCaptureLog.Info($"[USER_ACTION] browser-setting repairPrecision={Precision}");
        };
        repairBudgetSelector.SelectionChangeCommitted += (_, _) =>
        {
            UpdatePrecisionHint();
            LongCaptureLog.Info($"[USER_ACTION] browser-setting repairTimeLimitSeconds={RepairTimeLimitSeconds}");
        };
        calibrationToggle.CheckedChanged += (_, _) =>
'@ 'browser-setting repairTimeLimitSeconds='

Replace-One $adaptiveUi @'
            RepairPrecision = Precision,
            UseLocalCalibration = UseLocalCalibration,
'@ @'
            RepairPrecision = Precision,
            RepairTimeLimitSeconds = RepairTimeLimitSeconds,
            UseLocalCalibration = UseLocalCalibration,
'@ 'RepairTimeLimitSeconds = RepairTimeLimitSeconds,'

Replace-One $adaptiveUi @'
        if (changed) ApplyPreset(startDelay, pageSettle, scrollAmountOrOverlap, browserPreScanOrNativeScrollTop);
        else UpdateHint();
'@ @'
        if (changed) ApplyPreset(startDelay, pageSettle, scrollAmountOrOverlap, browserPreScanOrNativeScrollTop);
        else UpdateHint();
        UpdatePrecisionHint();
'@ 'UpdatePrecisionHint();'

Replace-One $adaptiveUi @'
    private static void BuildPanel(TableLayoutPanel panel, Control selector, Control hint)
    {
'@ @'
    private static void BuildRepairPanel(TableLayoutPanel panel, Control selector, Control budget, Control hint)
    {
        panel.Dock = DockStyle.Fill;
        panel.ColumnCount = 3;
        panel.RowCount = 1;
        panel.Margin = new Padding(0);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(selector, 0, 0);
        panel.Controls.Add(budget, 1, 0);
        panel.Controls.Add(hint, 2, 0);
    }

    private void UpdatePrecisionHint()
    {
        int seconds = RepairTimeLimitSeconds == 0
            ? BrowserAgentAdaptiveProfiles.DefaultRepairTimeLimitSeconds(Precision)
            : RepairTimeLimitSeconds;
        string time = seconds < 0 ? "no time limit" : seconds >= 60 ? $"{seconds / 60} min" : $"{seconds}s";
        string attempts = Precision == BrowserAgentRepairPrecision.Perfect ? "unlimited attempts" : $"up to {BrowserAgentAdaptiveProfiles.PostReviewAttempts(Precision)} attempts/segment";
        precisionHint.Text = $"{attempts} · {time} repair budget · suspicious sections are re-captured after the fast pass.";
    }

    private static void BuildPanel(TableLayoutPanel panel, Control selector, Control hint)
    {
'@ 'private void UpdatePrecisionHint()'

# Preserve the repair time option through the v0.1.8 end-condition wrapper.
Replace-One $endUi @'
            RepairPrecision = source.RepairPrecision,
            UseLocalCalibration = source.UseLocalCalibration,
'@ @'
            RepairPrecision = source.RepairPrecision,
            RepairTimeLimitSeconds = source.RepairTimeLimitSeconds,
            UseLocalCalibration = source.UseLocalCalibration,
'@ 'RepairTimeLimitSeconds = source.RepairTimeLimitSeconds,'

# ---------------------------------------------------------------------------
# Session model: provisional/suspect/repaired states and semantic anchor evidence.
# This is the internal equivalent of the user's "transition page / marker" idea;
# the final image never receives a visible warning page unless a debug artifact is requested.
# ---------------------------------------------------------------------------
Replace-One $models @'
namespace LongCapture.Standalone;

internal sealed class BrowserAgentFrameRecord
'@ @'
namespace LongCapture.Standalone;

internal sealed class BrowserAgentDomAnchorRecord
{
    public string Key { get; set; } = string.Empty;
    public double DocumentYCss { get; set; }
    public double ViewportYCss { get; set; }
    public double HeightCss { get; set; }
}

internal sealed class BrowserAgentFrameRecord
'@ 'internal sealed class BrowserAgentDomAnchorRecord'

Replace-One $models @'
    public string RepairStatus { get; set; } = string.Empty;
    public double CalibrationConfidence { get; set; }

    // v0.1.2+:
'@ @'
    public string RepairStatus { get; set; } = string.Empty;
    public double CalibrationConfidence { get; set; }

    // v0.1.8 repair ledger: bad frames are provisional evidence, not silently trusted output.
    public string QualityState { get; set; } = "clean";
    public DateTime? SuspectMarkedUtc { get; set; }
    public int IntegrityRiskScore { get; set; }
    public string IntegrityRiskReasons { get; set; } = string.Empty;
    public double ActualScrollDeltaCss { get; set; }
    public double ExpectedScrollDeltaCss { get; set; }
    public double ScrollDeltaErrorCss { get; set; }
    public double ScrollDeltaRobustZ { get; set; }
    public int AnchorSharedCount { get; set; }
    public double AnchorMaxDocumentDriftCss { get; set; }
    public string AnchorContinuityStatus { get; set; } = string.Empty;
    public List<BrowserAgentDomAnchorRecord> SemanticAnchors { get; set; } = new();

    // v0.1.2+:
'@ 'public string QualityState { get; set; } = "clean";'

Replace-One $models @'
    public int AdaptiveRepairFailures { get; set; }
    public bool RegionSelectionRequired { get; set; }
'@ @'
    public int AdaptiveRepairFailures { get; set; }
    public int RepairReviewPasses { get; set; }
    public int RepairTimeLimitSeconds { get; set; }
    public bool RepairUnlimitedAttempts { get; set; }
    public DateTime? RepairReviewStartedUtc { get; set; }
    public DateTime? RepairReviewCompletedUtc { get; set; }
    public int UnresolvedRepairFrames { get; set; }
    public bool RegionSelectionRequired { get; set; }
'@ 'public int RepairReviewPasses { get; set; }'

# ---------------------------------------------------------------------------
# Browser data provider: collect privacy-preserving semantic anchor hashes for
# visible structural blocks. Text is normalized and hashed in the page; raw text
# is not copied to session.json.
# ---------------------------------------------------------------------------
Replace-One $worker @'
function collectDocumentState() {
'@ @'
function collectSemanticAnchorsV018(scrollY, viewportHeight) {
  const hashText = (value) => {
    let hash = 2166136261;
    const text = String(value || "");
    for (let i = 0; i < text.length; i++) {
      hash ^= text.charCodeAt(i);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(16).padStart(8, "0");
  };

  const selectors = "article,[data-post-number],[data-post-id],[data-topic-id],[id],h1,h2,h3,h4,p,pre,blockquote,li";
  const candidates = Array.from(document.querySelectorAll(selectors));
  const anchors = [];
  const seen = new Set();
  for (const element of candidates) {
    if (anchors.length >= 16) break;
    const rect = element.getBoundingClientRect();
    if (!rect || rect.height < 14 || rect.width < 40 || rect.bottom < -8 || rect.top > viewportHeight + 8) continue;
    const style = getComputedStyle(element);
    if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity || 1) <= 0.02) continue;

    const stableAttr = element.getAttribute("data-post-number") ||
      element.getAttribute("data-post-id") || element.getAttribute("data-topic-id") || element.id || "";
    const normalized = String(element.innerText || element.textContent || "").replace(/\s+/g, " ").trim().slice(0, 160);
    if (!stableAttr && normalized.length < 20) continue;
    const key = hashText(`${String(element.tagName || "").toLowerCase()}|${stableAttr}|${normalized}`);
    if (seen.has(key)) continue;
    seen.add(key);
    anchors.push({
      key,
      documentY: Math.round((rect.top + scrollY) * 100) / 100,
      viewportY: Math.round(rect.top * 100) / 100,
      height: Math.round(rect.height * 100) / 100
    });
  }
  return anchors;
}

function collectDocumentState() {
'@ 'function collectSemanticAnchorsV018(scrollY, viewportHeight)'

Replace-One $worker @'
  let elementCount = 0;
  let bestCounter = null;

  for (const element of elements) {
'@ @'
  let elementCount = 0;
  let bestCounter = null;
  const semanticAnchors = collectSemanticAnchorsV018(scrollY, viewportHeight);

  for (const element of elements) {
'@ 'const semanticAnchors = collectSemanticAnchorsV018(scrollY, viewportHeight);'

Replace-One $worker @'
    pageCounterTotal: bestCounter?.total || 0,
    pageCounterText: bestCounter?.text || "",
    stateHash:
'@ @'
    pageCounterTotal: bestCounter?.total || 0,
    pageCounterText: bestCounter?.text || "",
    semanticAnchors,
    stateHash:
'@ '    semanticAnchors,`r`n    stateHash:'

# Reset the hard-stop marker before a post-stop repair pass. This does not resume
# forward scrolling; it only allows targeted captureAt/moveTo repair commands.
Replace-One $worker @'
      case "cancel":
        result = await cancelActiveOperation(payload);
        break;
      case "begin":
'@ @'
      case "cancel":
        result = await cancelActiveOperation(payload);
        break;
      case "repairReset":
        captureCancelled = false;
        result = await executeInTarget(clearLongCaptureCancellation);
        postAgentEvent("repair-reset", {});
        break;
      case "begin":
'@ 'case "repairReset":'

# ---------------------------------------------------------------------------
# Persist anchors on both original capture and repair capture.
# ---------------------------------------------------------------------------
Replace-One $session @'
            PageCounterText = ReadOptionalString(before, "pageCounterText"),
            EstimatedFramesToLoadedEnd = estimatedRemaining
'@ @'
            PageCounterText = ReadOptionalString(before, "pageCounterText"),
            SemanticAnchors = BrowserAgentIntegrityPolicyV018.ReadAnchors(before),
            EstimatedFramesToLoadedEnd = estimatedRemaining
'@ 'SemanticAnchors = BrowserAgentIntegrityPolicyV018.ReadAnchors(before),'

Replace-One $session @'
        frame.PageCounterText = ReadOptionalString(before, "pageCounterText");
        frame.CapturedUtc = DateTime.UtcNow;
'@ @'
        frame.PageCounterText = ReadOptionalString(before, "pageCounterText");
        frame.SemanticAnchors = BrowserAgentIntegrityPolicyV018.ReadAnchors(before);
        frame.CapturedUtc = DateTime.UtcNow;
'@ 'frame.SemanticAnchors = BrowserAgentIntegrityPolicyV018.ReadAnchors(before);'

# Integrate the independent integrity ledger with adaptive risk. A suspect frame is
# deliberately marked provisional and becomes a repair candidate after the fast pass.
Replace-One $session @'
                record.RepairCandidate = adaptiveDecision.RepairCandidate ||
                    record.OverlapLeftEdgeContamination || record.OverlapRightEdgeContamination;
                if (record.RepairCandidate) manifest.AdaptiveRepairCandidates++;
'@ @'
                BrowserAgentIntegrityAssessmentV018 integrity = BrowserAgentIntegrityPolicyV018.Evaluate(manifest.Frames, record);
                record.IntegrityRiskScore = integrity.RiskScore;
                record.IntegrityRiskReasons = integrity.Reasons;
                record.ActualScrollDeltaCss = integrity.ActualDeltaCss;
                record.ExpectedScrollDeltaCss = integrity.ExpectedDeltaCss;
                record.ScrollDeltaErrorCss = integrity.DeltaErrorCss;
                record.ScrollDeltaRobustZ = integrity.DeltaRobustZ;
                record.AnchorSharedCount = integrity.SharedAnchors;
                record.AnchorMaxDocumentDriftCss = integrity.MaxAnchorDriftCss;
                record.AnchorContinuityStatus = integrity.AnchorStatus;
                record.RepairCandidate = adaptiveDecision.RepairCandidate || integrity.Suspect ||
                    record.OverlapLeftEdgeContamination || record.OverlapRightEdgeContamination;
                record.QualityState = record.RepairCandidate ? "suspect" : "clean";
                if (record.RepairCandidate)
                {
                    record.SuspectMarkedUtc ??= DateTime.UtcNow;
                    manifest.AdaptiveRepairCandidates++;
                    LongCaptureLog.Warn($"[BA_MARK] frame={record.Sequence} risk={record.IntegrityRiskScore} reasons={LongCaptureLog.OneLine(record.IntegrityRiskReasons)} " +
                        $"delta={record.ActualScrollDeltaCss:F1}/{record.ExpectedScrollDeltaCss:F1} z={record.ScrollDeltaRobustZ:F1} " +
                        $"anchors={record.AnchorSharedCount} drift={record.AnchorMaxDocumentDriftCss:F1}px");
                }
'@ '[BA_MARK] frame='

# Manual F8 now means "stop the forward pass". It must NOT bypass post-capture
# Quality Guard. Use a fresh repair token because the forward-pass token is already
# cancelled. repairReset clears the extension's hard-stop marker without resuming scroll.
Replace-One $session @'
        if (!cancelled &&
            (string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal) ||
             BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason)) &&
            adaptive.IsAdaptive)
        {
            status?.Invoke($"Adaptive quality review ({options.RepairPrecision}) — checking marked sections before final stitch...");
            int unresolvedRepairs = await RunAdaptivePostReviewAsync(
                sessionDirectory,
                manifest,
                options,
                status,
                cancellationToken).ConfigureAwait(false);
'@ @'
        if (adaptive.IsAdaptive && manifest.Frames.Count >= 3 &&
            (cancelled || string.Equals(stopReason, "document-bottom-confirmed", StringComparison.Ordinal) ||
             BrowserAgentStopPolicyV018.IsRequestedEnd(stopReason)))
        {
            CancellationToken repairToken = cancelled ? CancellationToken.None : cancellationToken;
            if (cancelled)
            {
                status?.Invoke("Forward capture stopped by F8. Running Quality Guard on marked sections before stitching...");
                try
                {
                    await bridge.SendRequestAsync("repairReset", new { }, TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LongCaptureLog.Warn($"[BA_REPAIR] repair-reset failed: {LongCaptureLog.OneLine(ex.Message)}");
                }
            }
            status?.Invoke($"Adaptive quality review ({options.RepairPrecision}) — checking marked sections before final stitch...");
            int unresolvedRepairs = await RunAdaptivePostReviewAsync(
                sessionDirectory,
                manifest,
                options,
                status,
                repairToken).ConfigureAwait(false);
'@ 'Forward capture stopped by F8. Running Quality Guard'

# Add time-bounded / attempt-unbounded Perfect behavior to the existing local repair loop.
Replace-One $session @'
        int budget = BrowserAgentAdaptiveProfiles.PostReviewBudget(options.RepairPrecision);
        int attempts = BrowserAgentAdaptiveProfiles.PostReviewAttempts(options.RepairPrecision);
        List<int> candidates = manifest.Frames
'@ @'
        int budget = BrowserAgentAdaptiveProfiles.PostReviewBudget(options.RepairPrecision);
        int attempts = BrowserAgentAdaptiveProfiles.PostReviewAttempts(options.RepairPrecision);
        bool unlimitedAttempts = BrowserAgentAdaptiveProfiles.UnlimitedRepairAttempts(options.RepairPrecision);
        int timeLimitSeconds = options.RepairTimeLimitSeconds == 0
            ? BrowserAgentAdaptiveProfiles.DefaultRepairTimeLimitSeconds(options.RepairPrecision)
            : options.RepairTimeLimitSeconds;
        DateTime repairStartedUtc = DateTime.UtcNow;
        DateTime? repairDeadlineUtc = timeLimitSeconds < 0 ? null : repairStartedUtc.AddSeconds(timeLimitSeconds);
        manifest.RepairReviewStartedUtc = repairStartedUtc;
        manifest.RepairTimeLimitSeconds = timeLimitSeconds;
        manifest.RepairUnlimitedAttempts = unlimitedAttempts;
        manifest.RepairReviewPasses++;
        List<int> candidates = manifest.Frames
'@ 'manifest.RepairReviewStartedUtc = repairStartedUtc;'

Replace-One $session @'
        LongCaptureLog.Info($"[BA_REPAIR] post-review candidates={candidates.Count} attemptsPerCandidate={attempts} precision={options.RepairPrecision}");
'@ @'
        LongCaptureLog.Info($"[BA_REPAIR] post-review candidates={candidates.Count} attemptsPerCandidate={(unlimitedAttempts ? "unlimited" : attempts)} precision={options.RepairPrecision} timeLimitSeconds={timeLimitSeconds}");
'@ 'timeLimitSeconds={timeLimitSeconds}'

Replace-One $session @'
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                manifest.AdaptiveRepairAttempts++;
                frame.RepairAttempts++;
                status?.Invoke($"Repair review: frame {frame.Sequence}, attempt {attempt}/{attempts}...");
                LongCaptureLog.Info($"[BA_REPAIR] frame={frame.Sequence} phase=recapture attempt={attempt}/{attempts} y={frame.ScrollYCss:F0}");
'@ @'
            for (int attempt = 1; unlimitedAttempts || attempt <= attempts; attempt++)
            {
                if (repairDeadlineUtc.HasValue && DateTime.UtcNow >= repairDeadlineUtc.Value)
                {
                    LongCaptureLog.Warn($"[BA_REPAIR] time budget reached before frame={frame.Sequence} attempt={attempt}");
                    break;
                }
                manifest.AdaptiveRepairAttempts++;
                frame.RepairAttempts++;
                string attemptLabel = unlimitedAttempts ? $"{attempt}/∞" : $"{attempt}/{attempts}";
                status?.Invoke($"Repair review: frame {frame.Sequence}, attempt {attemptLabel}...");
                LongCaptureLog.Info($"[BA_REPAIR] frame={frame.Sequence} phase=recapture attempt={attemptLabel} y={frame.ScrollYCss:F0}");
'@ 'string attemptLabel = unlimitedAttempts ?'

Replace-One $session @'
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
'@ @'
            if (repaired)
            {
                frame.RepairStatus = "post-review-ok";
                frame.QualityState = "repaired";
                manifest.AdaptiveRepairSuccesses++;
            }
            else
            {
                frame.RepairStatus = "post-review-unresolved";
                frame.QualityState = "unresolved";
                manifest.AdaptiveRepairFailures++;
                unresolved++;
            }
'@ 'frame.QualityState = "repaired";'

Replace-One $session @'
        return unresolved;
    }

    private static void ApplyStability(BrowserAgentFrameRecord record, JsonElement response)
'@ @'
        manifest.UnresolvedRepairFrames = unresolved;
        manifest.RepairReviewCompletedUtc = DateTime.UtcNow;
        return unresolved;
    }

    private static void ApplyStability(BrowserAgentFrameRecord record, JsonElement response)
'@ 'manifest.UnresolvedRepairFrames = unresolved;'

# If manual stop repair succeeds, result remains Partial because the requested document range
# was intentionally stopped early; if repair is unresolved, the reason must say so explicitly.
Replace-One $session @'
            if (unresolvedRepairs > 0)
            {
                stopReason = "quality-review-unresolved";
                status?.Invoke($"Adaptive quality review left {unresolvedRepairs} suspicious section(s); result will be marked Partial.");
            }
'@ @'
            if (unresolvedRepairs > 0)
            {
                stopReason = cancelled ? "manual-stop-quality-unresolved" : "quality-review-unresolved";
                status?.Invoke($"Adaptive quality review left {unresolvedRepairs} suspicious section(s); result will be marked Partial with unresolved quality evidence.");
            }
            else if (cancelled)
            {
                stopReason = "manual-stop-repaired";
                status?.Invoke("Manual-stop range quality review completed: all marked sections were repaired/verified.");
            }
'@ 'manual-stop-repaired'

# Self-test must cover the integrity policy and Perfect semantics before packaging.
Replace-One $selftest @'
            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
                Console.Error.WriteLine("Browser Agent self-test failed adaptive policy.");
                return 64;
            }
'@ @'
            if (!BrowserAgentAdaptiveProfiles.SelfTest())
            {
                Console.Error.WriteLine("Browser Agent self-test failed adaptive policy.");
                return 64;
            }
            if (!BrowserAgentIntegrityPolicyV018.SelfTest() ||
                !BrowserAgentAdaptiveProfiles.UnlimitedRepairAttempts(BrowserAgentRepairPrecision.Perfect) ||
                BrowserAgentAdaptiveProfiles.DefaultRepairTimeLimitSeconds(BrowserAgentRepairPrecision.Perfect) < 600)
            {
                Console.Error.WriteLine("Browser Agent self-test failed v0.1.8 integrity/Perfect repair policy.");
                return 68;
            }
'@ 'self-test failed v0.1.8 integrity/Perfect repair policy'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.8 repair-ledger r3 compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.8 repair-ledger r3 applied: semantic anchors, suspect-frame ledger, manual-stop repair, Perfect unlimited attempts with a separate time budget." -ForegroundColor Green
}
