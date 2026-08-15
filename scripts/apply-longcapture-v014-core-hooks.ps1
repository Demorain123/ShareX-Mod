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
    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.4 target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[LongCapture-v0.1.4] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.4 compatibility anchor not found: $Marker in $Path" }
    Write-Host "[LongCapture-v0.1.4] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
        Write-Host "[LongCapture-v0.1.4] applied: $Marker" -ForegroundColor Cyan
    }
}

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"

# --- Anchor-first compositor -------------------------------------------------
# Once the multi-anchor matcher establishes the real delta, do not let the legacy exact-row
# matcher choose a second, unrelated matchIndex/ignoreBottomOffset pair. Appending exactly delta
# pixels keeps frame -> mosaic coordinates stable and avoids re-stamping changing fixed side UI.
Replace-Literal -Path $manager `
    -Old '                            Bitmap newResult = await CombineImagesAsync(Result, modCombineImage);' `
    -New @'
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
    -Marker 'modV014AnchorCompositorUsed = newResult != null;'

# --- GUI: foreground F7, region-inside-window default, debug capture ----------
Replace-Literal -Path $main `
    -Old @'
    private const int HotkeyId = 0x4C43;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F8 = 0x77;
'@ `
    -New @'
    private const int HotkeyId = 0x4C43;
    private const int ForegroundHotkeyId = 0x4C44;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F7 = 0x76;
    private const uint VK_F8 = 0x77;
'@ `
    -Marker 'private const int ForegroundHotkeyId = 0x4C44;'

Replace-Literal -Path $main `
    -Old @'
    private readonly Button refreshTargetsButton = new();
    private readonly Button openLogsButton = new();
'@ `
    -New @'
    private readonly Button foregroundTargetButton = new();
    private readonly Button refreshTargetsButton = new();
    private readonly Button openLogsButton = new();
'@ `
    -Marker 'private readonly Button foregroundTargetButton = new();'

Replace-Literal -Path $main `
    -Old @'
    private readonly CheckBox autoScrollTop = new();
    private readonly Label outputLabel = new();
'@ `
    -New @'
    private readonly CheckBox autoScrollTop = new();
    private readonly CheckBox wholeWindowCapture = new();
    private readonly CheckBox debugCaptureUi = new();
    private readonly Label outputLabel = new();
'@ `
    -Marker 'private readonly CheckBox wholeWindowCapture = new();'

Replace-Literal -Path $main `
    -Old '    private bool hotkeyRegistered;' `
    -New @'
    private bool hotkeyRegistered;
    private bool foregroundHotkeyRegistered;
'@ `
    -Marker 'private bool foregroundHotkeyRegistered;'

Replace-Literal -Path $main `
    -Old '        Text = "LongCapture Standalone v0.1.3-dev";' `
    -New '        Text = $"LongCapture Standalone v{StandaloneVersion.Value}";' `
    -Marker 'Text = $"LongCapture Standalone v{StandaloneVersion.Value}";'

Replace-Literal -Path $main `
    -Old @'
            ColumnCount = 4,
            RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
'@ `
    -New @'
            ColumnCount = 5,
            RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
'@ `
    -Marker 'targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));'

Replace-Literal -Path $main `
    -Old @'
        targetSelector.SelectionChangeCommitted += (_, _) => LogSelectedTarget();

        refreshTargetsButton.Text = "Refresh targets";
'@ `
    -New @'
        targetSelector.SelectionChangeCommitted += (_, _) => LogSelectedTarget();
        targetSelector.DrawMode = DrawMode.OwnerDrawFixed;
        targetSelector.ItemHeight = 24;
        targetSelector.DrawItem += DrawTargetSelectorItem;

        foregroundTargetButton.Text = "Foreground target (F7)";
        foregroundTargetButton.Dock = DockStyle.Fill;
        foregroundTargetButton.MinimumSize = new Size(155, 34);
        foregroundTargetButton.Click += (_, _) => ArmForegroundTargetSelection();

        refreshTargetsButton.Text = "Refresh targets";
'@ `
    -Marker 'foregroundTargetButton.Text = "Foreground target (F7)";'

