namespace LongCapture.Standalone;

internal sealed class BrowserAgentAdaptiveUiV015
{
    private readonly ComboBox speedSelector = new();
    private readonly ComboBox precisionSelector = new();
    private readonly Label speedHint = new();
    private readonly Label precisionHint = new();
    private readonly TableLayoutPanel speedPanel = new();
    private readonly TableLayoutPanel precisionPanel = new();

    private Control? normalRow3Control;
    private Control? normalRow4Control;
    private string normalRow3Label = "Recipe";
    private string normalRow4Label = "Recipe actions";
    private bool initialized;
    private bool browserMode;
    private bool applyingPreset;

    public BrowserAgentAdaptiveUiV015()
    {
        speedSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        speedSelector.Items.AddRange(BrowserAgentAdaptiveProfiles.StrategyDisplayNames.Cast<object>().ToArray());
        speedSelector.SelectedIndex = (int)BrowserAgentSpeedStrategy.AdaptiveBalanced;
        speedSelector.Dock = DockStyle.Fill;

        precisionSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        precisionSelector.Items.AddRange(BrowserAgentAdaptiveProfiles.PrecisionDisplayNames.Cast<object>().ToArray());
        precisionSelector.SelectedIndex = (int)BrowserAgentRepairPrecision.Medium;
        precisionSelector.Dock = DockStyle.Left;
        precisionSelector.Width = 145;

        speedHint.Text = "Adaptive slows down on loading/layout risk, then gradually returns to the selected target speed.";
        speedHint.AutoSize = true;
        speedHint.ForeColor = Color.DimGray;
        speedHint.Padding = new Padding(8, 6, 0, 0);

        precisionHint.Text = "Controls how often suspicious sections are reviewed/re-captured.";
        precisionHint.AutoSize = true;
        precisionHint.ForeColor = Color.DimGray;
        precisionHint.Padding = new Padding(8, 6, 0, 0);

        BuildPanel(speedPanel, speedSelector, speedHint);
        BuildPanel(precisionPanel, precisionSelector, precisionHint);
    }

    public BrowserAgentSpeedStrategy Strategy => (BrowserAgentSpeedStrategy)Math.Clamp(speedSelector.SelectedIndex, 0, BrowserAgentAdaptiveProfiles.StrategyDisplayNames.Length - 1);
    public BrowserAgentRepairPrecision Precision => (BrowserAgentRepairPrecision)Math.Clamp(precisionSelector.SelectedIndex, 0, 2);

    public void AttachHandlers(
        NumericUpDown startDelay,
        NumericUpDown pageSettle,
        NumericUpDown overlap,
        CheckBox preScan)
    {
        speedSelector.SelectionChangeCommitted += (_, _) =>
        {
            ApplyPreset(startDelay, pageSettle, overlap, preScan);
            LongCaptureLog.Info($"[USER_ACTION] browser-setting speedStrategy={Strategy}");
        };
        precisionSelector.SelectionChangeCommitted += (_, _) =>
        {
            LongCaptureLog.Info($"[USER_ACTION] browser-setting repairPrecision={Precision}");
        };
    }

    public void SetMode(
        bool browserMode,
        TableLayoutPanel body,
        NumericUpDown startDelay,
        NumericUpDown pageSettle,
        NumericUpDown overlap,
        CheckBox preScan)
    {
        InitializeRows(body);
        if (browserMode == this.browserMode)
        {
            if (browserMode)
            {
                precisionSelector.Enabled = BrowserAgentAdaptiveProfiles.IsAdaptive(Strategy);
            }
            return;
        }

        this.browserMode = browserMode;
        body.SuspendLayout();
        try
        {
            if (browserMode)
            {
                SetRowLabel(body, 3, "Capture speed");
                SetRowLabel(body, 4, "Repair precision");
                ReplaceRowControl(body, 3, speedPanel);
                ReplaceRowControl(body, 4, precisionPanel);
                ApplyPreset(startDelay, pageSettle, overlap, preScan);
                precisionSelector.Enabled = BrowserAgentAdaptiveProfiles.IsAdaptive(Strategy);
            }
            else
            {
                SetRowLabel(body, 3, normalRow3Label);
                SetRowLabel(body, 4, normalRow4Label);
                if (normalRow3Control is not null) ReplaceRowControl(body, 3, normalRow3Control);
                if (normalRow4Control is not null) ReplaceRowControl(body, 4, normalRow4Control);
            }
        }
        finally
        {
            body.ResumeLayout(true);
        }
    }

