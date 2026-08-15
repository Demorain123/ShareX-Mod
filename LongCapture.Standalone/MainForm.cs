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

    private readonly Label statusLabel = new();
    private readonly Label readinessLabel = new();
    private readonly Label qualityLabel = new();
    private readonly Button captureButton = new();
    private readonly Button browserButton = new();
    private readonly Button reviewButton = new();
    private readonly Button browseRecipeButton = new();
    private readonly ComboBox modeSelector = new();
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

        Text = "LongCapture Standalone v0.1-dev";
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
            Text = "Independent long screenshot workspace — ShareX capture engine, quality guard, Smart Web and Capture Recipes",
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(20, 0, 0, 8)
        };

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
        openOutputButton.Location = new Point(0, 8);
        actionPanel.Controls.Add(openOutputButton);
        actionPanel.Controls.Add(qualityLabel);
        actionPanel.Controls.Add(captureButton);
        body.Controls.Add(actionPanel, 0, 11);
        body.SetColumnSpan(actionPanel, 2);

        statusLabel.Text = "Ready. Press F8 or click Start long capture.";
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
        Controls.Add(subtitle);
        Controls.Add(title);
        Controls.Add(statusLabel);

        Shown += async (_, _) => await RefreshModeUiAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, VK_F8);
        if (!hotkeyRegistered)
        {
            statusLabel.Text = "F8 is already in use by another app. Use the Start button; tray Stop remains available.";
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
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
        await RefreshReadinessAsync(showDialogOnFailure: false);
    }

    private async Task<bool> RefreshReadinessAsync(bool showDialogOnFailure)
    {
        LongCaptureStandaloneMode mode = SelectedMode;
        string? recipe = mode == LongCaptureStandaloneMode.RunRecipe ? recipePath.Text : null;
        LongCaptureStandaloneReadiness readiness = await LongCaptureStandaloneBridge.ProbeAsync(mode, recipe);
        readinessLabel.Text = readiness.Ready ? "Ready · " + readiness.Detail : "Not ready · " + readiness.Detail;

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
        try
        {
            LongCaptureBrowserLaunchResult result = await LongCaptureStandaloneBridge.LaunchCaptureBrowserAsync();
            readinessLabel.Text = result.Started ? "Capture Browser ready · " + result.Detail : "Capture Browser failed · " + result.Detail;
            if (!result.Started)
            {
                MessageBox.Show(this, result.Detail, "Capture Browser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                await Task.Delay(250);
                await RefreshReadinessAsync(showDialogOnFailure: false);
            }
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
            MessageBox.Show(this, "No valid Capture Recipe could be loaded.", "Recipe review", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var review = new RecipeReviewForm(info);
        if (review.ShowDialog(this) == DialogResult.OK)
        {
            recipePath.Text = info.RecipePath;
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
        if (!await RefreshReadinessAsync(showDialogOnFailure: true)) return;

        LongCaptureStandaloneBridge.ConfigureMode(
            mode,
            mode == LongCaptureStandaloneMode.RunRecipe ? recipePath.Text : null);

        captureBusy = true;
        SetControlsEnabled(false);
        captureButton.Text = "Stop capture   (F8)";
        statusLabel.Text = mode == LongCaptureStandaloneMode.Normal
            ? "Select the scrolling window or region..."
            : "Select the Capture Browser window or desired capture region...";

        var options = new ScrollingCaptureOptions
        {
            StartDelay = (int)startDelay.Value,
            ScrollDelay = (int)scrollDelay.Value,
            ScrollAmount = (int)scrollAmount.Value,
            ScrollMethod = Enum.TryParse(scrollMethod.SelectedItem?.ToString(), out ScrollMethod method) ? method : ScrollMethod.MouseWheel,
            AutoScrollTop = autoScrollTop.Checked,
            AutoIgnoreBottomEdge = true,
            AutoUpload = false,
            ShowRegion = true
        };

        try
        {
            using var service = new ScrollingCaptureService(options);
            activeService = service;

            Hide();
            await Task.Delay(180);

            if (!service.SelectWindow())
            {
                ShowMainWindow();
                statusLabel.Text = "Capture cancelled before start.";
                return;
            }

            trayStopItem.Enabled = true;
            trayIcon.Visible = true;
            trayIcon.ShowBalloonTip(1800, "LongCapture is running", "Press F8 or use the tray menu to stop at any point.", ToolTipIcon.Info);

            ScrollingCaptureStatus status = await service.StartCaptureAsync();
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;

            string? savedPath = null;
            Size? resultSize = null;
            if (service.Result is not null && service.Result.Width > 0 && service.Result.Height > 0)
            {
                Directory.CreateDirectory(outputDirectory);
                savedPath = Path.Combine(outputDirectory, $"LongCapture_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
                service.Result.Save(savedPath, ImageFormat.Png);
                resultSize = service.Result.Size;
            }

            ShowMainWindow();

            if (mode == LongCaptureStandaloneMode.Teach)
            {
                string latest = LongCaptureStandaloneBridge.GetLatestRecipePath();
                if (!string.IsNullOrWhiteSpace(latest)) recipePath.Text = latest;
            }

            UpdateQualityLabel();

            if (savedPath is not null && resultSize.HasValue)
            {
                statusLabel.Text = $"{status}: {resultSize.Value.Width} × {resultSize.Value.Height}px — {savedPath}";
            }
            else
            {
                statusLabel.Text = $"Capture ended with status {status}; no usable image was produced.";
                MessageBox.Show(this, "LongCapture did not receive a usable stitched image. Try a larger scrolling region or a different scroll method.", "LongCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            trayStopItem.Enabled = false;
            trayIcon.Visible = false;
            ShowMainWindow();
            statusLabel.Text = "Capture failed.";
            MessageBox.Show(this, ex.Message, "LongCapture capture error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            activeService = null;
            captureBusy = false;
            captureButton.Text = "Start long capture   (F8)";
            SetControlsEnabled(true);
            await RefreshModeUiAsync();
        }
    }

    private void UpdateQualityLabel()
    {
        LongCaptureQualityInfo? quality = LongCaptureStandaloneBridge.FindLatestQualityInfo();
        qualityLabel.Text = quality is null
            ? "Quality: no quality summary was produced for this capture."
            : $"Quality: {quality.Status} / {quality.Confidence} · integrity {quality.IntegrityScore}/100 · {quality.SummaryPath}";
    }

    private void RequestStop()
    {
        if (activeService?.IsCapturing == true)
        {
            trayStopItem.Enabled = false;
            activeService.StopCapture();
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        modeSelector.Enabled = enabled;
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
        Directory.CreateDirectory(outputDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = outputDirectory,
            UseShellExecute = true
        });
    }
}
