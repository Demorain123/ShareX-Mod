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
    -Old @'
        modeSelector.Items.AddRange(new object[]
        {
            "Normal Long Capture",
            "Smart Web Capture",
            "Teach Capture",
            "Run Recipe"
        });
'@ `
    -New @'
        modeSelector.Items.AddRange(new object[]
        {
            "Normal Long Capture",
            "Browser Assisted Capture",
            "Smart Web Capture",
            "Teach Capture",
            "Run Recipe"
        });
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
    -Old @'
            trayIcon.Dispose();
            activeService?.Dispose();
'@ `
    -New @'
            trayIcon.Dispose();
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
            refreshTargetsButton.Enabled = false;
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
        refreshTargetsButton.Enabled = !captureBusy;
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
        statusLabel.Text = "Browser Assisted Capture starting — the web viewport will be outlined briefly.";
        qualityLabel.Text = "Browser Agent: Auto until confirmed page end; press F8 again for a partial manual stop.";
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
                : "Browser Agent: partial capture saved; verified frames were stitched without claiming a complete page.";
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

Replace-Literal -Path $mainForm `
    -Old @'
    private void SetControlsEnabled(bool enabled)
    {
        modeSelector.Enabled = enabled;
        targetSelector.Enabled = enabled;
        refreshTargetsButton.Enabled = enabled;
        openLogsButton.Enabled = true;
        startDelay.Enabled = enabled;
        scrollDelay.Enabled = enabled;
        scrollAmount.Enabled = enabled;
        scrollMethod.Enabled = enabled;
        autoScrollTop.Enabled = enabled;
        if (enabled)
        {
            browserButton.Enabled = SelectedMode != LongCaptureStandaloneMode.Normal;
            recipePath.Enabled = SelectedMode == LongCaptureStandaloneMode.RunRecipe;
            browseRecipeButton.Enabled = SelectedMode == LongCaptureStandaloneMode.RunRecipe;
            reviewButton.Enabled = SelectedMode == LongCaptureStandaloneMode.RunRecipe;
        }
'@ `
    -New @'
    private void SetControlsEnabled(bool enabled)
    {
        modeSelector.Enabled = enabled;
        targetSelector.Enabled = enabled && !BrowserAgentSelected;
        refreshTargetsButton.Enabled = enabled && !BrowserAgentSelected;
        openLogsButton.Enabled = true;
        startDelay.Enabled = enabled && !BrowserAgentSelected;
        scrollDelay.Enabled = enabled && !BrowserAgentSelected;
        scrollAmount.Enabled = enabled && !BrowserAgentSelected;
        scrollMethod.Enabled = enabled && !BrowserAgentSelected;
        autoScrollTop.Enabled = enabled && !BrowserAgentSelected;
        if (enabled)
        {
            browserButton.Enabled = BrowserAgentSelected || SelectedMode != LongCaptureStandaloneMode.Normal;
            recipePath.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;
            browseRecipeButton.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;
            reviewButton.Enabled = !BrowserAgentSelected && SelectedMode == LongCaptureStandaloneMode.RunRecipe;
        }
'@ `
    -Marker 'startDelay.Enabled = enabled && !BrowserAgentSelected;'

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

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.2 main-window integration compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.2 main-window integration applied." -ForegroundColor Green
}