    public BrowserAgentCaptureOptions EnrichOptions(BrowserAgentCaptureOptions source)
    {
        BrowserAgentFrameTuning tuning = BrowserAgentAdaptiveProfiles.BaseTuning(Strategy);
        bool adaptive = BrowserAgentAdaptiveProfiles.IsAdaptive(Strategy);
        return new BrowserAgentCaptureOptions
        {
            StartDelayMs = adaptive ? tuning.StartDelayMs : source.StartDelayMs,
            StableWindowMs = adaptive ? tuning.StableWindowMs : source.StableWindowMs,
            OverlapRatio = adaptive ? tuning.OverlapRatio : source.OverlapRatio,
            PreloadDynamicContent = source.PreloadDynamicContent,
            PreloadMaxSeconds = source.PreloadMaxSeconds,
            RequireRegionSelection = source.RequireRegionSelection,
            SpeedStrategy = Strategy,
            RepairPrecision = Precision
        }.Normalize();
    }

    private void ApplyPreset(
        NumericUpDown startDelay,
        NumericUpDown pageSettle,
        NumericUpDown overlap,
        CheckBox preScan)
    {
        if (applyingPreset) return;
        applyingPreset = true;
        try
        {
            BrowserAgentFrameTuning tuning = BrowserAgentAdaptiveProfiles.BaseTuning(Strategy);
            startDelay.Value = Clamp(tuning.StartDelayMs, startDelay.Minimum, startDelay.Maximum);
            pageSettle.Value = Clamp(tuning.StableWindowMs, pageSettle.Minimum, pageSettle.Maximum);
            overlap.Value = Clamp((decimal)(tuning.OverlapRatio * 100.0), overlap.Minimum, overlap.Maximum);
            preScan.Checked = false;
            precisionSelector.Enabled = BrowserAgentAdaptiveProfiles.IsAdaptive(Strategy);
            speedHint.Text = BrowserAgentAdaptiveProfiles.IsAdaptive(Strategy)
                ? $"Target {tuning.Name}: adaptive controller can slow down on risk and recover speed after clean frames."
                : $"Fixed {tuning.Name}: {tuning.StableWindowMs} ms settle, {tuning.OverlapRatio:P0} overlap.";
        }
        finally
        {
            applyingPreset = false;
        }
    }

    private void InitializeRows(TableLayoutPanel body)
    {
        if (initialized) return;
        normalRow3Control = body.GetControlFromPosition(1, 3);
        normalRow4Control = body.GetControlFromPosition(1, 4);
        normalRow3Label = (body.GetControlFromPosition(0, 3) as Label)?.Text ?? normalRow3Label;
        normalRow4Label = (body.GetControlFromPosition(0, 4) as Label)?.Text ?? normalRow4Label;
        initialized = true;
    }

    private static void BuildPanel(TableLayoutPanel panel, Control selector, Control hint)
    {
        panel.Dock = DockStyle.Fill;
        panel.ColumnCount = 2;
        panel.RowCount = 1;
        panel.Margin = new Padding(0);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(selector, 0, 0);
        panel.Controls.Add(hint, 1, 0);
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

    private static decimal Clamp(decimal value, decimal min, decimal max) => Math.Min(max, Math.Max(min, value));
    private static decimal Clamp(int value, decimal min, decimal max) => Clamp((decimal)value, min, max);
}
