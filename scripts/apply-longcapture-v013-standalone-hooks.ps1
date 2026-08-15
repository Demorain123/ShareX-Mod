[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
if (-not (Test-Path -LiteralPath $main)) { throw "LongCapture MainForm.cs not found." }

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $text = [IO.File]::ReadAllText($main)
    if ($text.Contains($Marker)) {
        Write-Host "[LongCapture-v0.1.3] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "LongCapture v0.1.3 compatibility check failed: '$Marker' anchor not found."
    }

    Write-Host "[LongCapture-v0.1.3] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($main, $text, [Text.UTF8Encoding]::new($true))
        Write-Host "[LongCapture-v0.1.3] applied: $Marker" -ForegroundColor Cyan
    }
}

# Export the correlated app + ShareX-Mod evidence as one user-friendly ZIP.
Replace-Literal `
    -Old @'
        var openOutputButton = new Button { Text = "Open output folder", Dock = DockStyle.Left, Width = 150 };
        openOutputButton.Click += (_, _) => OpenOutputDirectory();

        qualityLabel.Text = "Quality: waiting for a capture.";
'@ `
    -New @'
        var openOutputButton = new Button { Text = "Open output folder", Dock = DockStyle.Left, Width = 150 };
        openOutputButton.Click += (_, _) => OpenOutputDirectory();

        var exportDiagnosticsButton = new Button { Text = "Export diagnostics", Dock = DockStyle.Left, Width = 150 };
        exportDiagnosticsButton.Click += (_, _) => ExportDiagnostics();

        qualityLabel.Text = "Quality: waiting for a capture.";
'@ `
    -Marker 'Text = "Export diagnostics"'

Replace-Literal `
    -Old @'
        openOutputButton.Location = new Point(0, 8);
        actionPanel.Controls.Add(openOutputButton);
        actionPanel.Controls.Add(qualityLabel);
'@ `
    -New @'
        openOutputButton.Location = new Point(0, 8);
        actionPanel.Controls.Add(exportDiagnosticsButton);
        actionPanel.Controls.Add(openOutputButton);
        actionPanel.Controls.Add(qualityLabel);
'@ `
    -Marker 'actionPanel.Controls.Add(exportDiagnosticsButton);'

# Start a standalone correlation session before target selection/capture work begins.
Replace-Literal `
    -Old @'
        LongCaptureLog.Info(
            $"capture requested mode={mode} target={targetSummary} startDelay={options.StartDelay} scrollDelay={options.ScrollDelay} scrollAmount={options.ScrollAmount} scrollMethod={options.ScrollMethod} autoScrollTop={options.AutoScrollTop}");

        try
'@ `
    -New @'
        LongCaptureLog.Info(
            $"capture requested mode={mode} target={targetSummary} startDelay={options.StartDelay} scrollDelay={options.ScrollDelay} scrollAmount={options.ScrollAmount} scrollMethod={options.ScrollMethod} autoScrollTop={options.AutoScrollTop}");

        CaptureSessionRecorder? recorder = null;

        try
'@ `
    -Marker 'CaptureSessionRecorder? recorder = null;'

Replace-Literal `
    -Old @'
            trayStopItem.Enabled = true;
            trayIcon.Visible = true;

            CaptureExclusion.ApplyToCurrentProcessTopLevelWindows("pre-capture");
'@ `
    -New @'
            recorder = new CaptureSessionRecorder(mode.ToString(), targetSummary, options);

            trayStopItem.Enabled = true;
            trayIcon.Visible = true;

            CaptureExclusion.ApplyToCurrentProcessTopLevelWindows("pre-capture");
'@ `
    -Marker 'recorder = new CaptureSessionRecorder(mode.ToString(), targetSummary, options);'

# Correlate the already-existing Capture Map / robust session / final quality summary rather than
# duplicating a second per-frame algorithm inside the standalone shell.
Replace-Literal `
    -Old @'
            ShowMainWindow();

            if (mode == LongCaptureStandaloneMode.Teach)
'@ `
    -New @'
            LongCaptureQualityInfo? correlatedQuality = LongCaptureStandaloneBridge.FindLatestQualityInfo();
            CaptureSessionQuality? standaloneQuality = recorder?.Complete(status, savedPath, resultSize, correlatedQuality);
            if (standaloneQuality != null)
            {
                LongCaptureLog.Info($"standalone quality status={standaloneQuality.Status} score={standaloneQuality.Score}/100 engine={standaloneQuality.EngineQualityStatus}/{standaloneQuality.EngineConfidence} evidenceFiles={standaloneQuality.EvidenceFilesCopied}");
            }

            ShowMainWindow();

            if (mode == LongCaptureStandaloneMode.Teach)
'@ `
    -Marker 'CaptureSessionQuality? standaloneQuality = recorder?.Complete'

Replace-Literal `
    -Old @'
            LongCaptureLog.Error("capture failed", ex);
            MessageBox.Show(this, ex.Message + "\n\nDiagnostic log: " + LongCaptureLog.CurrentLogPath, "LongCapture capture error", MessageBoxButtons.OK, MessageBoxIcon.Error);
'@ `
    -New @'
            LongCaptureLog.Error("capture failed", ex);
            try
            {
                recorder?.Complete(
                    ScrollingCaptureStatus.Failed,
                    null,
                    null,
                    LongCaptureStandaloneBridge.FindLatestQualityInfo());
            }
            catch (Exception diagnosticsEx)
            {
                LongCaptureLog.Warn($"capture diagnostic finalization failed type={diagnosticsEx.GetType().Name} message={LongCaptureLog.OneLine(diagnosticsEx.Message)}");
            }
            MessageBox.Show(
                this,
                ex.Message + "\n\nDiagnostic log: " + LongCaptureLog.CurrentLogPath +
                (recorder is null ? string.Empty : "\nCapture session: " + recorder.SessionDirectory),
                "LongCapture capture error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
'@ `
    -Marker 'capture diagnostic finalization failed'

Replace-Literal `
    -Old @'
        finally
        {
            activeService = null;
'@ `
    -New @'
        finally
        {
            recorder?.Dispose();
            activeService = null;
'@ `
    -Marker 'recorder?.Dispose();'

Replace-Literal `
    -Old @'
    private void OpenLogDirectory()
    {
'@ `
    -New @'
    private void ExportDiagnostics()
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            string bundle = CaptureSessionRecorder.ExportLatestBundle(outputDirectory);
            LongCaptureLog.Info($"diagnostic bundle exported path={LongCaptureLog.OneLine(bundle)}");
            Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
                UseShellExecute = true
            });
            MessageBox.Show(
                this,
                "Diagnostic bundle created:\n\n" + bundle +
                "\n\nReview it before sharing publicly because window titles and local file paths can appear in diagnostics.",
                "LongCapture diagnostics",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("diagnostic bundle export failed", ex);
            MessageBox.Show(this, ex.Message, "Export diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogDirectory()
    {
'@ `
    -Marker 'private void ExportDiagnostics()'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.3 standalone hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.3 standalone hooks applied." -ForegroundColor Green
}
