[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.6 target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[LongCapture-v0.1.6] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "v0.1.6 compatibility anchor not found: '$Marker' in $Path"
    }

    Write-Host "[LongCapture-v0.1.6] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
        Write-Host "[LongCapture-v0.1.6] applied: $Marker" -ForegroundColor Cyan
    }
}

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$program = Join-Path $repoRoot "LongCapture.Standalone\Program.cs"
$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
$qualitySummary = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModFinalQualitySummary.cs"

# --- Debug UI policy ---------------------------------------------------------
# Normal Debug intentionally exposes only the real LongCapture GUI/HUD while the temporary
# selector/Avalonia/helper top-level windows stay WDA_EXCLUDEFROMCAPTURE. A second explicit
# checkbox lets the user include those internal windows when diagnosing window-host behaviour.
Replace-Literal -Path $main `
    -Old @'
    private readonly CheckBox wholeWindowCapture = new();
    private readonly CheckBox debugCaptureUi = new();
    private readonly Label outputLabel = new();
'@ `
    -New @'
    private readonly CheckBox wholeWindowCapture = new();
    private readonly CheckBox debugCaptureUi = new();
    private readonly CheckBox includeInternalDebugWindows = new();
    private readonly Label outputLabel = new();
'@ `
    -Marker 'private readonly CheckBox includeInternalDebugWindows = new();'

Replace-Literal -Path $main `
    -Old @'
        debugCaptureUi.Text = "Debug: safe GUI/settings snapshot + evidence";
        debugCaptureUi.AutoSize = true;
        debugCaptureUi.Checked = false;
        debugCaptureUi.CheckedChanged += (_, _) => CaptureExclusion.SetDebugCaptureUi(debugCaptureUi.Checked, "main-form-checkbox");

        outputLabel.Text = outputDirectory;
'@ `
    -New @'
        debugCaptureUi.Text = "Debug: GUI + raw replay evidence";
        debugCaptureUi.AutoSize = true;
        debugCaptureUi.Checked = false;
        debugCaptureUi.CheckedChanged += (_, _) =>
        {
            CaptureExclusion.SetDebugCaptureUi(debugCaptureUi.Checked, "main-form-checkbox");
            ShareXModReplayDiagnostics.Configure(debugCaptureUi.Checked);
            includeInternalDebugWindows.Enabled = debugCaptureUi.Checked;
            if (!debugCaptureUi.Checked && includeInternalDebugWindows.Checked)
            {
                includeInternalDebugWindows.Checked = false;
            }
        };

        includeInternalDebugWindows.Text = "Debug: include internal helper/selector/Avalonia windows";
        includeInternalDebugWindows.AutoSize = true;
        includeInternalDebugWindows.Checked = false;
        includeInternalDebugWindows.Enabled = false;
        includeInternalDebugWindows.CheckedChanged += (_, _) =>
            CaptureExclusion.SetIncludeInternalDebugWindows(
                debugCaptureUi.Checked && includeInternalDebugWindows.Checked,
                "main-form-internal-window-checkbox");

        outputLabel.Text = outputDirectory;
'@ `
    -Marker 'includeInternalDebugWindows.Text = "Debug: include internal helper/selector/Avalonia windows";'

Replace-Literal -Path $main `
    -Old @'
        captureBehaviorPanel.Controls.Add(autoScrollTop);
        captureBehaviorPanel.Controls.Add(wholeWindowCapture);
        captureBehaviorPanel.Controls.Add(debugCaptureUi);
        AddRow(body, 9, "Capture behavior", captureBehaviorPanel);
'@ `
    -New @'
        captureBehaviorPanel.Controls.Add(autoScrollTop);
        captureBehaviorPanel.Controls.Add(wholeWindowCapture);
        captureBehaviorPanel.Controls.Add(debugCaptureUi);
        captureBehaviorPanel.Controls.Add(includeInternalDebugWindows);
        AddRow(body, 9, "Capture behavior", captureBehaviorPanel);
        body.RowStyles[9].Height = 72;
'@ `
    -Marker 'captureBehaviorPanel.Controls.Add(includeInternalDebugWindows);'

