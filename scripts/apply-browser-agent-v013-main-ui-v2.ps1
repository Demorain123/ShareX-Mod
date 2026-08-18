[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"

function Replace-One {
    param([string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($main)
    if ($text.Contains($Marker)) { Write-Host "[v0.1.3-ui] already: $Marker" -ForegroundColor DarkYellow; return }
    if (-not $text.Contains($Old)) { throw "v0.1.3 UI anchor missing: $Marker" }
    Write-Host "[v0.1.3-ui] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($main, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[v0.1.3-ui] applied: $Marker" -ForegroundColor Cyan
    }
}

Replace-One '    private readonly BrowserAgentIntegratedController browserAgentController = new();' @'
    private readonly BrowserAgentIntegratedController browserAgentController = new();
    private readonly BrowserAgentUiModeAdapterV013 browserAgentUiAdapter = new();
    private TableLayoutPanel mainBody = null!;
'@ 'BrowserAgentUiModeAdapterV013 browserAgentUiAdapter'

Replace-One @'
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
'@ @'
        mainBody = body;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
'@ 'mainBody = body;'

Replace-One '        targetStrip.Controls.Add(openLogsButton, 4, 0);' @'
        targetStrip.Controls.Add(openLogsButton, 4, 0);
        BrowserAgentUiModeAdapterV013.ReflowTargetStrip(
            targetStrip, targetLabel, targetSelector, foregroundTargetButton, refreshTargetsButton, openLogsButton);
'@ 'BrowserAgentUiModeAdapterV013.ReflowTargetStrip('

Replace-One @'
        Controls.Add(body);
        Controls.Add(targetStrip);
'@ @'
        BrowserAgentUiModeAdapterV013.Polish(this, body);
        Controls.Add(body);
        Controls.Add(targetStrip);
'@ 'BrowserAgentUiModeAdapterV013.Polish(this, body);'

Replace-One @'
        if (BrowserAgentSelected)
        {
            browserButton.Text = "Open Browser Agent folder";
'@ @'
        if (BrowserAgentSelected)
        {
            browserAgentUiAdapter.SetMode(
                true, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
                autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
            browserButton.Text = "Open Browser Agent folder";
'@ 'true, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,'

Replace-One @'
        targetSelector.Enabled = !captureBusy;
        foregroundTargetButton.Enabled = !captureBusy;
        refreshTargetsButton.Enabled = !captureBusy;
        startDelay.Enabled = !captureBusy;
        scrollDelay.Enabled = !captureBusy;
        scrollAmount.Enabled = !captureBusy;
        scrollMethod.Enabled = !captureBusy;
        autoScrollTop.Enabled = !captureBusy;
        wholeWindowCapture.Enabled = !captureBusy;
        debugCaptureUi.Enabled = !captureBusy;
        includeInternalDebugWindows.Enabled = !captureBusy && debugCaptureUi.Checked;
        outputLabel.Text = outputDirectory;
'@ @'
        browserAgentUiAdapter.SetMode(
            false, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
            autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
        targetSelector.Enabled = !captureBusy;
        foregroundTargetButton.Enabled = !captureBusy;
        refreshTargetsButton.Enabled = !captureBusy;
        startDelay.Enabled = !captureBusy;
        scrollDelay.Enabled = !captureBusy;
        scrollAmount.Enabled = !captureBusy;
        scrollMethod.Enabled = !captureBusy;
        autoScrollTop.Enabled = !captureBusy;
        wholeWindowCapture.Enabled = !captureBusy;
        debugCaptureUi.Enabled = !captureBusy;
        includeInternalDebugWindows.Enabled = !captureBusy && debugCaptureUi.Checked;
        outputLabel.Text = outputDirectory;
'@ 'false, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,'

Replace-One @'
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(message =>
            {
'@ @'
            BrowserAgentCaptureOptions browserOptions = browserAgentUiAdapter.BuildOptions(
                startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture);
            LongCaptureLog.Info(
                $"Browser Agent v0.1.3 options startDelay={browserOptions.StartDelayMs} settle={browserOptions.StableWindowMs} overlap={browserOptions.OverlapRatio:P0} preload={browserOptions.PreloadDynamicContent} regionSelect={browserOptions.RequireRegionSelection}");
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(browserOptions, message =>
            {
'@ 'Browser Agent v0.1.3 options startDelay='

foreach ($pair in @(
    @('        startDelay.Enabled = enabled && !BrowserAgentSelected;', '        startDelay.Enabled = enabled; // v0.1.3 Browser option'),
    @('        scrollDelay.Enabled = enabled && !BrowserAgentSelected;', '        scrollDelay.Enabled = enabled; // v0.1.3 Browser option'),
    @('        scrollAmount.Enabled = enabled && !BrowserAgentSelected;', '        scrollAmount.Enabled = enabled; // v0.1.3 Browser option'),
    @('        autoScrollTop.Enabled = enabled && !BrowserAgentSelected;', '        autoScrollTop.Enabled = enabled; // v0.1.3 Browser option')
)) { Replace-One $pair[0] $pair[1] $pair[1].Trim() }

if ($CheckOnly) { Write-Host "Browser Agent v0.1.3 main UI compatibility passed." -ForegroundColor Green }
else { Write-Host "Browser Agent v0.1.3 responsive main UI applied." -ForegroundColor Green }
