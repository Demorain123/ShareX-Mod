using ShareX.ScreenCaptureLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal sealed class MainForm : Form
{
    private const int HotkeyId = 0x4C43;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F8 = 0x77;
    private const int CaptureUiQuietPeriodMs = 350;
    private const string ShareXPickerLabel = "ShareX region/window picker (fallback)";

    private readonly Label statusLabel = new();
    private readonly Label readinessLabel = new();
    private readonly Label qualityLabel = new();
    private readonly Button captureButton = new();
    private readonly Button browserButton = new();
    private readonly Button reviewButton = new();
    private readonly Button browseRecipeButton = new();
    private readonly Button refreshTargetsButton = new();
    private readonly Button openLogsButton = new();
    private readonly Button exportDiagnosticsButton = new();
    private readonly ComboBox modeSelector = new();
    private readonly ComboBox targetSelector = new();
    private readonly TextBox recipePath = new();
    private readonly NumericUpDown startDelay = new();
    private readonly NumericUpDown scrollDelay = new();
    private readonly NumericUpDown scrollAmount = new();
    private readonly ComboBox scrollMethod = new();
    private readonly CheckBox autoScrollTop = new();
    private readonly Label outputLabel = new();
    private readonly NotifyIcon trayIcon = new();
    private readonly ToolStripMenuItem trayStopItem = new("Stop capture (F8)");

    private ScrollingCaptureService? activeService;
    private CaptureSessionQuality? latestSessionQuality;
    private bool captureBusy;
    private bool hotkeyRegistered;
    private readonly string outputDirectory;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainForm()
    {
        outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LongCapture");

        Text = "LongCapture Standalone v0.1.3-dev";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 690);
        Size = new Size(900, 760);
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "LongCapture",
            Dock = DockStyle.Top,
            Height = 58,
            Font = new Font("Segoe UI Semibold", 22F),
            Padding = new Padding(18, 10, 0, 0)
        };

        var subtitle = new Label
        {
            Text = "Independent long screenshot workspace — adaptive settle, fixed/sticky suppression, flight recorder, Smart Web and Capture Recipes",
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(20, 0, 0, 8)
        };

        var targetStrip = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(20, 6, 20, 6),
            ColumnCount = 4,
            RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));

        var targetLabel = new Label
        {
            Text = "Capture target",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };

        targetSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        targetSelector.Dock = DockStyle.Fill;
        targetSelector.MaxDropDownItems = 24;
        targetSelector.DropDownWidth = 760;
        targetSelector.Items.Add(ShareXPickerLabel);
        targetSelector.SelectedIndex = 0;
        targetSelector.SelectionChangeCommitted += (_, _) => LogSelectedTarget();

        refreshTargetsButton.Text = "Refresh targets";
        refreshTargetsButton.Dock = DockStyle.Fill;
        refreshTargetsButton.MinimumSize = new Size(130, 34);
        refreshTargetsButton.Click += (_, _) => RefreshTargetList();

        openLogsButton.Text = "Open logs folder";
        openLogsButton.Dock = DockStyle.Fill;
        openLogsButton.MinimumSize = new Size(125, 34);
        openLogsButton.Click += (_, _) => OpenLogDirectory();

        targetStrip.Controls.Add(targetLabel, 0, 0);
        targetStrip.Controls.Add(targetSelector, 1, 0);
        targetStrip.Controls.Add(refreshTargetsButton, 2, 0);
        targetStrip.Controls.Add(openLogsButton, 3, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 12
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 11; i++) body.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        modeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        modeSelector.Items.AddRange(new object[]
        {
            "Normal Long Capture",
            "Smart Web Capture",
            "Teach Capture",
            "Run Recipe"
        });
        modeSelector.SelectedIndex = 0;
        modeSelector.Dock = DockStyle.Fill;
        modeSelector.SelectionChangeCommitted += async (_, _) => await RefreshModeUiAsync();

        browserButton.Text = "Open Capture Browser";
        browserButton.Dock = DockStyle.Left;
        browserButton.Width = 175;
        browserButton.Click += async (_, _) => await LaunchCaptureBrowserAsync();

        var readinessPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var probeButton = new Button { Text = "Check readiness", Width = 130, Height = 32 };
        probeButton.Click += async (_, _) => await RefreshReadinessAsync(showDialogOnFailure: false);
        readinessLabel.AutoSize = true;
        readinessLabel.Padding = new Padding(8, 7, 0, 0);
        readinessPanel.Controls.Add(probeButton);
        readinessPanel.Controls.Add(readinessLabel);

        recipePath.Dock = DockStyle.Fill;
        recipePath.PlaceholderText = "Latest recipe is selected automatically";
        recipePath.Text = LongCaptureStandaloneBridge.GetLatestRecipePath();

        var recipePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        browseRecipeButton.Text = "Browse recipe";
        browseRecipeButton.Width = 120;
        browseRecipeButton.Click += (_, _) => BrowseRecipe();
        reviewButton.Text = "Review / approve";
        reviewButton.Width = 140;
        reviewButton.Click += (_, _) => ReviewRecipe();
        recipePanel.Controls.Add(browseRecipeButton);
        recipePanel.Controls.Add(reviewButton);

        startDelay.Minimum = 0;
        startDelay.Maximum = 10000;
        startDelay.Value = 500;
        startDelay.Increment = 100;
        startDelay.Dock = DockStyle.Fill;

        scrollDelay.Minimum = 50;
        scrollDelay.Maximum = 10000;
        scrollDelay.Value = 450;
        scrollDelay.Increment = 50;
        scrollDelay.Dock = DockStyle.Fill;

        scrollAmount.Minimum = 1;
        scrollAmount.Maximum = 20;
        scrollAmount.Value = 2;
        scrollAmount.Dock = DockStyle.Fill;

        scrollMethod.DropDownStyle = ComboBoxStyle.DropDownList;
        scrollMethod.Items.AddRange(Enum.GetNames<ScrollMethod>());
        scrollMethod.SelectedItem = ScrollMethod.MouseWheel.ToString();
        scrollMethod.Dock = DockStyle.Fill;

        autoScrollTop.Text = "Scroll selected target to the top before capture";
        autoScrollTop.Dock = DockStyle.Fill;

        outputLabel.Text = outputDirectory;
        outputLabel.AutoEllipsis = true;
        outputLabel.Dock = DockStyle.Fill;
        outputLabel.TextAlign = ContentAlignment.MiddleLeft;

        captureButton.Text = "Start long capture   (F8)";
        captureButton.Height = 56;
        captureButton.Dock = DockStyle.Top;
        captureButton.Font = new Font("Segoe UI Semibold", 12F);
        captureButton.Click += async (_, _) => await ToggleCaptureAsync();

        var openOutputButton = new Button { Text = "Open output folder", Dock = DockStyle.Left, Width = 150 };
        openOutputButton.Click += (_, _) => OpenOutputDirectory();

        exportDiagnosticsButton.Text = "Export diagnostics";
        exportDiagnosticsButton.Dock = DockStyle.Left;
        exportDiagnosticsButton.Width = 150;
        exportDiagnosticsButton.Click += (_, _) => ExportDiagnostics();

        qualityLabel.Text = "Quality: waiting for a capture.";
        qualityLabel.Dock = DockStyle.Fill;
        qualityLabel.AutoEllipsis = true;
        qualityLabel.TextAlign = ContentAlignment.MiddleLeft;

        AddRow(body, 0, "Capture mode", modeSelector);
        AddRow(body, 1, "Browser", browserButton);
        AddRow(body, 2, "Readiness", readinessPanel);
        AddRow(body, 3, "Recipe", recipePath);
        AddRow(body, 4, "Recipe actions", recipePanel);
        AddRow(body, 5, "Start delay (ms)", startDelay);
        AddRow(body, 6, "Scroll settle (ms)", scrollDelay);
        AddRow(body, 7, "Scroll amount", scrollAmount);
        AddRow(body, 8, "Scroll method", scrollMethod);
        AddRow(body, 9, "Start position", autoScrollTop);
        AddRow(body, 10, "Output", outputLabel);

        var actionPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        qualityLabel.Dock = DockStyle.Bottom;
        qualityLabel.Height = 36;
        actionPanel.Controls.Add(exportDiagnosticsButton);
        actionPanel.Controls.Add(openOutputButton);
        actionPanel.Controls.Add(qualityLabel);
        actionPanel.Controls.Add(captureButton);
        body.Controls.Add(actionPanel, 0, 11);
        body.SetColumnSpan(actionPanel, 2);

        statusLabel.Text = "Ready. Choose a target by title, or keep the ShareX picker fallback, then press F8.";
        statusLabel.Dock = DockStyle.Bottom;
        statusLabel.Height = 46;
        statusLabel.Padding = new Padding(18, 12, 0, 0);

        trayStopItem.Enabled = false;
        trayStopItem.Click += (_, _) => RequestStop();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(trayStopItem);
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "LongCapture";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = false;

        Controls.Add(body);
        Controls.Add(targetStrip);
        Controls.Add(subtitle);
        Controls.Add(title);
        Controls.Add(statusLabel);

        Shown += async (_, _) =>
        {
            RefreshTargetList();
            await RefreshModeUiAsync();
            LongCaptureLog.Info($"main form shown version={StandaloneVersion.Value} output={outputDirectory}");
        };
        FormClosed += (_, _) => LongCaptureLog.Info("main form closed");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(this, "main-form");
        hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, VK_F8);
        if (!hotkeyRegistered)
        {
            int error = Marshal.GetLastWin32Error();
            statusLabel.Text = "F8 is already in use by another app. Use the Start button; tray Stop remains available.";
            LongCaptureLog.Warn($"global F8 registration failed win32={error}");
        }
        else
        {
            LongCaptureLog.Info("global F8 hotkey registered");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            LongCaptureLog.Info("global F8 hotkey unregistered");
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            LongCaptureLog.Info(captureBusy ? "F8 requested capture stop" : "F8 requested capture start");
            if (captureBusy) RequestStop();
            else BeginInvoke(new Action(() => _ = ToggleCaptureAsync()));
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            activeService?.Dispose();
        }
        base.Dispose(disposing);
    }

    private LongCaptureStandaloneMode SelectedMode => modeSelector.SelectedIndex switch
    {
        1 => LongCaptureStandaloneMode.SmartWeb,
        2 => LongCaptureStandaloneMode.Teach,
        3 => LongCaptureStandaloneMode.RunRecipe,
        _ => LongCaptureStandaloneMode.Normal
    };

    private CaptureTargetDescriptor? SelectedTarget => targetSelector.SelectedItem as CaptureTargetDescriptor;

    private static void AddRow(TableLayoutPanel body, int row, string label, Control control)
    {
        body.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        body.Controls.Add(control, 1, row);
    }

    private void RefreshTargetList()
    {
        IntPtr previousHandle = SelectedTarget?.Handle ?? IntPtr.Zero;
        targetSelector.BeginUpdate();
        try
        {
            targetSelector.Items.Clear();
            targetSelector.Items.Add(ShareXPickerLabel);

            var targets = CaptureTargetService.EnumerateTopLevelWindows();
            int selectedIndex = 0;
            foreach (CaptureTargetDescriptor target in targets)
            {
                int index = targetSelector.Items.Add(target);
                if (target.Handle == previousHandle) selectedIndex = index;
            }

            targetSelector.SelectedIndex = selectedIndex;
            LongCaptureLog.Info($"target list refreshed visibleTargets={targets.Count} preserved={(selectedIndex > 0)}");
        }
        catch (Exception ex)
        {
            targetSelector.Items.Clear();
            targetSelector.Items.Add(ShareXPickerLabel);
            targetSelector.SelectedIndex = 0;
            LongCaptureLog.Error("target list refresh failed", ex);
        }
        finally
        {
            targetSelector.EndUpdate();
        }
    }

    private void LogSelectedTarget()
    {
        CaptureTargetDescriptor? target = SelectedTarget;
        if (target is null)
        {
            LongCaptureLog.Info("capture target changed to ShareX picker fallback");
            return;
        }

        LongCaptureLog.Info(
            $"capture target selected hwnd={target.HandleHex} pid={target.ProcessId} process={LongCaptureLog.OneLine(target.ProcessName)} title={LongCaptureLog.OneLine(target.Title)} bounds={target.Bounds}");
    }

    private async Task RefreshModeUiAsync()
    {
        LongCaptureStandaloneMode mode = SelectedMode;
        bool browserMode = mode != LongCaptureStandaloneMode.Normal;
        bool recipeMode = mode is LongCaptureStandaloneMode.Teach or LongCaptureStandaloneMode.RunRecipe;
        bool runMode = mode == LongCaptureStandaloneMode.RunRecipe;

        browserButton.Enabled = browserMode && !captureBusy;
        recipePath.Enabled = runMode && !captureBusy;
        browseRecipeButton.Enabled = runMode && !captureBusy;
        reviewButton.Enabled = runMode && !captureBusy;

        if (recipeMode && string.IsNullOrWhiteSpace(recipePath.Text))
        {
            recipePath.Text = LongCaptureStandaloneBridge.GetLatestRecipePath();
        }

        LongCaptureStandaloneBridge.ConfigureMode(mode, runMode ? recipePath.Text : null);
        LongCaptureLog.Info($"mode configured mode={mode}");
        await RefreshReadinessAsync(showDialogOnFailure: false);
    }

    private async Task<bool> RefreshReadinessAsync(bool showDialogOnFailure)
    {
        LongCaptureStandaloneMode mode = SelectedMode;
        string? recipe = mode == LongCaptureStandaloneMode.RunRecipe ? recipePath.Text : null;
        LongCaptureStandaloneReadiness readiness = await LongCaptureStandaloneBridge.ProbeAsync(mode, recipe);
        readinessLabel.Text = readiness.Ready ? "Ready · " + readiness.Detail : "Not ready · " + readiness.Detail;
        LongCaptureLog.Info($"readiness mode={mode} ready={readiness.Ready} detail={LongCaptureLog.OneLine(readiness.Detail)}");

        if (!string.IsNullOrWhiteSpace(readiness.RecipePath) && mode == LongCaptureStandaloneMode.RunRecipe)
        {
            recipePath.Text = readiness.RecipePath;
        }

        if (!readiness.Ready && showDialogOnFailure)
        {
            MessageBox.Show(this,
                readiness.Detail + "\n\nFor Smart Web / Teach / Run Recipe, open the Capture Browser first and navigate to the page you want to capture.",
                "LongCapture is not ready for this mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        return readiness.Ready;
    }

    private async Task LaunchCaptureBrowserAsync()
    {
        browserButton.Enabled = false;
        readinessLabel.Text = "Starting Capture Browser...";
        LongCaptureLog.Info("Capture Browser launch requested");
        try
        {
            LongCaptureBrowserLaunchResult result = await LongCaptureStandaloneBridge.LaunchCaptureBrowserAsync();
            readinessLabel.Text = result.Started ? "Capture Browser ready · " + result.Detail : "Capture Browser failed · " + result.Detail;
            LongCaptureLog.Info($"Capture Browser launch started={result.Started} detail={LongCaptureLog.OneLine(result.Detail)}");
            if (!result.Started)
            {
                MessageBox.Show(this, result.Detail, "Capture Browser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                await Task.Delay(250);
                RefreshTargetList();
                await RefreshReadinessAsync(showDialogOnFailure: false);
            }
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("Capture Browser launch failed", ex);
            MessageBox.Show(this, ex.Message, "Capture Browser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            browserButton.Enabled = SelectedMode != LongCaptureStandaloneMode.Normal && !captureBusy;
        }
    }

    private void BrowseRecipe()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Capture Recipe (capture-recipe.json)|capture-recipe.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Title = "Select Capture Recipe"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            recipePath.Text = dialog.FileName;
            LongCaptureStandaloneBridge.ConfigureMode(LongCaptureStandaloneMode.RunRecipe, dialog.FileName);
            LongCaptureLog.Info($"recipe selected path={LongCaptureLog.OneLine(dialog.FileName)}");
            _ = RefreshReadinessAsync(showDialogOnFailure: false);
        }
    }

    private void ReviewRecipe()
    {
        string path = recipePath.Text;
        if (string.IsNullOrWhiteSpace(path)) path = LongCaptureStandaloneBridge.GetLatestRecipePath();
        LongCaptureRecipeReviewInfo? info = LongCaptureStandaloneBridge.LoadRecipeReview(path);
        if (info is null)
        {
            LongCaptureLog.Warn($"recipe review failed path={LongCaptureLog.OneLine(path)}");
            MessageBox.Show(this, "No valid Capture Recipe could be loaded.", "Recipe review", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        LongCaptureLog.Info($"recipe review opened path={LongCaptureLog.OneLine(info.RecipePath)}");
        using var review = new RecipeReviewForm(info);
        if (review.ShowDialog(this) == DialogResult.OK)
        {
            recipePath.Text = info.RecipePath;
            LongCaptureLog.Info($"recipe review approved path={LongCaptureLog.OneLine(info.RecipePath)}");
            _ = RefreshReadinessAsync(showDialogOnFailure: false);
        }
    }

    private async Task ToggleCaptureAsync()
    {
        if (captureBusy)
        {
            RequestStop();
            return;
        }

        LongCaptureStandaloneMode mode = SelectedMode;
        if (!await RefreshReadinessAsync(showDialogOnFailure: true))
        {
            LongCaptureLog.Warn($"capture blocked by readiness mode={mode}");
            return;
        }

        LongCaptureStandaloneBridge.ConfigureMode(
            mode,
            mode == LongCaptureStandaloneMode.RunRecipe ? recipePath.Text : null);

        CaptureTargetDescriptor? requestedTarget = SelectedTarget;
        captureBusy = true;
        SetControlsEnabled(false);
        captureButton.Text = "Stop capture   (F8)";
        statusLabel.Text = requestedTarget is null
            ? (mode == LongCaptureStandaloneMode.Normal
                ? "Select the scrolling window or region..."
                : "Select the Capture Browser window or desired capture region...")
            : $"Locking capture to: {requestedTarget.Title}";

        int settle = (int)scrollDelay.Value;
        var options = new ScrollingCaptureOptions
        {
            StartDelay = (int)startDelay.Value,
            ScrollDelay = settle,
            ScrollAmount = (int)scrollAmount.Value,
            ScrollMethod = Enum.TryParse(scrollMethod.SelectedItem?.ToString(), out ScrollMethod method) ? method : ScrollMethod.MouseWheel,
            AutoScrollTop = autoScrollTop.Checked,
            AutoIgnoreBottomEdge = true,
            AutoUpload = false,
            ShowRegion = true,
            AdaptiveSettle = true,
            AdaptiveSettleProbeInterval = 100,
            AdaptiveSettleStableSamples = 2,
            AdaptiveSettleMaxDelay = Math.Min(4500, Math.Max(1800, settle * 5)),
            AdaptiveSettleChangedFraction = 0.012,
            SuppressStationaryOverlays = true
        };

        string targetSummary = requestedTarget is null
            ? "ShareX-picker-fallback"
            : $"hwnd={requestedTarget.HandleHex} pid={requestedTarget.ProcessId} title={LongCaptureLog.OneLine(requestedTarget.Title)}";
        LongCaptureLog.Info(
            $"capture requested mode={mode} target={targetSummary} startDelay={options.StartDelay} scrollDelay={options.ScrollDelay} scrollAmount={options.ScrollAmount} scrollMethod={options.ScrollMethod} autoScrollTop={options.AutoScrollTop} adaptiveSettle={options.AdaptiveSettle} settleMax={options.AdaptiveSettleMaxDelay} stationarySuppression={options.SuppressStationaryOverlays}");

        CaptureSessionRecorder? recorder = null;
        ScrollingCaptureStatus completionStatus = ScrollingCaptureStatus.Failed;
        string? completionPath = null;
        Size? completionSize = null;

        try
        {
            using var service = new ScrollingCaptureService(options);
            activeService = service;

            Hide();
            await Task.Delay(180);

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

            recorder = new CaptureSessionRecorder(mode.ToString(), targetSummary, options);
            recorder.Attach(options);

            trayStopItem.Enabled = true;
            trayIcon.Visible = true;

            CaptureExclusion.ApplyToCurrentProcessTopLevelWindows("pre-capture");
            LongCaptureLog.Info($"capture UI quiet period started delayMs={CaptureUiQuietPeriodMs}");
            await Task.Delay(CaptureUiQuietPeriodMs);
            CaptureExclusion.ApplyToCurrentProcessTopLevelWindows("post-quiet-pre-frame");
            LongCaptureLog.Info($"capture UI quiet period completed delayMs={CaptureUiQuietPeriodMs}");
            LongCaptureLog.Info("capture engine start requested");

            completionStatus = await service.StartCaptureAsync();
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            LongCaptureLog.Info($"capture engine completed status={completionStatus}");

            if (service.Result is not null && service.Result.Width > 0 && service.Result.Height > 0)
            {
                Directory.CreateDirectory(outputDirectory);
                completionPath = Path.Combine(outputDirectory, $"LongCapture_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
                service.Result.Save(completionPath, ImageFormat.Png);
                completionSize = service.Result.Size;
                LongCaptureLog.Info($"capture image saved width={completionSize.Value.Width} height={completionSize.Value.Height} path={LongCaptureLog.OneLine(completionPath)}");
            }

            latestSessionQuality = recorder.Complete(completionStatus, completionPath, completionSize);
            ShowMainWindow();

            if (mode == LongCaptureStandaloneMode.Teach)
            {
                string latest = LongCaptureStandaloneBridge.GetLatestRecipePath();
                if (!string.IsNullOrWhiteSpace(latest)) recipePath.Text = latest;
                LongCaptureLog.Info($"Teach mode latest recipe={LongCaptureLog.OneLine(latest)}");
            }

            UpdateQualityLabel();

            if (completionPath is not null && completionSize.HasValue)
            {
                statusLabel.Text = $"{completionStatus}: {completionSize.Value.Width} × {completionSize.Value.Height}px — {completionPath}";
            }
            else
            {
                statusLabel.Text = $"Capture ended with status {completionStatus}; no usable image was produced.";
                LongCaptureLog.Warn($"capture produced no usable stitched image status={completionStatus}");
                MessageBox.Show(this,
                    "LongCapture did not receive a usable stitched image. The capture-session diagnostics were retained automatically.\n\nApp log: " + LongCaptureLog.CurrentLogPath + "\nSession: " + recorder.SessionDirectory,
                    "LongCapture",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            ShowMainWindow();
            statusLabel.Text = "Capture failed.";
            LongCaptureLog.Error("capture failed", ex);

            if (recorder is not null)
            {
                try
                {
                    latestSessionQuality = recorder.Complete(ScrollingCaptureStatus.Failed, completionPath, completionSize);
                }
                catch (Exception qualityEx)
                {
                    LongCaptureLog.Warn($"failed to finalize capture-session diagnostics type={qualityEx.GetType().Name} message={LongCaptureLog.OneLine(qualityEx.Message)}");
                }
            }

            MessageBox.Show(this,
                ex.Message + "\n\nDiagnostic log: " + LongCaptureLog.CurrentLogPath +
                (recorder is null ? string.Empty : "\nCapture session: " + recorder.SessionDirectory),
                "LongCapture capture error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            recorder?.Dispose();
            activeService = null;
            captureBusy = false;
            captureButton.Text = "Start long capture   (F8)";
            SetControlsEnabled(true);
            LongCaptureLog.Info("capture lifecycle cleanup completed");
            await RefreshModeUiAsync();
        }
    }

    private void UpdateQualityLabel()
    {
        if (latestSessionQuality is not null)
        {
            qualityLabel.Text = $"Quality: {latestSessionQuality.Status} · {latestSessionQuality.Score}/100 · frames {latestSessionQuality.TotalFrames} · low-confidence {latestSessionQuality.LowConfidenceStitches} · settle timeouts {latestSessionQuality.SettleTimeouts}";
            LongCaptureLog.Info(
                $"flight-recorder quality status={latestSessionQuality.Status} score={latestSessionQuality.Score}/100 frames={latestSessionQuality.TotalFrames} lowConfidence={latestSessionQuality.LowConfidenceStitches} settleTimeouts={latestSessionQuality.SettleTimeouts}");
            return;
        }

        LongCaptureQualityInfo? quality = LongCaptureStandaloneBridge.FindLatestQualityInfo();
        qualityLabel.Text = quality is null
            ? "Quality: no quality summary was produced for this capture."
            : $"Quality: {quality.Status} / {quality.Confidence} · integrity {quality.IntegrityScore}/100 · {quality.SummaryPath}";

        if (quality is null)
        {
            LongCaptureLog.Warn("quality summary was not produced for the latest capture");
        }
        else
        {
            LongCaptureLog.Info($"quality status={quality.Status} confidence={quality.Confidence} integrity={quality.IntegrityScore}/100 summary={LongCaptureLog.OneLine(quality.SummaryPath)}");
        }
    }

    private void RequestStop()
    {
        if (activeService?.IsCapturing == true)
        {
            trayStopItem.Enabled = false;
            LongCaptureLog.Info("capture stop forwarded to ShareX scrolling engine");
            activeService.StopCapture();
        }
        else
        {
            LongCaptureLog.Warn("capture stop requested while engine was not capturing");
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        modeSelector.Enabled = enabled;
        targetSelector.Enabled = enabled;
        refreshTargetsButton.Enabled = enabled;
        openLogsButton.Enabled = true;
        exportDiagnosticsButton.Enabled = true;
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
        else
        {
            browserButton.Enabled = false;
            recipePath.Enabled = false;
            browseRecipeButton.Enabled = false;
            reviewButton.Enabled = false;
        }
    }

    private void ShowMainWindow()
    {
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OpenOutputDirectory()
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
                UseShellExecute = true
            });
            LongCaptureLog.Info($"opened output directory path={LongCaptureLog.OneLine(outputDirectory)}");
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("failed to open output directory", ex);
            MessageBox.Show(this, ex.Message, "Open output folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LongCaptureLog.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LongCaptureLog.LogDirectory,
                UseShellExecute = true
            });
            LongCaptureLog.Info($"opened log directory path={LongCaptureLog.OneLine(LongCaptureLog.LogDirectory)}");
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("failed to open log directory", ex);
            MessageBox.Show(this, ex.Message, "Open logs folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

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
            MessageBox.Show(this,
                "Diagnostic bundle created:\n\n" + bundle + "\n\nReview it before sharing publicly because window titles and local file paths can appear in diagnostics.",
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
}
