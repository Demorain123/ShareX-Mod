[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) { Write-Host "[BrowserAgent-v0.1.4-log] already: $Marker" -ForegroundColor DarkYellow; return }
    if (-not $text.Contains($Old)) { throw "Browser Agent v0.1.4 operation-log anchor missing: $Marker" }
    Write-Host "[BrowserAgent-v0.1.4-log] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.4-log] applied: $Marker" -ForegroundColor Cyan
    }
}

Replace-One $main @'
        captureButton.Click += async (_, _) => await ToggleCaptureAsync();
'@ @'
        captureButton.Click += async (_, _) =>
        {
            LongCaptureLog.Info($"[USER_ACTION] capture-button clicked browserMode={BrowserAgentSelected} busy={captureBusy}");
            await ToggleCaptureAsync();
        };

        startDelay.ValueChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting startDelayMs={startDelay.Value}");
        };
        scrollDelay.ValueChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting pageSettleMs={scrollDelay.Value}");
        };
        scrollAmount.ValueChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting overlapPercent={scrollAmount.Value}");
        };
        autoScrollTop.CheckedChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting optionalPreScan={autoScrollTop.Checked}");
        };
        wholeWindowCapture.CheckedChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting fullViewport={wholeWindowCapture.Checked}");
        };
        debugCaptureUi.CheckedChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting debugGui={debugCaptureUi.Checked}");
        };
        includeInternalDebugWindows.CheckedChanged += (_, _) =>
        {
            if (BrowserAgentSelected) LongCaptureLog.Info($"[USER_ACTION] browser-setting debugInternalWindows={includeInternalDebugWindows.Checked}");
        };
'@ '[USER_ACTION] capture-button clicked'

Replace-One $worker @'
  return {
    ...captured,
    after,
    stabilityAfter,
    warmupTriggered: warmup.triggered,
'@ @'
  postAgentEvent("capture-scroll", {
    fromY: Math.round(captured.before.scrollY || 0),
    toY: Math.round(after.scrollY || 0),
    scrollHeight: Math.round(after.scrollHeight || captured.before.scrollHeight || 0),
    atBottom,
    captureStateChanged: captured.captureStateChanged === true,
    hiddenCount: Number(captured.hiddenCount || 0),
    pageCurrent: captured.before.pageCounterCurrent || 0,
    pageTotal: captured.before.pageCounterTotal || 0
  });

  return {
    ...captured,
    after,
    stabilityAfter,
    warmupTriggered: warmup.triggered,
'@ 'postAgentEvent("capture-scroll"'

if ($CheckOnly) { Write-Host "Browser Agent v0.1.4 operation timeline compatibility passed." -ForegroundColor Green }
else { Write-Host "Browser Agent v0.1.4 user-action and scroll-step telemetry applied." -ForegroundColor Green }