Replace-Literal -Path $main `
    -Old @'
        CaptureExclusion.SetDebugCaptureUi(debugCaptureUi.Checked, "capture-request");
        if (debugCaptureUi.Checked)
        {
'@ `
    -New @'
        CaptureExclusion.SetDebugCaptureUi(debugCaptureUi.Checked, "capture-request");
        CaptureExclusion.SetIncludeInternalDebugWindows(
            debugCaptureUi.Checked && includeInternalDebugWindows.Checked,
            "capture-request-internal-window-policy");
        ShareXModReplayDiagnostics.Configure(debugCaptureUi.Checked);
        if (debugCaptureUi.Checked)
        {
'@ `
    -Marker 'capture-request-internal-window-policy'

Replace-Literal -Path $main `
    -Old @'
                WholeWindow = wholeWindowCapture.Checked,
                DebugCaptureUi = debugCaptureUi.Checked,
                Displayed = new
'@ `
    -New @'
                WholeWindow = wholeWindowCapture.Checked,
                DebugCaptureUi = debugCaptureUi.Checked,
                IncludeInternalDebugWindows = includeInternalDebugWindows.Checked,
                RawFrameReplayEvidence = ShareXModReplayDiagnostics.Enabled,
                Displayed = new
'@ `
    -Marker 'RawFrameReplayEvidence = ShareXModReplayDiagnostics.Enabled,'

Replace-Literal -Path $main `
    -Old @'
        autoScrollTop.Enabled = enabled;
        wholeWindowCapture.Enabled = enabled;
        debugCaptureUi.Enabled = enabled;
        if (enabled)
'@ `
    -New @'
        autoScrollTop.Enabled = enabled;
        wholeWindowCapture.Enabled = enabled;
        debugCaptureUi.Enabled = enabled;
        includeInternalDebugWindows.Enabled = enabled && debugCaptureUi.Checked;
        if (enabled)
'@ `
    -Marker 'includeInternalDebugWindows.Enabled = enabled && debugCaptureUi.Checked;'

# --- Raw frame recorder + compositor ----------------------------------------
Replace-Literal -Path $manager `
    -Old @'
                        modRobustSession?.OnFrameCaptured(lastScreenshot);
                        modQualityGuard?.OnFrameCaptured(lastScreenshot);
'@ `
    -New @'
                        modRobustSession?.OnFrameCaptured(lastScreenshot);
                        modQualityGuard?.OnFrameCaptured(lastScreenshot);
                        ShareXModReplayDiagnostics.RecordRawFrame(lastScreenshot, selectedRectangle, Options);
'@ `
    -Marker 'ShareXModReplayDiagnostics.RecordRawFrame(lastScreenshot, selectedRectangle, Options);'

# v0.1.4/v0.1.5 mutated the already-composited Result and pre-cleaned the current frame. That loses
# the clean evidence needed to decide whether a changing button/card is fixed or document-relative.
# v0.1.6 keeps both neighbouring raw frames intact, performs classification there, and only then
# writes repairs onto a new output surface. Legacy CombineImagesAsync remains a fail-open fallback.
Replace-Literal -Path $manager `
    -Old @'
                            Bitmap modCombineImage = lastScreenshot;
                            Bitmap modCleanedImage = null;
                            ShareXModAnchorMatch modAnchor = default;
                            bool modHasAnchor =
                                modRobustSession != null &&
                                previousScreenshot != null &&
                                ShareXModAnchorMatcher.TryEstimateScrollDelta(
                                    previousScreenshot,
                                    lastScreenshot,
                                    out modAnchor);

                            int modRepairedOverlayTiles = 0;

                            if (modHasAnchor && Result != null)
                            {
                                modRepairedOverlayTiles +=
                                    ShareXModStaticOverlayCleaner.TryRepairPreviousResultTail(
                                        Result,
                                        previousScreenshot,
                                        lastScreenshot,
                                        modAnchor.ScrollDelta);
                            }

                            if (modHasAnchor)
                            {
                                ShareXModOverlayCleanResult? modOverlayClean =
                                    ShareXModStaticOverlayCleaner.TryClean(
                                        previousScreenshot,
                                        lastScreenshot,
                                        modAnchor.ScrollDelta);

                                if (modOverlayClean is ShareXModOverlayCleanResult cleaned)
                                {
                                    modCleanedImage = cleaned.Image;
                                    modCombineImage = cleaned.Image;
                                    modRepairedOverlayTiles = cleaned.RepairedTiles;
                                }
                            }

                            bool modV014AnchorCompositorUsed = false;
                            Bitmap newResult = null;
                            if (modHasAnchor && Result != null)
                            {
                                newResult = ShareXModAnchorCompositorV014.TryAppend(Result, modCombineImage, modAnchor.ScrollDelta);
                                modV014AnchorCompositorUsed = newResult != null;
                            }
                            if (newResult == null)
                            {
                                newResult = await CombineImagesAsync(Result, modCombineImage);
                            }
'@ `
    -New @'
                            Bitmap modCombineImage = lastScreenshot;
                            Bitmap modCleanedImage = null;
                            ShareXModAnchorMatch modAnchor = default;
                            bool modHasAnchor =
                                modRobustSession != null &&
                                previousScreenshot != null &&
                                ShareXModAnchorMatcher.TryEstimateScrollDelta(
                                    previousScreenshot,
                                    lastScreenshot,
                                    out modAnchor);

                            ShareXModReplayDiagnostics.RecordAnchor(modHasAnchor, modAnchor);

                            int modRepairedOverlayTiles = 0;
                            bool modV016DelayedCompositorUsed = false;
                            Bitmap newResult = null;
                            if (modHasAnchor && Result != null && previousScreenshot != null)
                            {
                                ShareXModV016Telemetry modV016Before = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
                                newResult = ShareXModDelayedCompositorV016.TryAppendLive(
                                    Result,
                                    previousScreenshot,
                                    lastScreenshot,
                                    modAnchor.ScrollDelta);
                                modV016DelayedCompositorUsed = newResult != null;
                                if (modV016DelayedCompositorUsed)
                                {
                                    ShareXModV016Telemetry modV016After = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
                                    int repairedPixels = Math.Max(0, modV016After.RepairedPixelsApprox - modV016Before.RepairedPixelsApprox);
                                    modRepairedOverlayTiles = repairedPixels <= 0 ? 0 : Math.Max(1, repairedPixels / (32 * 24));
                                }
                            }
                            if (newResult == null)
                            {
                                newResult = await CombineImagesAsync(Result, lastScreenshot);
                            }
'@ `
    -Marker 'bool modV016DelayedCompositorUsed = false;'

# --- Quality summary ---------------------------------------------------------
# One newest tail occurrence is deliberately retained when the user manually stops because there is
# no future raw frame to reveal the pixels behind it. It must therefore not be called a pristine
# clean/high result. Low-overlap captures are also downgraded because delayed repair evidence is weak.
Replace-Literal -Path $qualitySummary `
    -Old @'
            int semanticRangeCount = 0;
'@ `
    -New @'
            ShareXModV016Telemetry delayedCompositor = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();
            if (delayedCompositor.LowOverlapRisk)
            {
                status = "unresolved";
                confidence = "low";
            }
            else if (delayedCompositor.PendingTailComponents > 0 &&
                     string.Equals(status, "clean", StringComparison.OrdinalIgnoreCase))
            {
                status = "partially-repaired";
                confidence = "medium";
            }

            int semanticRangeCount = 0;
'@ `
    -Marker 'ShareXModV016Telemetry delayedCompositor = ShareXModDelayedCompositorV016.SnapshotLiveTelemetry();'

Replace-Literal -Path $qualitySummary `
    -Old @'
                semanticEvidence = new
                {
                    semanticAdditionalRangeCount = semanticRangeCount
                },
'@ `
    -New @'
                semanticEvidence = new
                {
                    semanticAdditionalRangeCount = semanticRangeCount
                },
                delayedCompositor = new
                {
                    delayedCompositor.AppendCount,
                    delayedCompositor.DetectedComponents,
                    delayedCompositor.RepairedComponents,
                    delayedCompositor.RepairedPixelsApprox,
                    delayedCompositor.PendingTailComponents,
                    delayedCompositor.LatestScrollDelta,
                    delayedCompositor.LowOverlapRisk
                },
'@ `
    -Marker 'delayedCompositor.PendingTailComponents,'

# --- Offline replay CLI ------------------------------------------------------
Replace-Literal -Path $program `
    -Old @'
        InitializeDesktopUiHosts();
        using var exclusionWatcher = CaptureExclusionWatcher.Start();
'@ `
    -New @'
        int replayIndex = Array.FindIndex(args, x => string.Equals(x, "--replay", StringComparison.OrdinalIgnoreCase));
        string? replayDirectory = replayIndex >= 0 && replayIndex + 1 < args.Length
            ? args[replayIndex + 1]
            : args.FirstOrDefault(x => x.StartsWith("--replay=", StringComparison.OrdinalIgnoreCase))?
                .Substring("--replay=".Length)
                .Trim('"');
        if (!string.IsNullOrWhiteSpace(replayDirectory))
        {
            try
            {
                string replayOutput = ShareXModOfflineReplay.Replay(replayDirectory);
                LongCaptureLog.Info($"offline replay completed input={LongCaptureLog.OneLine(replayDirectory)} output={LongCaptureLog.OneLine(replayOutput)}");
                return 0;
            }
            catch (Exception ex)
            {
                LongCaptureLog.Error($"offline replay failed input={LongCaptureLog.OneLine(replayDirectory)}", ex);
                return 31;
            }
        }

        InitializeDesktopUiHosts();
        using var exclusionWatcher = CaptureExclusionWatcher.Start();
'@ `
    -Marker 'offline replay completed input='

# QUICK must exercise the exact raw-frame compositor and disk-backed replay path.
Replace-Literal -Path $automation `
    -Old '        "ShareX.ScreenCaptureLib.ShareXModV014CompositorSelfTests",' `
    -New @'
        "ShareX.ScreenCaptureLib.ShareXModV014CompositorSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV016ReplaySelfTests",
'@ `
    -Marker '"ShareX.ScreenCaptureLib.ShareXModV016ReplaySelfTests",'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.6 replay/compositor hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.6 replay/compositor hooks applied." -ForegroundColor Green
}
