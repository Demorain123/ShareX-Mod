using System.Diagnostics;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentPocForm : Form
{
    private readonly BrowserAgentBridgeServer bridge = new();
    private readonly Label connectionLabel = new();
    private readonly Label targetLabel = new();
    private readonly Label statusLabel = new();
    private readonly NumericUpDown maxFramesInput = new();
    private readonly Button captureButton = new();
    private readonly Button stopButton = new();
    private readonly Button openExtensionButton = new();
    private readonly Button installHostButton = new();
    private readonly Button openLastButton = new();
    private CancellationTokenSource? captureCancellation;
    private string? lastSessionDirectory;

    public BrowserAgentPocForm()
    {
        Text = "LongCapture - Browser Agent v0.1 PoC";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 410);
        Size = new Size(820, 460);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 8,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Browser Assisted Capture v0.1 proof-of-concept",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(title);

        var instructions = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            Text = "This isolated PoC leaves Normal Long Capture untouched. Load the unpacked Chrome extension, install the native host using its extension ID, click the extension icon on the tab you want to capture, then press Start. Frames are saved individually; LongCapture performs the final DOM-geometry stitch.",
            Margin = new Padding(0, 0, 0, 12)
        };
        root.Controls.Add(instructions);

        connectionLabel.AutoSize = true;
        connectionLabel.Text = "Connection: waiting for extension";
        connectionLabel.Margin = new Padding(0, 2, 0, 3);
        root.Controls.Add(connectionLabel);

        targetLabel.AutoSize = true;
        targetLabel.MaximumSize = new Size(740, 0);
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
        maxFramesInput.Value = 180;
        maxFramesInput.Width = 72;
        captureRow.Controls.Add(maxFramesInput);

        captureButton.Text = "Start Browser Assisted Capture";
        captureButton.AutoSize = true;
        captureButton.Enabled = false;
        captureButton.Click += async (_, _) => await StartCaptureAsync();
        captureRow.Controls.Add(captureButton);

        stopButton.Text = "Stop";
        stopButton.AutoSize = true;
        stopButton.Enabled = false;
        stopButton.Click += (_, _) => captureCancellation?.Cancel();
        captureRow.Controls.Add(stopButton);

        openLastButton.Text = "Open last session";
        openLastButton.AutoSize = true;
        openLastButton.Enabled = false;
        openLastButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(lastSessionDirectory)) OpenPath(lastSessionDirectory);
        };
        captureRow.Controls.Add(openLastButton);
        root.Controls.Add(captureRow);

        statusLabel.AutoSize = true;
        statusLabel.MaximumSize = new Size(740, 0);
        statusLabel.Text = "Status: ready. Waiting for Browser Agent extension.";
        statusLabel.Margin = new Padding(0, 4, 0, 0);
        root.Controls.Add(statusLabel);

        bridge.ConnectionChanged += OnConnectionChanged;
        bridge.AgentAttached += OnAgentAttached;
        FormClosed += (_, _) => bridge.Dispose();
        FormClosing += (_, _) => captureCancellation?.Cancel();
    }

    private async Task StartCaptureAsync()
    {
        if (!bridge.IsConnected)
        {
            MessageBox.Show(this, "Click the Browser Agent extension icon on the target Chrome tab first.", "Browser Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        captureCancellation?.Dispose();
        captureCancellation = new CancellationTokenSource();
        captureButton.Enabled = false;
        stopButton.Enabled = true;
        openExtensionButton.Enabled = false;
        installHostButton.Enabled = false;

        try
        {
            var session = new BrowserAgentCaptureSession(bridge);
            BrowserAgentStitchResult result = await session.CaptureAsync(
                AppContext.BaseDirectory,
                (int)maxFramesInput.Value,
                UpdateStatusSafe,
                captureCancellation.Token);

            lastSessionDirectory = Path.GetDirectoryName(result.OutputPath);
            openLastButton.Enabled = true;
            UpdateStatusSafe($"Complete: {result.FrameCount} frames -> {result.Width}x{result.Height}. {result.OutputPath}");
            MessageBox.Show(
                this,
                $"Browser Agent PoC capture finished.\n\nFrames: {result.FrameCount}\nImage: {result.Width} x {result.Height}\n\n{result.OutputPath}",
                "Browser Agent v0.1",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("Browser Agent PoC capture failed", ex);
            UpdateStatusSafe("Failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Browser Agent capture failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            stopButton.Enabled = false;
            captureButton.Enabled = bridge.IsConnected;
            openExtensionButton.Enabled = true;
            installHostButton.Enabled = true;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        SafeUi(() =>
        {
            connectionLabel.Text = connected
                ? "Connection: native host connected"
                : "Connection: waiting for extension";
            captureButton.Enabled = connected && stopButton.Enabled == false;
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
            statusLabel.Text = "Status: Chrome tab attached. Ready to capture.";
            captureButton.Enabled = bridge.IsConnected && stopButton.Enabled == false;
        });
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