Replace-Literal -Path $main `
    -Old @'
        targetStrip.Controls.Add(targetLabel, 0, 0);
        targetStrip.Controls.Add(targetSelector, 1, 0);
        targetStrip.Controls.Add(refreshTargetsButton, 2, 0);
        targetStrip.Controls.Add(openLogsButton, 3, 0);
'@ `
    -New @'
        targetStrip.Controls.Add(targetLabel, 0, 0);
        targetStrip.Controls.Add(targetSelector, 1, 0);
        targetStrip.Controls.Add(foregroundTargetButton, 2, 0);
        targetStrip.Controls.Add(refreshTargetsButton, 3, 0);
        targetStrip.Controls.Add(openLogsButton, 4, 0);
'@ `
    -Marker 'targetStrip.Controls.Add(foregroundTargetButton, 2, 0);'

Replace-Literal -Path $main `
    -Old @'
            "Normal Long Capture",
            "Smart Web Capture",
            "Teach Capture",
            "Run Recipe"
'@ `
    -New @'
            "Normal Long Capture",
            "Smart Web Capture (Advanced)",
            "Teach Capture (Advanced)",
            "Run Recipe (Advanced)"
'@ `
    -Marker '"Smart Web Capture (Advanced)"'

Replace-Literal -Path $main `
    -Old @'
        autoScrollTop.Text = "Scroll selected target to the top before capture";
        autoScrollTop.Dock = DockStyle.Fill;

        outputLabel.Text = outputDirectory;
'@ `
    -New @'
        autoScrollTop.Text = "Scroll selected target to the top before capture";
        autoScrollTop.AutoSize = true;
        wholeWindowCapture.Text = "Whole window (skip region selection)";
        wholeWindowCapture.AutoSize = true;
        wholeWindowCapture.Checked = false;
        debugCaptureUi.Text = "Debug: GUI capturable + settings snapshot";
        debugCaptureUi.AutoSize = true;
        debugCaptureUi.Checked = false;
        debugCaptureUi.CheckedChanged += (_, _) => CaptureExclusion.SetDebugCaptureUi(debugCaptureUi.Checked, "main-form-checkbox");

        outputLabel.Text = outputDirectory;
'@ `
    -Marker 'debugCaptureUi.Text = "Debug: GUI capturable + settings snapshot";'

Replace-Literal -Path $main `
    -Old '        AddRow(body, 9, "Start position", autoScrollTop);' `
    -New @'
        var captureBehaviorPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        captureBehaviorPanel.Controls.Add(autoScrollTop);
        captureBehaviorPanel.Controls.Add(wholeWindowCapture);
        captureBehaviorPanel.Controls.Add(debugCaptureUi);
        AddRow(body, 9, "Capture behavior", captureBehaviorPanel);
'@ `
    -Marker 'captureBehaviorPanel.Controls.Add(wholeWindowCapture);'

# Register F7 beside F8.
Replace-Literal -Path $main `
    -Old @'
        hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, VK_F8);
        if (!hotkeyRegistered)
'@ `
    -New @'
        foregroundHotkeyRegistered = RegisterHotKey(Handle, ForegroundHotkeyId, MOD_NOREPEAT, VK_F7);
        LongCaptureLog.Info(foregroundHotkeyRegistered
            ? "global F7 foreground-target hotkey registered"
            : $"global F7 foreground-target hotkey registration failed win32={Marshal.GetLastWin32Error()}");

        hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, VK_F8);
        if (!hotkeyRegistered)
'@ `
    -Marker 'global F7 foreground-target hotkey registered'

Replace-Literal -Path $main `
    -Old @'
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            LongCaptureLog.Info("global F8 hotkey unregistered");
        }
        base.OnHandleDestroyed(e);
'@ `
    -New @'
        if (foregroundHotkeyRegistered)
        {
            UnregisterHotKey(Handle, ForegroundHotkeyId);
            foregroundHotkeyRegistered = false;
            LongCaptureLog.Info("global F7 hotkey unregistered");
        }
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            LongCaptureLog.Info("global F8 hotkey unregistered");
        }
        base.OnHandleDestroyed(e);
'@ `
    -Marker 'global F7 hotkey unregistered'

Replace-Literal -Path $main `
    -Old @'
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
'@ `
    -New @'
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == ForegroundHotkeyId)
        {
            SelectForegroundTarget();
            return;
        }
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
'@ `
    -Marker 'SelectForegroundTarget();'

