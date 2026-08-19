namespace LongCapture.Standalone;

internal sealed class BrowserAgentAdaptiveUiV015
{
    private readonly ComboBox speedSelector = new();
    private readonly ComboBox precisionSelector = new();
    private readonly Label speedHint = new();
    private readonly Label precisionHint = new();
    private readonly TableLayoutPanel precisionPanel = new();

    private Control? normalRow4Control;
    private string normalRow4Label = "Recipe actions";
    private bool initialized;
    private bool browserMode;
    private bool applyingPreset;
    private bool globalSpeedMounted;
    private BrowserAgentSpeedStrategy browserStrategy = BrowserAgentSpeedStrategy.AdaptiveBalanced;
    private BrowserAgentSpeedStrategy nativeStrategy = BrowserAgentSpeedStrategy.FixedMedium;

    public BrowserAgentAdaptiveUiV015()
    {
        speedSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        speedSelector.MinimumSize = new Size(205, 30);
        speedSelector.Width = 225;
        speedSelector.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        RebuildSpeedItems(browser: false);

        precisionSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        precisionSelector.Items.AddRange(BrowserAgentAdaptiveProfiles.PrecisionDisplayNames.Cast<object>().ToArray());
        precisionSelector.SelectedIndex = (int)BrowserAgentRepairPrecision.Medium;
        precisionSelector.Dock = DockStyle.Left;
        precisionSelector.Width = 145;

        speedHint.Text = "Fixed Medium";
        speedHint.AutoSize = true;
        speedHint.ForeColor = Color.DimGray;
        speedHint.Padding = new Padding(8, 5, 0, 0);

        precisionHint.Text = "Controls how often suspicious sections are reviewed/re-captured.";
        precisionHint.AutoSize = true;
        precisionHint.ForeColor = Color.DimGray;
        precisionHint.Padding = new Padding(8, 6, 0, 0);

        BuildPanel(precisionPanel, precisionSelector, precisionHint);
    }

    public BrowserAgentSpeedStrategy Strategy => browserMode ? browserStrategy : nativeStrategy;
    public BrowserAgentRepairPrecision Precision => (BrowserAgentRepairPrecision)Math.Clamp(precisionSelector.SelectedIndex, 0, 2);

