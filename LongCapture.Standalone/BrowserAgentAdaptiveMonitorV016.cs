using System.Runtime.InteropServices;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentAdaptiveMonitorV016 : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Label headline = new();
    private readonly Label effective = new();
    private readonly Label actual = new();
    private readonly Label risk = new();
    private readonly Label learning = new();

    public BrowserAgentAdaptiveMonitorV016()
    {
        Text = "LongCapture · Adaptive live";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(520, 146);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(10, 8, 10, 8);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        for (int i = 0; i < 5; i++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 28 : 24));

        headline.Font = new Font(Font, FontStyle.Bold);
        foreach (Label label in new[] { headline, effective, actual, risk, learning })
        {
            label.Dock = DockStyle.Fill;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            panel.Controls.Add(label);
        }

        Controls.Add(panel);
        ApplySnapshot(BrowserAgentAdaptiveTelemetryHub.Last);
        BrowserAgentAdaptiveTelemetryHub.Updated += OnUpdated;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(this, "browser-adaptive-live-monitor");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Rectangle working = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            Math.Max(working.Left, working.Right - Width - 18),
            Math.Max(working.Top, working.Top + 18));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) BrowserAgentAdaptiveTelemetryHub.Updated -= OnUpdated;
        base.Dispose(disposing);
    }

    private void OnUpdated(BrowserAgentAdaptiveRuntimeSnapshot snapshot)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => ApplySnapshot(snapshot))); }
            catch (InvalidOperationException) { }
            return;
        }
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(BrowserAgentAdaptiveRuntimeSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            headline.Text = "Waiting for first Browser Assisted frame...";
            effective.Text = "Effective: —";
            actual.Text = "Measured: —";
            risk.Text = "Risk: —";
            learning.Text = "Local calibration: —";
            return;
        }

        headline.Text = $"Frame {snapshot.Frame} · target {snapshot.TargetSpeed} · current {snapshot.CurrentSpeed}";
        effective.Text = $"Effective: start {snapshot.StartDelayMs}ms · settle {snapshot.StableWindowMs}ms · max {snapshot.MaxWaitMs}ms · overlap {snapshot.OverlapRatio:P0}";
        actual.Text = $"Measured: settle {snapshot.ActualSettleMs}ms · activity {snapshot.ActivityMs}ms · capture {snapshot.CaptureMs}ms";
        risk.Text = $"Risk {snapshot.RiskScore}: {snapshot.RiskReasons}";
        learning.Text = $"Local calibration: {snapshot.CalibrationConfidence:P0} confidence · {snapshot.CalibrationSamples} samples";
    }
}