# Add foreground-selection and icon draw helpers before RefreshTargetList.
Replace-Literal -Path $main `
    -Old @'
    private void RefreshTargetList()
    {
'@ `
    -New @'
    private void ArmForegroundTargetSelection()
    {
        MessageBox.Show(
            this,
            "After this dialog closes, activate the window you want to long-capture and press F7.\n\nF7 only locks the target window. When you press F8, LongCapture will still let you drag the exact capture region inside that window unless Whole window is enabled.",
            "Choose foreground target",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        Hide();
    }

    private void SelectForegroundTarget()
    {
        if (!CaptureTargetService.TryGetForegroundTarget(out CaptureTargetDescriptor? foreground, out string detail) || foreground is null)
        {
            ShowMainWindow();
            statusLabel.Text = "F7 target selection failed: " + detail;
            LongCaptureLog.Warn($"foreground target selection failed detail={LongCaptureLog.OneLine(detail)}");
            return;
        }

        RefreshTargetList();
        for (int i = 0; i < targetSelector.Items.Count; i++)
        {
            if (targetSelector.Items[i] is CaptureTargetDescriptor candidate && candidate.Handle == foreground.Handle)
            {
                targetSelector.SelectedIndex = i;
                ShowMainWindow();
                statusLabel.Text = $"Target locked by F7: {candidate.Title}. Press F8, then drag the capture region inside it.";
                LogSelectedTarget();
                return;
            }
        }

        targetSelector.Items.Add(foreground);
        targetSelector.SelectedItem = foreground;
        ShowMainWindow();
        statusLabel.Text = $"Target locked by F7: {foreground.Title}. Press F8, then drag the capture region inside it.";
        LogSelectedTarget();
    }

    private void DrawTargetSelectorItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= targetSelector.Items.Count) return;
        object item = targetSelector.Items[e.Index]!;
        string text = item.ToString() ?? string.Empty;
        int x = e.Bounds.Left + 4;
        if (item is CaptureTargetDescriptor target && target.Icon is not null)
        {
            e.Graphics.DrawIcon(target.Icon, new Rectangle(x, e.Bounds.Top + 3, 18, 18));
            x += 24;
        }
        TextRenderer.DrawText(e.Graphics, text, e.Font, new Rectangle(x, e.Bounds.Top, Math.Max(1, e.Bounds.Right - x), e.Bounds.Height), e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        e.DrawFocusRectangle();
    }

    private void RefreshTargetList()
    {
'@ `
    -Marker 'private void ArmForegroundTargetSelection()'

# Region selection is now the default even for a locked HWND. Whole-window capture is explicit.
Replace-Literal -Path $main `
    -Old @'
            bool targetAssigned = false;
            if (requestedTarget is not null)
            {
                if (CaptureTargetService.TryRefreshTarget(requestedTarget, out CaptureTargetDescriptor? refreshed, out string refreshDetail) && refreshed is not null)
                {
                    if (ScrollingCaptureTargetBridge.TryAssignTarget(service, refreshed, out string bridgeDetail))
                    {
                        targetAssigned = true;
                        LongCaptureLog.Info(
                            $"locked target assigned hwnd={refreshed.HandleHex} pid={refreshed.ProcessId} title={LongCaptureLog.OneLine(refreshed.Title)} bounds={refreshed.Bounds} bridge={LongCaptureLog.OneLine(bridgeDetail)}");
                    }
                    else
                    {
                        LongCaptureLog.Warn($"locked target bridge unavailable; falling back to ShareX picker detail={LongCaptureLog.OneLine(bridgeDetail)}");
                    }
                }
                else
                {
                    LongCaptureLog.Warn($"locked target became unavailable; falling back to ShareX picker detail={LongCaptureLog.OneLine(refreshDetail)}");
                }
            }

            if (!targetAssigned)
            {
                if (!service.SelectWindow())
                {
                    ShowMainWindow();
                    statusLabel.Text = "Capture cancelled before start.";
                    LongCaptureLog.Info("capture cancelled in ShareX target picker");
                    return;
                }
                LongCaptureLog.Info("ShareX fallback picker selected a capture region/window");
            }
'@ `
    -New @'
            bool targetAssigned = false;
            if (requestedTarget is not null)
            {
                if (CaptureTargetService.TryRefreshTarget(requestedTarget, out CaptureTargetDescriptor? refreshed, out string refreshDetail) && refreshed is not null)
                {
                    _ = CaptureTargetService.TryActivateTarget(refreshed, out string activationDetail);
                    LongCaptureLog.Info($"target activation detail={LongCaptureLog.OneLine(activationDetail)}");

                    if (wholeWindowCapture.Checked)
                    {
                        targetAssigned = ScrollingCaptureTargetBridge.TryAssignTarget(service, refreshed, out string bridgeDetail);
                        if (targetAssigned)
                        {
                            LongCaptureLog.Info($"whole-window target assigned hwnd={refreshed.HandleHex} bounds={refreshed.Bounds} bridge={LongCaptureLog.OneLine(bridgeDetail)}");
                        }
                        else
                        {
                            LongCaptureLog.Warn($"whole-window target bridge failed detail={LongCaptureLog.OneLine(bridgeDetail)}");
                        }
                    }
                    else
                    {
                        if (!service.SelectWindow())
                        {
                            ShowMainWindow();
                            statusLabel.Text = "Capture cancelled while selecting the region.";
                            LongCaptureLog.Info("capture cancelled in region-inside-target picker");
                            return;
                        }

                        if (!ScrollingCaptureTargetBridge.TryRelockTargetToSelectedRegion(service, refreshed, out Rectangle selectedRegion, out string regionDetail))
                        {
                            ShowMainWindow();
                            statusLabel.Text = "Selected region is outside the locked target. Try again.";
                            LongCaptureLog.Warn($"region relock rejected detail={LongCaptureLog.OneLine(regionDetail)}");
                            return;
                        }

                        targetAssigned = true;
                        LongCaptureLog.Info($"locked target region assigned hwnd={refreshed.HandleHex} selectedRegion={selectedRegion} detail={LongCaptureLog.OneLine(regionDetail)}");
                    }
                }
                else
                {
                    ShowMainWindow();
                    statusLabel.Text = "Selected target is no longer available. Press F7 to choose the foreground target again.";
                    LongCaptureLog.Warn($"locked target became unavailable detail={LongCaptureLog.OneLine(refreshDetail)}");
                    return;
                }
            }

            if (requestedTarget is null && !targetAssigned)
            {
                if (!service.SelectWindow())
                {
                    ShowMainWindow();
                    statusLabel.Text = "Capture cancelled before start.";
                    LongCaptureLog.Info("capture cancelled in ShareX fallback picker");
                    return;
                }
                LongCaptureLog.Info("ShareX fallback picker selected a capture region/window");
            }
'@ `
    -Marker 'region-inside-target picker'

# Save the exact displayed settings/engine options in Debug mode before the form is hidden.
Replace-Literal -Path $main `
    -Old @'
        LongCaptureLog.Info(
            $"capture requested mode={mode} target={targetSummary} startDelay={options.StartDelay} scrollDelay={options.ScrollDelay} scrollAmount={options.ScrollAmount} scrollMethod={options.ScrollMethod} autoScrollTop={options.AutoScrollTop}");

        CaptureSessionRecorder? recorder = null;
'@ `
    -New @'
        LongCaptureLog.Info(
            $"capture requested mode={mode} target={targetSummary} startDelay={options.StartDelay} scrollDelay={options.ScrollDelay} scrollAmount={options.ScrollAmount} scrollMethod={options.ScrollMethod} autoScrollTop={options.AutoScrollTop} wholeWindow={wholeWindowCapture.Checked} debugUi={debugCaptureUi.Checked}");

        CaptureExclusion.SetDebugCaptureUi(debugCaptureUi.Checked, "capture-request");
        if (debugCaptureUi.Checked)
        {
            DebugStateSnapshot.Save(this, outputDirectory, new
            {
                Version = StandaloneVersion.Value,
                Time = DateTimeOffset.Now,
                Mode = mode.ToString(),
                Target = targetSummary,
                WholeWindow = wholeWindowCapture.Checked,
                DebugCaptureUi = debugCaptureUi.Checked,
                Displayed = new
                {
                    StartDelay = (int)startDelay.Value,
                    ScrollDelay = (int)scrollDelay.Value,
                    ScrollAmount = (int)scrollAmount.Value,
                    ScrollMethod = scrollMethod.SelectedItem?.ToString(),
                    AutoScrollTop = autoScrollTop.Checked
                },
                EngineOptions = new
                {
                    options.StartDelay,
                    options.ScrollDelay,
                    options.ScrollAmount,
                    ScrollMethod = options.ScrollMethod.ToString(),
                    options.AutoScrollTop,
                    options.AutoIgnoreBottomEdge,
                    options.ShowRegion
                },
                Log = LongCaptureLog.CurrentLogPath
            });
        }

        CaptureSessionRecorder? recorder = null;
'@ `
    -Marker 'DebugStateSnapshot.Save(this, outputDirectory'

# Quality FAIL must be obvious and automatically produce evidence next to the image.
Replace-Literal -Path $main `
    -Old @'
            if (standaloneQuality != null)
            {
                LongCaptureLog.Info($"standalone quality status={standaloneQuality.Status} score={standaloneQuality.Score}/100 engine={standaloneQuality.EngineQualityStatus}/{standaloneQuality.EngineConfidence} evidenceFiles={standaloneQuality.EvidenceFilesCopied}");
            }

            ShowMainWindow();
'@ `
    -New @'
            string? automaticDiagnostics = null;
            if (standaloneQuality != null)
            {
                LongCaptureLog.Info($"standalone quality status={standaloneQuality.Status} score={standaloneQuality.Score}/100 engine={standaloneQuality.EngineQualityStatus}/{standaloneQuality.EngineConfidence} evidenceFiles={standaloneQuality.EvidenceFilesCopied}");
                bool qualityFailed = string.Equals(standaloneQuality.Status, "FAIL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(standaloneQuality.EngineConfidence, "low", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(standaloneQuality.EngineQualityStatus, "unresolved", StringComparison.OrdinalIgnoreCase);
                if (qualityFailed)
                {
                    try
                    {
                        automaticDiagnostics = CaptureSessionRecorder.ExportLatestBundle(outputDirectory);
                        LongCaptureLog.Warn($"quality failure auto-exported diagnostics path={LongCaptureLog.OneLine(automaticDiagnostics)}");
                    }
                    catch (Exception exportEx)
                    {
                        LongCaptureLog.Warn($"quality failure diagnostics export failed type={exportEx.GetType().Name} message={LongCaptureLog.OneLine(exportEx.Message)}");
                    }
                }
            }

            ShowMainWindow();
'@ `
    -Marker 'quality failure auto-exported diagnostics'

Replace-Literal -Path $main `
    -Old @'
                statusLabel.Text = $"{status}: {resultSize.Value.Width} × {resultSize.Value.Height}px — {savedPath}";
'@ `
    -New @'
                if (standaloneQuality is not null && string.Equals(standaloneQuality.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    statusLabel.Text = $"QUALITY FAIL {standaloneQuality.Score}/100 — image saved for diagnosis. Log: {LongCaptureLog.CurrentLogPath}" +
                        (string.IsNullOrWhiteSpace(automaticDiagnostics) ? string.Empty : $" — Diagnostics: {automaticDiagnostics}");
                }
                else
                {
                    statusLabel.Text = $"{status}: {resultSize.Value.Width} × {resultSize.Value.Height}px — {savedPath} — Log: {LongCaptureLog.CurrentLogPath}";
                }
'@ `
    -Marker 'QUALITY FAIL {standaloneQuality.Score}/100'

# Keep new controls enabled/disabled consistently during capture.
Replace-Literal -Path $main `
    -Old @'
        targetSelector.Enabled = enabled;
        refreshTargetsButton.Enabled = enabled;
'@ `
    -New @'
        targetSelector.Enabled = enabled;
        foregroundTargetButton.Enabled = enabled;
        refreshTargetsButton.Enabled = enabled;
'@ `
    -Marker 'foregroundTargetButton.Enabled = enabled;'

Replace-Literal -Path $main `
    -Old @'
        autoScrollTop.Enabled = enabled;
        if (enabled)
'@ `
    -New @'
        autoScrollTop.Enabled = enabled;
        wholeWindowCapture.Enabled = enabled;
        debugCaptureUi.Enabled = enabled;
        if (enabled)
'@ `
    -Marker 'wholeWindowCapture.Enabled = enabled;'

# Add the new multi-frame compositor fixture to the exact one-click automation path.
Replace-Literal -Path $automation `
    -Old '        "ShareX.ScreenCaptureLib.ShareXModDeferredOverlaySelfTests",' `
    -New @'
        "ShareX.ScreenCaptureLib.ShareXModDeferredOverlaySelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV014CompositorSelfTests",
'@ `
    -Marker '"ShareX.ScreenCaptureLib.ShareXModV014CompositorSelfTests",'

Replace-Literal -Path $automation `
    -Old 'AddManual(report, "manual.smart-web-live", "Real environment", "Smart Web live Capture Browser session",' `
    -New 'AddManual(report, "manual.smart-web-live", "Advanced optional", "Smart Web live Capture Browser session",' `
    -Marker '"Advanced optional", "Smart Web live Capture Browser session"'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.4 core hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.4 core hooks applied." -ForegroundColor Green
}