    public void MountGlobalSpeedStrip(TableLayoutPanel targetStrip)
    {
        if (globalSpeedMounted) return;
        globalSpeedMounted = true;

        targetStrip.SuspendLayout();
        try
        {
            targetStrip.RowCount = Math.Max(3, targetStrip.RowCount);
            while (targetStrip.RowStyles.Count < 3)
            {
                targetStrip.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            }
            targetStrip.RowStyles[2].SizeType = SizeType.Absolute;
            targetStrip.RowStyles[2].Height = 38;
            targetStrip.Height = Math.Max(targetStrip.Height, 140);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 2, 0, 0),
                Padding = new Padding(0)
            };
            var label = new Label
            {
                Text = "Capture speed",
                AutoSize = true,
                MinimumSize = new Size(120, 30),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 8, 0)
            };
            speedSelector.Margin = new Padding(0, 2, 6, 0);
            bar.Controls.Add(label);
            bar.Controls.Add(speedSelector);
            bar.Controls.Add(speedHint);
            targetStrip.Controls.Add(bar, 0, 2);
            targetStrip.SetColumnSpan(bar, 2);
        }
        finally
        {
            targetStrip.ResumeLayout(true);
        }
    }

    public void AttachHandlers(
        NumericUpDown startDelay,
        NumericUpDown pageSettle,
        NumericUpDown scrollAmountOrOverlap,
        CheckBox browserPreScanOrNativeScrollTop)
    {
        speedSelector.SelectionChangeCommitted += (_, _) =>
        {
            if (browserMode)
            {
                browserStrategy = (BrowserAgentSpeedStrategy)Math.Clamp(speedSelector.SelectedIndex, 0, BrowserAgentAdaptiveProfiles.StrategyDisplayNames.Length - 1);
            }
            else
            {
                nativeStrategy = (BrowserAgentSpeedStrategy)Math.Clamp(speedSelector.SelectedIndex, 0, 4);
            }
            ApplyPreset(startDelay, pageSettle, scrollAmountOrOverlap, browserPreScanOrNativeScrollTop);
            LongCaptureLog.Info($"[USER_ACTION] capture-setting speedStrategy={Strategy} browserMode={browserMode}");
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
        NumericUpDown scrollAmountOrOverlap,
        CheckBox browserPreScanOrNativeScrollTop)
    {
        InitializeRows(body);
        bool changed = browserMode != this.browserMode;
        this.browserMode = browserMode;

        if (changed)
        {
            RebuildSpeedItems(browserMode);
        }

        body.SuspendLayout();
        try
        {
            if (browserMode)
            {
                SetRowLabel(body, 4, "Repair precision");
                ReplaceRowControl(body, 4, precisionPanel);
                precisionSelector.Enabled = BrowserAgentAdaptiveProfiles.IsAdaptive(Strategy);
            }
            else
            {
                SetRowLabel(body, 4, normalRow4Label);
                if (normalRow4Control is not null) ReplaceRowControl(body, 4, normalRow4Control);
            }
        }
        finally
        {
            body.ResumeLayout(true);
        }

        if (changed)
        {
            ApplyPreset(startDelay, pageSettle, scrollAmountOrOverlap, browserPreScanOrNativeScrollTop);
        }
    }

    public BrowserAgentCaptureOptions EnrichOptions(BrowserAgentCaptureOptions source)
    {
        BrowserAgentFrameTuning tuning = BrowserAgentAdaptiveProfiles.BaseTuning(browserStrategy);
        bool adaptive = BrowserAgentAdaptiveProfiles.IsAdaptive(browserStrategy);
        return new BrowserAgentCaptureOptions
        {
            StartDelayMs = adaptive ? tuning.StartDelayMs : source.StartDelayMs,
            StableWindowMs = adaptive ? tuning.StableWindowMs : source.StableWindowMs,
            OverlapRatio = adaptive ? tuning.OverlapRatio : source.OverlapRatio,
            PreloadDynamicContent = source.PreloadDynamicContent,
            PreloadMaxSeconds = source.PreloadMaxSeconds,
            RequireRegionSelection = source.RequireRegionSelection,
            SpeedStrategy = browserStrategy,
            RepairPrecision = Precision
        }.Normalize();
    }

    private void ApplyPreset(
        NumericUpDown startDelay,
        NumericUpDown pageSettle,
        NumericUpDown scrollAmountOrOverlap,
        CheckBox browserPreScanOrNativeScrollTop)
    {
        if (applyingPreset) return;
        applyingPreset = true;
        try
        {
            if (browserMode)
            {
                BrowserAgentFrameTuning tuning = BrowserAgentAdaptiveProfiles.BaseTuning(browserStrategy);
                startDelay.Value = Clamp(tuning.StartDelayMs, startDelay.Minimum, startDelay.Maximum);
                pageSettle.Value = Clamp(tuning.StableWindowMs, pageSettle.Minimum, pageSettle.Maximum);
                scrollAmountOrOverlap.Value = Clamp((decimal)(tuning.OverlapRatio * 100.0), scrollAmountOrOverlap.Minimum, scrollAmountOrOverlap.Maximum);
                browserPreScanOrNativeScrollTop.Checked = false;
                precisionSelector.Enabled = BrowserAgentAdaptiveProfiles.IsAdaptive(browserStrategy);
                speedHint.Text = BrowserAgentAdaptiveProfiles.IsAdaptive(browserStrategy)
                    ? $"Target {tuning.Name}; slows on loading/layout risk, then recovers after clean frames."
                    : $"Fixed {tuning.Name}: {tuning.StableWindowMs} ms settle, {tuning.OverlapRatio:P0} overlap.";
            }
            else
            {
                int gear = Math.Clamp((int)nativeStrategy, 0, 4);
                (int delay, int settle, int scroll) = gear switch
                {
                    0 => (500, 1200, 1),
                    1 => (400, 850, 2),
                    2 => (300, 550, 3),
                    3 => (150, 350, 4),
                    _ => (0, 220, 5)
                };
                startDelay.Value = Clamp(delay, startDelay.Minimum, startDelay.Maximum);
                pageSettle.Value = Clamp(settle, pageSettle.Minimum, pageSettle.Maximum);
                scrollAmountOrOverlap.Value = Clamp(scroll, scrollAmountOrOverlap.Minimum, scrollAmountOrOverlap.Maximum);
                string name = BrowserAgentAdaptiveProfiles.ForGear(gear).Name;
                speedHint.Text = $"Fixed {name}: {settle} ms settle, scroll amount {scroll}.";
            }
        }
        finally
        {
            applyingPreset = false;
        }
    }

    private void RebuildSpeedItems(bool browser)
    {
        applyingPreset = true;
        try
        {
            speedSelector.BeginUpdate();
            speedSelector.Items.Clear();
            if (browser)
            {
                speedSelector.Items.AddRange(BrowserAgentAdaptiveProfiles.StrategyDisplayNames.Cast<object>().ToArray());
                speedSelector.SelectedIndex = Math.Clamp((int)browserStrategy, 0, BrowserAgentAdaptiveProfiles.StrategyDisplayNames.Length - 1);
            }
            else
            {
                speedSelector.Items.AddRange(BrowserAgentAdaptiveProfiles.StrategyDisplayNames.Take(5).Cast<object>().ToArray());
                speedSelector.SelectedIndex = Math.Clamp((int)nativeStrategy, 0, 4);
            }
            speedSelector.EndUpdate();
        }
        finally
        {
            applyingPreset = false;
        }
    }

    private void InitializeRows(TableLayoutPanel body)
    {
        if (initialized) return;
        normalRow4Control = body.GetControlFromPosition(1, 4);
        normalRow4Label = (body.GetControlFromPosition(0, 4) as Label)?.Text ?? normalRow4Label;
        initialized = true;
    }

    private static void BuildPanel(TableLayoutPanel panel, Control selector, Control hint)
    {
        panel.Dock = DockStyle.Fill;
        panel.ColumnCount = 2;
        panel.RowCount = 1;
        panel.Margin = new Padding(0);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
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
