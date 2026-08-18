[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$mainForm = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "Browser Agent v0.1.2 target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.2] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.2 compatibility anchor not found: '$Marker' in $Path"
    }

    Write-Host "[BrowserAgent-v0.1.2] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $updated = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.2] applied: $Marker" -ForegroundColor Cyan
    }
}

# Keep this integration post-RC6 and anchor-small. The RC overlays intentionally
# rename Advanced modes and add F7/debug controls, so do not replace whole UI blocks.
Replace-Literal -Path $mainForm `
    -Old @'
    private readonly ToolStripMenuItem trayStopItem = new("Stop capture (F8)");

    private ScrollingCaptureService? activeService;
'@ `
    -New @'
    private readonly ToolStripMenuItem trayStopItem = new("Stop capture (F8)");
    private readonly BrowserAgentIntegratedController browserAgentController = new();

    private ScrollingCaptureService? activeService;
'@ `
    -Marker 'BrowserAgentIntegratedController browserAgentController'

Replace-Literal -Path $mainForm `
    -Old '            "Normal Long Capture",' `
    -New @'
            "Normal Long Capture",
            "Browser Assisted Capture",
'@ `
    -Marker '"Browser Assisted Capture"'

Replace-Literal -Path $mainForm `
    -Old '        browserButton.Click += async (_, _) => await LaunchCaptureBrowserAsync();' `
    -New '        browserButton.Click += async (_, _) => await HandleBrowserActionAsync();' `
    -Marker 'HandleBrowserActionAsync()'

Replace-Literal -Path $mainForm `
    -Old @'
        Shown += async (_, _) =>
        {
'@ `
    -New @'
        browserAgentController.StateChanged += OnBrowserAgentStateChanged;

        Shown += async (_, _) =>
        {
'@ `
    -Marker 'browserAgentController.StateChanged += OnBrowserAgentStateChanged'

Replace-Literal -Path $mainForm `
    -Old '            activeService?.Dispose();' `
    -New @'
            activeService?.Dispose();
            browserAgentController.Dispose();
'@ `
    -Marker 'browserAgentController.Dispose();'

Replace-Literal -Path $mainForm `
    -Old @'
    private LongCaptureStandaloneMode SelectedMode => modeSelector.SelectedIndex switch
    {
        1 => LongCaptureStandaloneMode.SmartWeb,
        2 => LongCaptureStandaloneMode.Teach,
        3 => LongCaptureStandaloneMode.RunRecipe,
        _ => LongCaptureStandaloneMode.Normal
    };
'@ `
    -New @'
    private bool BrowserAgentSelected => modeSelector.SelectedIndex == 1;

    private LongCaptureStandaloneMode SelectedMode => modeSelector.SelectedIndex switch
    {
        2 => LongCaptureStandaloneMode.SmartWeb,
        3 => LongCaptureStandaloneMode.Teach,
        4 => LongCaptureStandaloneMode.RunRecipe,
        _ => LongCaptureStandaloneMode.Normal
    };
'@ `
    -Marker 'private bool BrowserAgentSelected => modeSelector.SelectedIndex == 1;'

Replace-Literal -Path $mainForm `
    -Old @'
    private async Task RefreshModeUiAsync()
    {
        LongCaptureStandaloneMode mode = SelectedMode;
'@ `
    -New @'
    private async Task RefreshModeUiAsync()
    {
        if (BrowserAgentSelected)
        {
            browserButton.Text = "Open Browser Agent folder";
            browserButton.Enabled = !captureBusy;
            recipePath.Enabled = false;
            browseRecipeButton.Enabled = false;
            reviewButton.Enabled = false;
            targetSelector.Enabled = false;
            foregroundTargetButton.Enabled = false;
            refreshTargetsButton.Enabled = false;
            startDelay.Enabled = false;
            scrollDelay.Enabled = false;
            scrollAmount.Enabled = false;
            scrollMethod.Enabled = false;
            autoScrollTop.Enabled = false;
            wholeWindowCapture.Enabled = false;
            debugCaptureUi.Enabled = false;
            includeInternalDebugWindows.Enabled = false;
            outputLabel.Text = Path.Combine(AppContext.BaseDirectory, "BrowserAgentCaptures");
            if (!captureBusy) captureButton.Text = "Start Browser Assisted Capture   (F8)";
            readinessLabel.Text = browserAgentController.IsConnected
                ? "Ready · " + browserAgentController.AttachedSummary
                : "Not ready · Activate the target Chromium/Helium tab and click the LongCapture extension (or Ctrl+Shift+L).";
            LongCaptureStandaloneBridge.ConfigureMode(LongCaptureStandaloneMode.Normal);
            LongCaptureLog.Info($"mode configured BrowserAssisted connected={browserAgentController.IsConnected} target={LongCaptureLog.OneLine(browserAgentController.AttachedSummary)}");
            await Task.CompletedTask;
            return;
        }

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
        browserButton.Text = "Open Capture Browser";
        if (!captureBusy) captureButton.Text = "Start long capture   (F8)";
        LongCaptureStandaloneMode mode = SelectedMode;
'@ `
    -Marker 'mode configured BrowserAssisted'

Replace-Literal -Path $mainForm `
    -Old @'
    private async Task<bool> RefreshReadinessAsync(bool showDialogOnFailure)
    {
        LongCaptureStandaloneMode mode = SelectedMode;
'@ `
    -New @'
    private async Task<bool> RefreshReadinessAsync(bool showDialogOnFailure)
    {
        if (BrowserAgentSelected)
        {
            bool ready = browserAgentController.IsConnected;
            readinessLabel.Text = ready
                ? "Ready · " + browserAgentController.AttachedSummary
                : "Not ready · Attach the target Chromium/Helium tab with the LongCapture extension (or Ctrl+Shift+L).";
            if (!ready && showDialogOnFailure)
            {
                MessageBox.Show(this,
                    "Activate the target Chromium/Helium tab, then click the LongCapture Browser Agent extension icon (or press Ctrl+Shift+L). After it shows as attached, press F8 to start.",
                    "Browser Assisted Capture is not attached",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            await Task.CompletedTask;
            return ready;
        }

        LongCaptureStandaloneMode mode = SelectedMode;
'@ `
    -Marker 'Browser Assisted Capture is not attached'

Replace-Literal -Path $mainForm `
    -Old @'
    private async Task LaunchCaptureBrowserAsync()
    {
'@ `
    -New @'
    private async Task HandleBrowserActionAsync()
    {
        if (BrowserAgentSelected)
        {
            OpenBrowserAgentFolder();
            await RefreshReadinessAsync(showDialogOnFailure: false);
            return;
        }
        await LaunchCaptureBrowserAsync();
    }

    private async Task LaunchCaptureBrowserAsync()
    {
'@ `
    -Marker 'private async Task HandleBrowserActionAsync()'

Replace-Literal -Path $mainForm `
    -Old @'
        if (captureBusy)
        {
            RequestStop();
            return;
        }

        LongCaptureStandaloneMode mode = SelectedMode;
'@ `
    -New @'
        if (captureBusy)
        {
            RequestStop();
            return;
        }

        if (BrowserAgentSelected)
        {
            await RunBrowserAssistedCaptureAsync();
            return;
        }

        LongCaptureStandaloneMode mode = SelectedMode;
'@ `
    -Marker 'await RunBrowserAssistedCaptureAsync();'

Replace-Literal -Path $mainForm `
    -Old @'
    private void UpdateQualityLabel()
    {
'@ `
    -New @'
    private async Task RunBrowserAssistedCaptureAsync()
    {
        if (!await RefreshReadinessAsync(showDialogOnFailure: true)) return;

        captureBusy = true;
        SetControlsEnabled(false);
        captureButton.Text = "Stop Browser Assisted Capture   (F8)";
        statusLabel.Text = "Browser Assisted Capture starting — the exact web viewport will be outlined briefly.";
        qualityLabel.Text = "Browser Agent: Auto until confirmed page end; press F8 again for a Partial manual stop.";
        trayStopItem.Enabled = true;
        trayIcon.Visible = true;

        try
        {
            Hide();
            await Task.Delay(180);
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(message =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() => statusLabel.Text = "Browser Agent: " + message));
                }
                catch (InvalidOperationException) { }
            });

            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            ShowMainWindow();

            string pageHint = result.PageCounterTotal > 0
                ? $" · DOM progress {result.PageCounterCurrent}/{result.PageCounterTotal}"
                : string.Empty;
            statusLabel.Text = result.IsComplete
                ? $"Complete: confirmed page end · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}"
                : $"Partial: {result.StopReason} · {result.FrameCount} frames · {result.Width} × {result.Height}px{pageHint} — {result.OutputPath}";
            qualityLabel.Text = result.IsComplete
                ? "Browser Agent: true page end confirmed; verified-overlap stitch completed."
                : "Browser Agent: Partial capture saved; verified frames were stitched without claiming a complete page.";
        }
        catch (Exception ex)
        {
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            ShowMainWindow();
            statusLabel.Text = "Browser Assisted Capture failed.";
            LongCaptureLog.Error("integrated Browser Assisted Capture failed", ex);

            string diagnostics = string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(browserAgentController.LastSessionDirectory))
                {
                    diagnostics = BrowserAgentDiagnosticsExporter.Export(browserAgentController.LastSessionDirectory);
                }
            }
            catch (Exception exportEx)
            {
                LongCaptureLog.Warn($"integrated Browser Agent diagnostics export failed: {LongCaptureLog.OneLine(exportEx.Message)}");
            }

            MessageBox.Show(this,
                ex.Message + (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : "\n\nDiagnostics ZIP:\n" + diagnostics),
                "Browser Assisted Capture error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            captureBusy = false;
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            SetControlsEnabled(true);
            await RefreshModeUiAsync();
        }
    }

    private void OnBrowserAgentStateChanged()
    {
        if (IsDisposed) return;
        void update()
        {
            if (!BrowserAgentSelected) return;
            readinessLabel.Text = browserAgentController.IsConnected
                ? "Ready · " + browserAgentController.AttachedSummary
                : "Not ready · Activate the target Chromium/Helium tab and click the LongCapture extension (or Ctrl+Shift+L).";
            if (!captureBusy)
            {
                statusLabel.Text = browserAgentController.IsConnected
                    ? "Browser Agent attached. Press F8 to outline the web viewport and start Auto-until-end capture."
                    : "Browser Agent waiting for a Chromium/Helium tab attachment.";
            }
        }

        if (InvokeRequired)
        {
            try { BeginInvoke((Action)update); } catch (InvalidOperationException) { }
        }
        else update();
    }

    private static void OpenBrowserAgentFolder()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "BrowserAgent");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private void UpdateQualityLabel()
    {
'@ `
    -Marker 'private async Task RunBrowserAssistedCaptureAsync()'

Replace-Literal -Path $mainForm `
    -Old @'
    private void RequestStop()
    {
        if (activeService?.IsCapturing == true)
'@ `
    -New @'
    private void RequestStop()
    {
        if (browserAgentController.IsCapturing)
        {
            trayStopItem.Enabled = false;
            LongCaptureLog.Info("capture stop forwarded to integrated Browser Agent");
            browserAgentController.Stop();
            return;
        }

        if (activeService?.IsCapturing == true)
'@ `
    -Marker 'capture stop forwarded to integrated Browser Agent'

# Patch individual enable lines so v0.1.4/v0.1.6-added controls do not break the anchor.
Replace-Literal -Path $mainForm -Old '        targetSelector.Enabled = enabled;' -New '        targetSelector.Enabled = enabled && !BrowserAgentSelected;' -Marker 'targetSelector.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        foregroundTargetButton.Enabled = enabled;' -New '        foregroundTargetButton.Enabled = enabled && !BrowserAgentSelected;' -Marker 'foregroundTargetButton.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        refreshTargetsButton.Enabled = enabled;' -New '        refreshTargetsButton.Enabled = enabled && !BrowserAgentSelected;' -Marker 'refreshTargetsButton.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        startDelay.Enabled = enabled;' -New '        startDelay.Enabled = enabled && !BrowserAgentSelected;' -Marker 'startDelay.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        scrollDelay.Enabled = enabled;' -New '        scrollDelay.Enabled = enabled && !BrowserAgentSelected;' -Marker 'scrollDelay.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        scrollAmount.Enabled = enabled;' -New '        scrollAmount.Enabled = enabled && !BrowserAgentSelected;' -Marker 'scrollAmount.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        scrollMethod.Enabled = enabled;' -New '        scrollMethod.Enabled = enabled && !BrowserAgentSelected;' -Marker 'scrollMethod.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        autoScrollTop.Enabled = enabled;' -New '        autoScrollTop.Enabled = enabled && !BrowserAgentSelected;' -Marker 'autoScrollTop.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        wholeWindowCapture.Enabled = enabled;' -New '        wholeWindowCapture.Enabled = enabled && !BrowserAgentSelected;' -Marker 'wholeWindowCapture.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        debugCaptureUi.Enabled = enabled;' -New '        debugCaptureUi.Enabled = enabled && !BrowserAgentSelected;' -Marker 'debugCaptureUi.Enabled = enabled && !BrowserAgentSelected;'
Replace-Literal -Path $mainForm -Old '        includeInternalDebugWindows.Enabled = enabled && debugCaptureUi.Checked;' -New '        includeInternalDebugWindows.Enabled = enabled && !BrowserAgentSelected && debugCaptureUi.Checked;' -Marker 'includeInternalDebugWindows.Enabled = enabled && !BrowserAgentSelected && debugCaptureUi.Checked;'
Replace-Literal -Path $mainForm -Old '            browserButton.Enabled = SelectedMode != LongCaptureStandaloneMode.Normal;' -New '            browserButton.Enabled = BrowserAgentSelected || SelectedMode != LongCaptureStandaloneMode.Normal;' -Marker 'browserButton.Enabled = BrowserAgentSelected || SelectedMode != LongCaptureStandaloneMode.Normal;'
Replace-Literal -Path $mainForm -Old '            recipePath.Enabled = SelectedMode == LongCaptureStandaloneMode.RunRecipe;' -New '            recipePath.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;' -Marker 'recipePath.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;'
Replace-Literal -Path $mainForm -Old '            browseRecipeButton.Enabled = SelectedMode == LongCaptureStandaloneMode.RunRecipe;' -New '            browseRecipeButton.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;' -Marker 'browseRecipeButton.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;'
Replace-Literal -Path $mainForm -Old '            reviewButton.Enabled = SelectedMode == LongCaptureStandaloneMode.RunRecipe;' -New '            reviewButton.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;' -Marker 'reviewButton.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;'

Replace-Literal -Path $mainForm `
    -Old @'
            Directory.CreateDirectory(outputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
'@ `
    -New @'
            string activeOutputDirectory = BrowserAgentSelected
                ? Path.Combine(AppContext.BaseDirectory, "BrowserAgentCaptures")
                : outputDirectory;
            Directory.CreateDirectory(activeOutputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = activeOutputDirectory,
'@ `
    -Marker 'string activeOutputDirectory = BrowserAgentSelected'

Replace-Literal -Path $mainForm `
    -Old @'
    private void ExportDiagnostics()
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
'@ `
    -New @'
    private void ExportDiagnostics()
    {
        if (BrowserAgentSelected)
        {
            try
            {
                string zip = browserAgentController.ExportLastDiagnostics();
                string directory = Path.GetDirectoryName(zip) ?? Path.Combine(AppContext.BaseDirectory, "BrowserAgentCaptures", "Diagnostics");
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
                MessageBox.Show(this, "Browser Agent diagnostics created:\n\n" + zip, "LongCapture diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                LongCaptureLog.Error("Browser Agent diagnostic bundle export failed", ex);
                MessageBox.Show(this, ex.Message, "Export Browser Agent diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
'@ `
    -Marker 'Browser Agent diagnostics created:'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.2 main-window integration compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.2 main-window integration applied." -ForegroundColor Green
}
