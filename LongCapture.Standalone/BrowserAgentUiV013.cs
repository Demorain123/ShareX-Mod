namespace LongCapture.Standalone;

internal sealed class BrowserAgentUiModeAdapterV013
{
    private bool browserModeActive;
    private decimal normalStartDelay;
    private decimal normalSettle;
    private decimal normalScrollAmount;
    private decimal normalScrollAmountMinimum;
    private decimal normalScrollAmountMaximum;
    private decimal normalScrollAmountIncrement;
    private readonly List<object> normalScrollMethods = new();
    private int normalScrollMethodIndex;
    private bool normalAutoScrollTop;
    private bool normalWholeWindow;

    private decimal browserStartDelay = 450;
    private decimal browserSettle = 1100;
    private decimal browserOverlapPercent = 32;
    private bool browserPreload = false;
    private bool browserFullViewport;

    public void SetMode(
        bool browserMode,
        TableLayoutPanel body,
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        ComboBox scrollMethod,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture,
        CheckBox debugCaptureUi,
        CheckBox includeInternalDebugWindows)
    {
        if (browserMode == browserModeActive)
        {
            ApplyEnabledState(browserMode, startDelay, scrollDelay, scrollAmount, scrollMethod, autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
            return;
        }

        if (browserMode)
        {
            SaveNormal(startDelay, scrollDelay, scrollAmount, scrollMethod, autoScrollTop, wholeWindowCapture);
            ConfigureBrowser(body, startDelay, scrollDelay, scrollAmount, scrollMethod, autoScrollTop, wholeWindowCapture);
        }
        else
        {
            SaveBrowser(startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture);
            RestoreNormal(body, startDelay, scrollDelay, scrollAmount, scrollMethod, autoScrollTop, wholeWindowCapture);
        }

        browserModeActive = browserMode;
        ApplyEnabledState(browserMode, startDelay, scrollDelay, scrollAmount, scrollMethod, autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
    }

    public BrowserAgentCaptureOptions BuildOptions(
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture)
    {
        SaveBrowser(startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture);
        return new BrowserAgentCaptureOptions
        {
            StartDelayMs = (int)browserStartDelay,
            StableWindowMs = (int)browserSettle,
            OverlapRatio = (double)browserOverlapPercent / 100.0,
            PreloadDynamicContent = browserPreload,
            PreloadMaxSeconds = browserPreload ? 90 : 10,
            RequireRegionSelection = !browserFullViewport
        }.Normalize();
    }

    public static void ReflowTargetStrip(
        TableLayoutPanel targetStrip,
        Label targetLabel,
        ComboBox targetSelector,
        Button foregroundTargetButton,
        Button refreshTargetsButton,
        Button openLogsButton)
    {
        targetStrip.SuspendLayout();
        try
        {
            targetStrip.Controls.Clear();
            targetStrip.ColumnStyles.Clear();
            targetStrip.RowStyles.Clear();
            targetStrip.ColumnCount = 2;
            targetStrip.RowCount = 2;
            targetStrip.Height = 102;
            targetStrip.Padding = new Padding(20, 8, 20, 8);
            targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            targetStrip.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            targetStrip.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            targetLabel.AutoSize = true;
            targetLabel.MinimumSize = new Size(120, 32);
            targetSelector.MinimumSize = new Size(280, 32);
            targetSelector.Margin = new Padding(8, 2, 0, 2);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(128, 2, 0, 0),
                Padding = new Padding(0)
            };
            foreach (Button button in new[] { foregroundTargetButton, refreshTargetsButton, openLogsButton })
            {
                button.AutoSize = true;
                button.Dock = DockStyle.None;
                button.MinimumSize = new Size(0, 32);
                button.Margin = new Padding(0, 0, 8, 4);
                actions.Controls.Add(button);
            }

            targetStrip.Controls.Add(targetLabel, 0, 0);
            targetStrip.Controls.Add(targetSelector, 1, 0);
            targetStrip.Controls.Add(actions, 0, 1);
            targetStrip.SetColumnSpan(actions, 2);
        }
        finally
        {
            targetStrip.ResumeLayout(true);
        }
    }

    public static void Polish(Form form, TableLayoutPanel body)
    {
        form.MinimumSize = new Size(860, 720);
        form.Size = new Size(Math.Max(form.Width, 980), Math.Max(form.Height, 820));
        form.AutoScaleMode = AutoScaleMode.Dpi;
        body.AutoScroll = true;
        body.Padding = new Padding(24, 18, 24, 20);
        if (body.ColumnStyles.Count >= 2)
        {
            body.ColumnStyles[0].SizeType = SizeType.Absolute;
            body.ColumnStyles[0].Width = 170;
            body.ColumnStyles[1].SizeType = SizeType.Percent;
            body.ColumnStyles[1].Width = 100;
        }

        ApplyButtonStyle(form);
    }

    private static void ApplyButtonStyle(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = Color.FromArgb(205, 210, 216);
                button.BackColor = Color.FromArgb(248, 249, 251);
                button.Padding = new Padding(8, 2, 8, 2);
            }
            ApplyButtonStyle(control);
        }
    }

    private void SaveNormal(
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        ComboBox scrollMethod,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture)
    {
        normalStartDelay = startDelay.Value;
        normalSettle = scrollDelay.Value;
        normalScrollAmount = scrollAmount.Value;
        normalScrollAmountMinimum = scrollAmount.Minimum;
        normalScrollAmountMaximum = scrollAmount.Maximum;
        normalScrollAmountIncrement = scrollAmount.Increment;
        normalScrollMethods.Clear();
        foreach (object item in scrollMethod.Items) normalScrollMethods.Add(item);
        normalScrollMethodIndex = scrollMethod.SelectedIndex;
        normalAutoScrollTop = autoScrollTop.Checked;
        normalWholeWindow = wholeWindowCapture.Checked;
    }

    private void SaveBrowser(
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture)
    {
        browserStartDelay = startDelay.Value;
        browserSettle = scrollDelay.Value;
        browserOverlapPercent = scrollAmount.Value;
        browserPreload = autoScrollTop.Checked;
        browserFullViewport = wholeWindowCapture.Checked;
    }

    private void ConfigureBrowser(
        TableLayoutPanel body,
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        ComboBox scrollMethod,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture)
    {
        SetRowLabel(body, 5, "Start delay (ms)");
        SetRowLabel(body, 6, "Page settle (ms)");
        SetRowLabel(body, 7, "Overlap (%)");
        SetRowLabel(body, 8, "Scroll engine");
        SetRowLabel(body, 9, "Dynamic content");

        startDelay.Value = Clamp(browserStartDelay, startDelay.Minimum, startDelay.Maximum);

        scrollDelay.Minimum = 450;
        scrollDelay.Maximum = 3000;
        scrollDelay.Increment = 100;
        scrollDelay.Value = Clamp(browserSettle, scrollDelay.Minimum, scrollDelay.Maximum);

        scrollAmount.Minimum = 20;
        scrollAmount.Maximum = 50;
        scrollAmount.Increment = 1;
        scrollAmount.Value = Clamp(browserOverlapPercent, scrollAmount.Minimum, scrollAmount.Maximum);

        scrollMethod.BeginUpdate();
        try
        {
            scrollMethod.Items.Clear();
            scrollMethod.Items.Add("DOM + visual verification (required)");
            scrollMethod.SelectedIndex = 0;
        }
        finally
        {
            scrollMethod.EndUpdate();
        }

        autoScrollTop.Text = "Optional gentle lazy-content pre-scan (visible, slower)";
        autoScrollTop.Checked = browserPreload;
        wholeWindowCapture.Text = "Full browser viewport (skip F8 region selection)";
        wholeWindowCapture.Checked = browserFullViewport;
    }

    private void RestoreNormal(
        TableLayoutPanel body,
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        ComboBox scrollMethod,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture)
    {
        SetRowLabel(body, 5, "Start delay (ms)");
        SetRowLabel(body, 6, "Scroll settle (ms)");
        SetRowLabel(body, 7, "Scroll amount");
        SetRowLabel(body, 8, "Scroll method");
        SetRowLabel(body, 9, "Capture behavior");

        startDelay.Value = Clamp(normalStartDelay, startDelay.Minimum, startDelay.Maximum);
        scrollDelay.Minimum = 50;
        scrollDelay.Maximum = 10000;
        scrollDelay.Increment = 50;
        scrollDelay.Value = Clamp(normalSettle, scrollDelay.Minimum, scrollDelay.Maximum);

        scrollAmount.Minimum = normalScrollAmountMinimum;
        scrollAmount.Maximum = normalScrollAmountMaximum;
        scrollAmount.Increment = normalScrollAmountIncrement;
        scrollAmount.Value = Clamp(normalScrollAmount, scrollAmount.Minimum, scrollAmount.Maximum);

        scrollMethod.BeginUpdate();
        try
        {
            scrollMethod.Items.Clear();
            foreach (object item in normalScrollMethods) scrollMethod.Items.Add(item);
            if (scrollMethod.Items.Count > 0)
            {
                scrollMethod.SelectedIndex = Math.Clamp(normalScrollMethodIndex, 0, scrollMethod.Items.Count - 1);
            }
        }
        finally
        {
            scrollMethod.EndUpdate();
        }

        autoScrollTop.Text = "Scroll selected target to the top before capture";
        autoScrollTop.Checked = normalAutoScrollTop;
        wholeWindowCapture.Text = "Whole window (skip region selection)";
        wholeWindowCapture.Checked = normalWholeWindow;
    }

    private static void ApplyEnabledState(
        bool browserMode,
        NumericUpDown startDelay,
        NumericUpDown scrollDelay,
        NumericUpDown scrollAmount,
        ComboBox scrollMethod,
        CheckBox autoScrollTop,
        CheckBox wholeWindowCapture,
        CheckBox debugCaptureUi,
        CheckBox includeInternalDebugWindows)
    {
        if (!browserMode) return;
        startDelay.Enabled = true;
        scrollDelay.Enabled = true;
        scrollAmount.Enabled = true;
        scrollMethod.Enabled = false;
        autoScrollTop.Enabled = true;
        wholeWindowCapture.Enabled = true;
        debugCaptureUi.Enabled = true;
        includeInternalDebugWindows.Enabled = debugCaptureUi.Checked;
    }

    private static void SetRowLabel(TableLayoutPanel body, int row, string text)
    {
        if (body.GetControlFromPosition(0, row) is Label label)
        {
            label.Text = text;
        }
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) => Math.Min(max, Math.Max(min, value));
}
