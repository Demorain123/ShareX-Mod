namespace LongCapture.Standalone;

internal sealed class BrowserAgentEndConditionUiV018
{
    private readonly ComboBox stopMode = new();
    private readonly NumericUpDown stopValue = new();
    private readonly Label stopHint = new();
    private readonly CheckBox backgroundWindow = new();
    private readonly TableLayoutPanel panel = new();

    private Control? normalRow3Control;
    private string normalRow3Label = "Recipe";
    private bool initialized;
    private bool browserMode;

    public BrowserAgentEndConditionUiV018()
    {
        stopMode.DropDownStyle = ComboBoxStyle.DropDownList;
        stopMode.Items.AddRange(new object[]
        {
            "Auto · page end / F8",
            "DOM progress ≥",
            "Frames ≥",
            "Elapsed minutes ≥"
        });
        stopMode.SelectedIndex = 0;
        stopMode.Width = 180;
        stopMode.MinimumSize = new Size(180, 34);
        stopMode.Margin = new Padding(0, 0, 8, 4);

        stopValue.Minimum = 0;
        stopValue.Maximum = 100000;
        stopValue.Value = 0;
        stopValue.Width = 105;
        stopValue.MinimumSize = new Size(105, 34);
        stopValue.Margin = new Padding(0, 0, 10, 4);
        stopValue.Enabled = false;

        stopHint.AutoSize = true;
        stopHint.ForeColor = Color.DimGray;
        stopHint.Margin = new Padding(0, 7, 0, 4);

        backgroundWindow.AutoSize = true;
        backgroundWindow.Checked = true;
        backgroundWindow.Text = "Allow target browser window behind other apps (tab stays active; not minimized)";
        backgroundWindow.Margin = new Padding(0, 4, 0, 0);
        backgroundWindow.Padding = new Padding(0, 4, 0, 4);

        panel.Dock = DockStyle.Fill;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.ColumnCount = 3;
        panel.RowCount = 2;
        panel.Margin = Padding.Empty;
        panel.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(stopMode, 0, 0);
        panel.Controls.Add(stopValue, 1, 0);
        panel.Controls.Add(stopHint, 2, 0);
        panel.Controls.Add(backgroundWindow, 0, 1);
        panel.SetColumnSpan(backgroundWindow, 3);

        stopMode.SelectionChangeCommitted += (_, _) =>
        {
            ConfigureValue();
            LongCaptureLog.Info($"[USER_ACTION] browser-setting stopMode={SelectedMode} stopValue={SelectedValue}");
        };
        stopValue.ValueChanged += (_, _) =>
        {
            if (!browserMode) return;
            UpdateHint();
            LongCaptureLog.Info($"[USER_ACTION] browser-setting stopValue={SelectedValue} mode={SelectedMode}");
        };
        backgroundWindow.CheckedChanged += (_, _) =>
        {
            if (!browserMode) return;
            LongCaptureLog.Info($"[USER_ACTION] browser-setting allowBackgroundWindow={backgroundWindow.Checked}");
        };
        ConfigureValue();
    }

    public BrowserAgentStopModeV018 SelectedMode =>
        (BrowserAgentStopModeV018)Math.Clamp(stopMode.SelectedIndex, 0, 3);

    public int SelectedValue => (int)stopValue.Value;

    public void SetMode(bool browserMode, TableLayoutPanel body)
    {
        Initialize(body);
        this.browserMode = browserMode;
        body.SuspendLayout();
        try
        {
            if (browserMode)
            {
                SetRowLabel(body, 3, "Capture end");
                ReplaceRowControl(body, 3, panel);
                panel.Visible = true;
            }
            else
            {
                SetRowLabel(body, 3, normalRow3Label);
                if (normalRow3Control is not null) ReplaceRowControl(body, 3, normalRow3Control);
            }
        }
        finally
        {
            body.ResumeLayout(true);
        }
        UpdateHint();
    }

    public BrowserAgentCaptureOptions EnrichOptions(BrowserAgentCaptureOptions source)
    {
        return new BrowserAgentCaptureOptions
        {
            StartDelayMs = source.StartDelayMs,
            StableWindowMs = source.StableWindowMs,
            OverlapRatio = source.OverlapRatio,
            PreloadDynamicContent = source.PreloadDynamicContent,
            PreloadMaxSeconds = source.PreloadMaxSeconds,
            RequireRegionSelection = source.RequireRegionSelection,
            SpeedStrategy = source.SpeedStrategy,
            RepairPrecision = source.RepairPrecision,
            UseLocalCalibration = source.UseLocalCalibration,
            ShowLiveAdaptiveMonitor = source.ShowLiveAdaptiveMonitor,
            StopMode = SelectedMode,
            StopValue = SelectedValue,
            AllowBackgroundWindow = backgroundWindow.Checked
        }.Normalize();
    }

    private void ConfigureValue()
    {
        switch (SelectedMode)
        {
            case BrowserAgentStopModeV018.AutoPageEnd:
                stopValue.Enabled = false;
                stopValue.Minimum = 0;
                stopValue.Maximum = 0;
                stopValue.Value = 0;
                break;
            case BrowserAgentStopModeV018.DomCounter:
                stopValue.Enabled = true;
                stopValue.Minimum = 1;
                stopValue.Maximum = 100000;
                if (stopValue.Value < 1) stopValue.Value = 1;
                break;
            case BrowserAgentStopModeV018.FrameCount:
                stopValue.Enabled = true;
                stopValue.Minimum = 2;
                stopValue.Maximum = 1200;
                if (stopValue.Value < 2) stopValue.Value = 100;
                break;
            case BrowserAgentStopModeV018.ElapsedMinutes:
                stopValue.Enabled = true;
                stopValue.Minimum = 1;
                stopValue.Maximum = 1440;
                if (stopValue.Value < 1) stopValue.Value = 5;
                break;
        }
        UpdateHint();
    }

    private void UpdateHint()
    {
        stopHint.Text = BrowserAgentStopPolicyV018.Describe(SelectedMode, SelectedValue);
    }

    private void Initialize(TableLayoutPanel body)
    {
        if (initialized) return;
        normalRow3Control = body.GetControlFromPosition(1, 3);
        normalRow3Label = (body.GetControlFromPosition(0, 3) as Label)?.Text ?? normalRow3Label;
        initialized = true;
    }

    private static void ReplaceRowControl(TableLayoutPanel body, int row, Control replacement)
    {
        Control? existing = body.GetControlFromPosition(1, row);
        if (ReferenceEquals(existing, replacement)) return;
        if (existing is not null) body.Controls.Remove(existing);
        body.Controls.Add(replacement, 1, row);
    }

    private static void SetRowLabel(TableLayoutPanel body, int row, string text)
    {
        if (body.GetControlFromPosition(0, row) is Label label) label.Text = text;
    }
}
