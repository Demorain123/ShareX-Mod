namespace LongCapture.Standalone;

internal sealed class BrowserAgentAdaptiveUiV015
{
    private readonly ComboBox speedSelector = new();
    private readonly ComboBox precisionSelector = new();
    private readonly Label speedHint = new();
    private readonly Label precisionHint = new();
    private readonly TableLayoutPanel precisionPanel = new();
    private readonly Button benchmarkButton = new();
    private readonly CheckBox calibrationToggle = new();
    private readonly CheckBox liveMonitorToggle = new();

    private Control? normalRow4Control;
    private string normalRow4Label = "Recipe actions";
    private bool initialized;
    private bool browserMode;
    private bool applyingPreset;
    private bool globalSpeedMounted;
    private BrowserAgentSpeedStrategy browserStrategy = BrowserAgentSpeedStrategy.AdaptiveBalanced;
    private BrowserAgentSpeedStrategy nativeStrategy = BrowserAgentSpeedStrategy.FixedMedium;
    private Func<Task<BrowserAgentCalibrationResult>>? calibrationRunner;
    private BrowserAgentAdaptiveMonitorV016? liveMonitor;

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

        benchmarkButton.Text = "Browser benchmark";
        benchmarkButton.AutoSize = true;
        benchmarkButton.Height = 30;
        benchmarkButton.Enabled = false;

        calibrationToggle.Text = "Use local calibration";
        calibrationToggle.AutoSize = true;
        calibrationToggle.Checked = true;
        calibrationToggle.Enabled = false;
        calibrationToggle.Padding = new Padding(4, 5, 0, 0);

        liveMonitorToggle.Text = "Live params";
        liveMonitorToggle.AutoSize = true;
        liveMonitorToggle.Checked = true;
        liveMonitorToggle.Enabled = false;
        liveMonitorToggle.Padding = new Padding(4, 5, 0, 0);

        benchmarkButton.Click += async (_, _) => await RunCalibrationAsync();
        calibrationToggle.CheckedChanged += (_, _) =>
        {
            if (browserMode && !applyingPreset)
            {
                LongCaptureLog.Info($"[USER_ACTION] browser-setting localCalibration={calibrationToggle.Checked}");
            }
        };

