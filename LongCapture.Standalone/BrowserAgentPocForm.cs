using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentPocForm : Form
{
    private const int HotkeyId = 0x4C46;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F8 = 0x77;

    private readonly BrowserAgentBridgeServer bridge = new();
    private readonly Label connectionLabel = new();
    private readonly Label targetLabel = new();
    private readonly Label statusLabel = new();
    private readonly NumericUpDown maxFramesInput = new();
    private readonly Button captureButton = new();
    private readonly Button stopButton = new();
    private readonly Button openExtensionButton = new();
    private readonly Button installHostButton = new();
    private readonly Button openLogsButton = new();
    private readonly Button openLastButton = new();
    private readonly Button exportDiagnosticsButton = new();

    private CancellationTokenSource? captureCancellation;
    private string? lastSessionDirectory;
    private bool hotkeyRegistered;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public BrowserAgentPocForm()
    {
        Text = "LongCapture - Browser Agent v0.1.1 Dynamic Page Stability";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 470);
        Size = new Size(940, 560);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 8,
            AutoScroll = true
        };
        for (int i = 0; i < 7; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Browser Assisted Capture v0.1.1 — dynamic-page stability",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(title);

        var instructions = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(860, 0),
            Text = "Attach the active Chromium tab by clicking the extension icon (or Ctrl+Shift+L), then press F8 to start/stop. v0.1.1 waits for DOM/layout stability, warms lazy-load boundaries, verifies raster overlap, and re-captures a recent viewport window when the page changes under capture.",
            Margin = new Padding(0, 0, 0, 12)
        };
        root.Controls.Add(instructions);

        connectionLabel.AutoSize = true;
        connectionLabel.Text = "Connection: waiting for extension";
        connectionLabel.Margin = new Padding(0, 2, 0, 3);
        root.Controls.Add(connectionLabel);

        targetLabel.AutoSize = true;
        targetLabel.MaximumSize = new Size(860, 0);
        targetLabel.Text = "Target: none";
        targetLabel.Margin = new Padding(0, 0, 0, 10);
        root.Controls.Add(targetLabel);

        var setupButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 10)
        };

        openExtensionButton.Text = "Open extension folder";
        openExtensionButton.AutoSize = true;
        openExtensionButton.Click += (_, _) => OpenPath(Path.Combine(AppContext.BaseDirectory, "BrowserAgent", "Extension"));
        setupButtons.Controls.Add(openExtensionButton);

        installHostButton.Text = "Install native host...";
        installHostButton.AutoSize = true;
        installHostButton.Click += (_, _) => StartHelper(Path.Combine(AppContext.BaseDirectory, "BrowserAgent", "INSTALL-BROWSER-AGENT.cmd"));
        setupButtons.Controls.Add(installHostButton);

        openLogsButton.Text = "Open logs";
        openLogsButton.AutoSize = true;
        openLogsButton.Click += (_, _) => OpenPath(LongCaptureLog.LogDirectory);
        setupButtons.Controls.Add(openLogsButton);
        root.Controls.Add(setupButtons);

        var captureRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        captureRow.Controls.Add(new Label
        {
            Text = "Maximum frames:",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });

        maxFramesInput.Minimum = 2;
        maxFramesInput.Maximum = 240;
        maxFramesInput.Value = 200;
        maxFramesInput.Width = 72;
        captureRow.Controls.Add(maxFramesInput);

        captureButton.Text = "Start Browser Assisted Capture (F8)";
        captureButton.AutoSize = true;
        captureButton.Enabled = false;
        captureButton.Click += async (_, _) => await StartCaptureAsync();
        captureRow.Controls.Add(captureButton);

        stopButton.Text = "Stop (F8)";
        stopButton.AutoSize = true;
        stopButton.Enabled = false;
        stopButton.Click += (_, _) => captureCancellation?.Cancel();
        captureRow.Controls.Add(stopButton);

        openLastButton.Text = "Open last session";
        openLastButton.AutoSize = true;
        openLastButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(lastSessionDirectory)) OpenPath(lastSessionDirectory);
        };
        captureRow.Controls.Add(openLastButton);

        exportDiagnosticsButton.Text = "Export diagnostics ZIP";
        exportDiagnosticsButton.AutoSize = true;
        exportDiagnosticsButton.Click += (_, _) => ExportDiagnostics();
        captureRow.Controls.Add(exportDiagnosticsButton);
        root.Controls.Add(captureRow);

        statusLabel.AutoSize = true;
        statusLabel.MaximumSize = new Size(860, 0);
        statusLabel.Text = "Status: ready. Waiting for Browser Agent extension. F8 is available once the tab is attached.";
        statusLabel.Margin = new Padding(0, 4, 0, 0);
        root.Controls.Add(statusLabel);

        lastSessionDirectory = FindLatestSessionDirectory();
        UpdateSessionButtons();

        bridge.ConnectionChanged += OnConnectionChanged;
        bridge.AgentAttached += OnAgentAttached;
        FormClosed += (_, _) => bridge.Dispose();
        FormClosing += (_, _) => captureCancellation?.Cancel();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, VK_F8);
        if (!hotkeyRegistered)
        {
            int error = Marshal.GetLastWin32Error();
            statusLabel.Text = "Status: F8 is already in use by another app/LongCapture window. Buttons still work.";
            LongCaptureLog.Warn($"Browser Agent global F8 registration failed win32={error}");
        }
        else
        {
            LongCaptureLog.Info("Browser Agent global F8 start/stop hotkey registered");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            LongCaptureLog.Info("Browser Agent global F8 hotkey unregistered");
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            if (stopButton.Enabled)
            {
                captureCancellation?.Cancel();
            }
            else if (bridge.IsConnected && captureButton.Enabled)
            {
                BeginInvoke(new Action(() => _ = StartCaptureAsync()));
            }
            return;
        }
        base.WndProc(ref m);
    }

    private async Task StartCaptureAsync()
    {
        if (!bridge.IsConnected)
        {
            MessageBox.Show(
                this,
                "Attach the target Chromium tab first by clicking the Browser Agent extension icon (or Ctrl+Shift+L).",
                "Browser Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        captureCancellation?.Dispose();
        captureCancellation = new CancellationTokenSource();
        captureButton.Enabled = false;
        stopButton.Enabled = true;
        openExtensionButton.Enabled = false;
        installHostButton.Enabled = false;

        BrowserAgentCaptureSession? session = null;
        try
        {
            session = new BrowserAgentCaptureSession(bridge);
            BrowserAgentStitchResult result = await session.CaptureAsync(
                AppContext.BaseDirectory,
                (int)maxFramesInput.Value,
                UpdateStatusSafe,
                captureCancellation.Token);

            lastSessionDirectory = Path.GetDirectoryName(result.OutputPath);
            UpdateSessionButtons();
            UpdateStatusSafe($"Complete: {result.FrameCount} verified frames -> {result.Width}x{result.Height}. {result.OutputPath}");
            MessageBox.Show(
                this,
                $"Browser Agent v0.1.1 capture finished.\n\nFrames: {result.FrameCount}\nImage: {result.Width} x {result.Height}\n\n{result.OutputPath}",
                "Browser Agent v0.1.1",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(session?.LastSessionDirectory))
            {
                lastSessionDirectory = session.LastSessionDirectory;
            }
            UpdateSessionButtons();

            string diagnosticSuffix = string.Empty;
            if (!string.IsNullOrWhiteSpace(lastSessionDirectory) && Directory.Exists(lastSessionDirectory))
            {
                try
                {
                    string zip = BrowserAgentDiagnosticsExporter.Export(lastSessionDirectory);
                    diagnosticSuffix = $"\n\nDiagnostics were exported automatically:\n{zip}";
                }
                catch (Exception exportEx)
                {
                    LongCaptureLog.Warn($"Browser Agent automatic diagnostics export failed: {LongCaptureLog.OneLine(exportEx.Message)}");
                }
            }

            LongCaptureLog.Error("Browser Agent v0.1.1 capture failed", ex);
            UpdateStatusSafe("Failed safely: " + ex.Message);
            MessageBox.Show(
                this,
                ex.Message + diagnosticSuffix,
                "Browser Agent capture stopped",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            stopButton.Enabled = false;
            captureButton.Enabled = bridge.IsConnected;
            openExtensionButton.Enabled = true;
            installHostButton.Enabled = true;
        }
    }

    private void ExportDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(lastSessionDirectory) || !Directory.Exists(lastSessionDirectory))
        {
            MessageBox.Show(this, "No Browser Agent session is available yet.", "Browser Agent diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            string zip = BrowserAgentDiagnosticsExporter.Export(lastSessionDirectory);
            UpdateStatusSafe("Diagnostics exported: " + zip);
            MessageBox.Show(this, zip, "Browser Agent diagnostics exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("Browser Agent diagnostics export failed", ex);
            MessageBox.Show(this, ex.Message, "Browser Agent diagnostics export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        SafeUi(() =>
        {
            connectionLabel.Text = connected
                ? "Connection: native host connected"
                : "Connection: waiting for extension";
            captureButton.Enabled = connected && !stopButton.Enabled;
            if (!connected)
            {
                targetLabel.Text = "Target: none";
            }
        });
    }

    private void OnAgentAttached(string summary)
    {
        SafeUi(() =>
        {
            targetLabel.Text = "Target: " + summary;
            statusLabel.Text = "Status: Chromium tab attached. Ready. Press F8 or Start.";
            captureButton.Enabled = bridge.IsConnected && !stopButton.Enabled;
        });
    }

    private void UpdateSessionButtons()
    {
        bool available = !string.IsNullOrWhiteSpace(lastSessionDirectory) && Directory.Exists(lastSessionDirectory);
        openLastButton.Enabled = available;
        exportDiagnosticsButton.Enabled = available;
    }

    private void UpdateStatusSafe(string message)
    {
        SafeUi(() => statusLabel.Text = "Status: " + message);
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Window is closing.
            }
            return;
        }
        action();
    }

    private static string? FindLatestSessionDirectory()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "BrowserAgentCaptures");
        if (!Directory.Exists(root)) return null;

        return Directory
            .EnumerateDirectories(root, "BrowserAgent-*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show($"Not found:\n{path}", "Browser Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private static void StartHelper(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"Not found:\n{path}", "Browser Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }
}
