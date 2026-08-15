using System;
using System.Drawing;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal sealed class MainForm : Form
{
    private readonly Label statusLabel = new();

    public MainForm()
    {
        Text = "LongCapture Standalone v0.1-dev";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        Size = new Size(900, 620);
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "LongCapture",
            Dock = DockStyle.Top,
            Height = 64,
            Font = new Font("Segoe UI Semibold", 22F),
            Padding = new Padding(18, 14, 0, 0)
        };

        var subtitle = new Label
        {
            Text = "Independent long screenshot workspace built on ShareX capture foundations",
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(20, 0, 0, 10)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 2
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        grid.Controls.Add(CreateModeButton("Normal Long Capture", "Window / app / browser scrolling capture", CaptureMode.Normal), 0, 0);
        grid.Controls.Add(CreateModeButton("Smart Web Capture", "Lazy-load / fixed UI / quality guard", CaptureMode.SmartWeb), 1, 0);
        grid.Controls.Add(CreateModeButton("Teach Capture", "Demonstrate a workflow once and record it", CaptureMode.Teach), 0, 1);
        grid.Controls.Add(CreateModeButton("Run Recipe", "Replay a saved capture workflow", CaptureMode.RunRecipe), 1, 1);

        statusLabel.Text = "Ready";
        statusLabel.Dock = DockStyle.Bottom;
        statusLabel.Height = 42;
        statusLabel.Padding = new Padding(18, 10, 0, 0);

        Controls.Add(grid);
        Controls.Add(subtitle);
        Controls.Add(title);
        Controls.Add(statusLabel);
    }

    private Control CreateModeButton(string title, string description, CaptureMode mode)
    {
        var button = new Button
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(8),
            Text = $"{title}\r\n\r\n{description}",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18),
            Font = new Font("Segoe UI Semibold", 12F),
            Tag = mode
        };
        button.Click += (_, _) => OnModeSelected(mode);
        return button;
    }

    private void OnModeSelected(CaptureMode mode)
    {
        switch (mode)
        {
            case CaptureMode.Normal:
                statusLabel.Text = "Normal Long Capture selected — capture engine integration is starting.";
                MessageBox.Show(this,
                    "Normal Long Capture is the primary capture path for this standalone build.\n\nThe next integration step opens ShareX's scrolling-capture engine without launching ShareX.exe.",
                    "LongCapture", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case CaptureMode.SmartWeb:
                statusLabel.Text = "Smart Web Capture: advanced web quality pipeline.";
                MessageBox.Show(this, "Smart Web Capture is reserved for the browser-enhanced quality pipeline.", "LongCapture");
                break;
            case CaptureMode.Teach:
                statusLabel.Text = "Teach Capture: workflow recording mode.";
                MessageBox.Show(this, "Teach Capture records capture intent and workflow steps rather than raw mouse coordinates.", "LongCapture");
                break;
            case CaptureMode.RunRecipe:
                statusLabel.Text = "Run Recipe: workflow replay mode.";
                MessageBox.Show(this, "Run Recipe replays a saved capture workflow with quality checks.", "LongCapture");
                break;
        }
    }
}