        BuildPanel(precisionPanel, precisionSelector, precisionHint);
    }

    public BrowserAgentSpeedStrategy Strategy => browserMode ? browserStrategy : nativeStrategy;
    public BrowserAgentRepairPrecision Precision => (BrowserAgentRepairPrecision)Math.Clamp(precisionSelector.SelectedIndex, 0, 2);
    public bool UseLocalCalibration => browserMode && calibrationToggle.Checked;
    public bool ShowLiveMonitor => browserMode && liveMonitorToggle.Checked;

    public void MountGlobalSpeedStrip(TableLayoutPanel targetStrip)
    {
        if (globalSpeedMounted) return;
        globalSpeedMounted = true;

        targetStrip.SuspendLayout();
        try
        {
            targetStrip.RowCount = Math.Max(3, targetStrip.RowCount);
            while (targetStrip.RowStyles.Count < 3) targetStrip.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
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
            benchmarkButton.Margin = new Padding(6, 2, 4, 0);
            calibrationToggle.Margin = new Padding(4, 2, 2, 0);
            liveMonitorToggle.Margin = new Padding(2, 2, 4, 0);
            bar.Controls.Add(label);
            bar.Controls.Add(speedSelector);
            bar.Controls.Add(benchmarkButton);
            bar.Controls.Add(calibrationToggle);
            bar.Controls.Add(liveMonitorToggle);
            bar.Controls.Add(speedHint);
            targetStrip.Controls.Add(bar, 0, 2);
            targetStrip.SetColumnSpan(bar, 4);
        }
        finally
        {
            targetStrip.ResumeLayout(true);
        }
    }

    public void AttachCalibrationHandler(Func<Task<BrowserAgentCalibrationResult>> runner)
    {
        calibrationRunner = runner;
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
        calibrationToggle.CheckedChanged += (_, _) =>
        {
            if (!browserMode || applyingPreset) return;
            ApplyPreset(startDelay, pageSettle, scrollAmountOrOverlap, browserPreScanOrNativeScrollTop);
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

        if (changed) RebuildSpeedItems(browserMode);

        benchmarkButton.Enabled = browserMode;
        calibrationToggle.Enabled = browserMode;
        liveMonitorToggle.Enabled = browserMode;

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

        if (changed) ApplyPreset(startDelay, pageSettle, scrollAmountOrOverlap, browserPreScanOrNativeScrollTop);
        else UpdateHint();
    }

    public BrowserAgentCaptureOptions EnrichOptions(BrowserAgentCaptureOptions source)
    {
        BrowserAgentFrameTuning tuning = BrowserAgentAdaptiveProfiles.BaseTuning(browserStrategy);
        if (UseLocalCalibration) tuning = BrowserAgentCalibrationStore.Current.Tune(tuning);
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
            RepairPrecision = Precision,
            UseLocalCalibration = UseLocalCalibration,
            ShowLiveAdaptiveMonitor = ShowLiveMonitor
        }.Normalize();
    }

    public void BeginCaptureMonitor()
    {
        EndCaptureMonitor();
        BrowserAgentAdaptiveTelemetryHub.Reset();
        if (!ShowLiveMonitor) return;
        liveMonitor = new BrowserAgentAdaptiveMonitorV016();
        liveMonitor.Show();
        LongCaptureLog.Info("[BA_LIVE] adaptive live monitor shown without activation");
    }

    public void EndCaptureMonitor()
    {
        BrowserAgentAdaptiveMonitorV016? monitor = liveMonitor;
        liveMonitor = null;
        if (monitor is null) return;
        try { monitor.Close(); } catch { monitor.Dispose(); }
        LongCaptureLog.Info("[BA_LIVE] adaptive live monitor closed");
    }

    private async Task RunCalibrationAsync()
    {
        if (!browserMode || calibrationRunner is null) return;
        benchmarkButton.Enabled = false;
        string old = benchmarkButton.Text;
        benchmarkButton.Text = "Benchmarking...";
        try
        {
            BrowserAgentCalibrationResult result = await calibrationRunner();
            UpdateHint();
            MessageBox.Show(
                result.Summary,
                "Browser calibration complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"[BA_CALIB] benchmark failed: {LongCaptureLog.OneLine(ex.Message)}");
            MessageBox.Show(ex.Message, "Browser calibration failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            benchmarkButton.Text = old;
            benchmarkButton.Enabled = browserMode;
        }
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
                if (UseLocalCalibration) tuning = BrowserAgentCalibrationStore.Current.Tune(tuning);
                startDelay.Value = Clamp(tuning.StartDelayMs, startDelay.Minimum, startDelay.Maximum);
                pageSettle.Value = Clamp(tuning.StableWindowMs, pageSettle.Minimum, pageSettle.Maximum);
                scrollAmountOrOverlap.Value = Clamp((decimal)(tuning.OverlapRatio * 100.0), scrollAmountOrOverlap.Minimum, scrollAmountOrOverlap.Maximum);
                browserPreScanOrNativeScrollTop.Checked = false;
                precisionSelector.Enabled = BrowserAgentAdaptiveProfiles.IsAdaptive(browserStrategy);
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
            }
            UpdateHint();
        }
        finally
        {
            applyingPreset = false;
        }
    }

    private void UpdateHint()
    {
        if (browserMode)
        {
            BrowserAgentFrameTuning tuning = BrowserAgentAdaptiveProfiles.BaseTuning(browserStrategy);
            BrowserAgentCalibrationProfile profile = BrowserAgentCalibrationStore.Current;
            if (UseLocalCalibration) tuning = profile.Tune(tuning);
            string learned = UseLocalCalibration
                ? $" · local confidence {profile.Confidence:P0} ({profile.BenchmarkSamples + profile.FrameSamples} samples)"
                : " · local calibration off";
            speedHint.Text = BrowserAgentAdaptiveProfiles.IsAdaptive(browserStrategy)
                ? $"Target {tuning.Name}: {tuning.StableWindowMs}ms / {tuning.OverlapRatio:P0}{learned}"
                : $"Fixed {tuning.Name}: {tuning.StartDelayMs}ms / {tuning.StableWindowMs}ms / {tuning.OverlapRatio:P0}{learned}";
        }
        else
        {
            int gear = Math.Clamp((int)nativeStrategy, 0, 4);
            string name = BrowserAgentAdaptiveProfiles.ForGear(gear).Name;
            speedHint.Text = $"Fixed {name} · app-specific runtime learning is not applied to native modes yet.";
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
