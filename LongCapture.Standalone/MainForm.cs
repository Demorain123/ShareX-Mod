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
    private readonly Button captureButton = new();
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
        MinimumSize = new Size(720, 520);
        Size = new Size(800, 600);
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "LongCapture",
            Dock = DockStyle.Top,
            Height = 60,
            Font = new Font("Segoe UI Semibold", 22F),
            Padding = new Padding(18, 12, 0, 0)
        };

        var subtitle = new Label
        {
            Text = "Independent scrolling capture — ShareX capture engine, no ShareX.exe launch required",
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(20, 0, 0, 8)
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 8
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
        captureButton.Height = 54;
        captureButton.Dock = DockStyle.Top;
        captureButton.Font = new Font("Segoe UI Semibold", 12F);
        captureButton.Click += async (_, _) => await ToggleCaptureAsync();

        var openOutputButton = new Button { Text = "Open output folder", Dock = DockStyle.Left, Width = 150 };
        openOutputButton.Click += (_, _) => OpenOutputDirectory();

        AddRow(body, 0, "Start delay (ms)", startDelay);
        AddRow(body, 1, "Scroll settle (ms)", scrollDelay);
        AddRow(body, 2, "Scroll amount", scrollAmount);
        AddRow(body, 3, "Scroll method", scrollMethod);
        AddRow(body, 4, "Start position", autoScrollTop);
        AddRow(body, 5, "Output", outputLabel);
        AddRow(body, 6, "", openOutputButton);

        var actionPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 18, 0, 0) };
        actionPanel.Controls.Add(captureButton);
        body.Controls.Add(actionPanel, 0, 7);
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
            if (captureBusy)
            {
                RequestStop();
            }
            else
            {
                BeginInvoke(new Action(() => _ = ToggleCaptureAsync()));
            }
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

    private async Task ToggleCaptureAsync()
    {
        if (captureBusy)
        {
            RequestStop();
            return;
        }

        captureBusy = true;
        SetControlsEnabled(false);
        captureButton.Text = "Stop capture   (F8)";
        statusLabel.Text = "Select the scrolling window or region...";

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
        }
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
        startDelay.Enabled = enabled;
        scrollDelay.Enabled = enabled;
        scrollAmount.Enabled = enabled;
        scrollMethod.Enabled = enabled;
        autoScrollTop.Enabled = enabled;
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
