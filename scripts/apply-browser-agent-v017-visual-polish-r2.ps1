[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$adaptive = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentAdaptiveUiV015.cs"

function Patch-Literal {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.1.7-polish-r2] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.7 polish-r2 anchor missing: $Marker" }
    Write-Host "[v0.1.7-polish-r2] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[v0.1.7-polish-r2] applied: $Marker" -ForegroundColor Cyan
    }
}

Patch-Literal -Path $ui `
    -Old @'
            ConfigureReadiness(readinessPanel);
            ConfigureRecipeActions(recipePanel);
            ConfigureSimpleRows(body);
            if (actionPanel is not null) HardenActionPanel(actionPanel, highContrast);

            ComboBox? modeSelector = body.GetControlFromPosition(1, 0) as ComboBox;
'@ `
    -New @'
            ConfigureReadiness(readinessPanel);
            ConfigureRecipeActions(recipePanel);
            ConfigureSimpleRows(body);
            if (actionPanel is not null) HardenActionPanel(actionPanel, highContrast);
            RebuildModernTargetSurface(targetStrip, actionPanel, highContrast);
            ConfigureCompactSettings(body);

            ComboBox? modeSelector = body.GetControlFromPosition(1, 0) as ComboBox;
'@ `
    -Marker 'RebuildModernTargetSurface(targetStrip, actionPanel, highContrast);'

Patch-Literal -Path $ui `
    -Old '                    combo.FlatStyle = FlatStyle.Flat;' `
    -New '                    combo.FlatStyle = FlatStyle.Standard;' `
    -Marker 'combo.FlatStyle = FlatStyle.Standard;'

Patch-Literal -Path $ui `
    -Old @'
        if (subtitle is not null)
        {
            subtitle.Text = $"Reliable long screenshots · Browser Assisted adaptive quality · Recipes · UI v{ExperienceVersion}";
'@ `
    -New @'
        if (subtitle is not null)
        {
            subtitle.Text = "Reliable long screenshots · Browser-assisted capture · Quality guard · Recipes";
'@ `
    -Marker 'Browser-assisted capture · Quality guard · Recipes'

Patch-Literal -Path $ui `
    -Old @'
    private static void ApplyModeVisibility(TableLayoutPanel body, ComboBox modeSelector)
'@ `
    -New @'
    private static void RebuildModernTargetSurface(TableLayoutPanel? targetStrip, Panel? actionPanel, bool highContrast)
    {
        if (targetStrip is null) return;

        Label? targetLabel = targetStrip.Controls.OfType<Label>()
            .FirstOrDefault(x => x.Text.StartsWith("Capture target", StringComparison.OrdinalIgnoreCase));
        ComboBox? targetSelector = targetStrip.Controls.OfType<ComboBox>().FirstOrDefault();
        FlowLayoutPanel? speedBar = FindSpeedBar(targetStrip);
        FlowLayoutPanel? actions = targetStrip.Controls.OfType<FlowLayoutPanel>()
            .FirstOrDefault(x => !ReferenceEquals(x, speedBar) && x.Controls.OfType<Button>().Any());
        Label? speedLabel = speedBar?.Controls.OfType<Label>()
            .FirstOrDefault(x => string.Equals(x.Text, "Capture speed", StringComparison.OrdinalIgnoreCase));

        if (targetLabel is null || targetSelector is null || speedBar is null || actions is null || speedLabel is null) return;

        Button? capture = actionPanel is null ? null : Descendants(actionPanel).OfType<Button>().FirstOrDefault(IsPrimaryCaptureButton);
        if (capture is not null)
        {
            capture.Parent?.Controls.Remove(capture);
            capture.Dock = DockStyle.None;
            capture.AutoSize = true;
            capture.MinimumSize = new Size(156, 40);
            capture.Margin = new Padding(0, 0, 8, 6);
            StyleButton(capture, highContrast, primary: true);
            actions.Controls.Add(capture);
            actions.Controls.SetChildIndex(capture, 0);
        }

        speedBar.Controls.Remove(speedLabel);
        targetStrip.SuspendLayout();
        try
        {
            targetStrip.Controls.Clear();
            targetStrip.ColumnStyles.Clear();
            targetStrip.RowStyles.Clear();
            targetStrip.ColumnCount = 2;
            targetStrip.RowCount = 3;
            targetStrip.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
            targetStrip.AutoSize = true;
            targetStrip.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            targetStrip.MinimumSize = Size.Empty;
            targetStrip.Padding = new Padding(24, 10, 24, 10);
            targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            targetStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            targetStrip.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            targetStrip.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            targetStrip.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            targetLabel.AutoSize = true;
            targetLabel.MinimumSize = new Size(0, 36);
            targetLabel.Margin = new Padding(0, 2, 12, 6);
            targetLabel.TextAlign = ContentAlignment.MiddleLeft;

            targetSelector.Dock = DockStyle.Fill;
            targetSelector.MinimumSize = new Size(280, 36);
            targetSelector.Margin = new Padding(0, 0, 0, 8);

            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.WrapContents = true;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.MinimumSize = Size.Empty;
            actions.Margin = new Padding(0, 0, 0, 8);
            actions.Padding = Padding.Empty;
            foreach (Button button in actions.Controls.OfType<Button>())
            {
                button.Dock = DockStyle.None;
                button.AutoSize = true;
                button.MinimumSize = new Size(button.MinimumSize.Width, 38);
                button.Margin = new Padding(0, 0, 8, 6);
            }

            speedLabel.AutoSize = true;
            speedLabel.MinimumSize = new Size(0, 36);
            speedLabel.Margin = new Padding(0, 2, 12, 0);
            speedLabel.TextAlign = ContentAlignment.MiddleLeft;

            speedBar.Dock = DockStyle.Fill;
            speedBar.AutoSize = true;
            speedBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            speedBar.WrapContents = true;
            speedBar.FlowDirection = FlowDirection.LeftToRight;
            speedBar.MinimumSize = new Size(0, 38);
            speedBar.MaximumSize = Size.Empty;
            speedBar.Margin = Padding.Empty;
            speedBar.Padding = Padding.Empty;
            foreach (Control child in speedBar.Controls)
            {
                child.Margin = child is Label ? new Padding(10, 7, 0, 4) : new Padding(0, 0, 8, 4);
            }

            targetStrip.Controls.Add(targetLabel, 0, 0);
            targetStrip.Controls.Add(targetSelector, 1, 0);
            targetStrip.Controls.Add(actions, 1, 1);
            targetStrip.Controls.Add(speedLabel, 0, 2);
            targetStrip.Controls.Add(speedBar, 1, 2);
        }
        finally
        {
            targetStrip.ResumeLayout(true);
        }
    }

    private static void ConfigureCompactSettings(TableLayoutPanel body)
    {
        if (body.GetControlFromPosition(1, 0) is ComboBox mode)
        {
            mode.Dock = DockStyle.Left;
            mode.Width = 320;
        }

        foreach (int row in new[] { 5, 6, 7 })
        {
            if (body.GetControlFromPosition(1, row) is NumericUpDown numeric)
            {
                numeric.Dock = DockStyle.Left;
                numeric.AutoSize = false;
                numeric.Width = 220;
                numeric.Height = 34;
            }
        }

        if (body.GetControlFromPosition(1, 8) is ComboBox method)
        {
            method.Dock = DockStyle.Left;
            method.Width = 300;
        }
    }

    private static void ApplyModeVisibility(TableLayoutPanel body, ComboBox modeSelector)
'@ `
    -Marker 'private static void RebuildModernTargetSurface'

Patch-Literal -Path $ui `
    -Old '                if (!ValidatePrimaryActions(body, out detail)) return false;' `
    -New '                if (!ValidatePrimaryActions(form, body, out detail)) return false;' `
    -Marker 'ValidatePrimaryActions(form, body, out detail)'

Patch-Literal -Path $ui `
    -Old @'
    private static bool ValidatePrimaryActions(TableLayoutPanel body, out string detail)
    {
        if (body.GetControlFromPosition(0, 11) is not Panel panel)
'@ `
    -New @'
    private static bool ValidatePrimaryActions(Form form, TableLayoutPanel body, out string detail)
    {
        if (body.GetControlFromPosition(0, 11) is not Panel panel)
'@ `
    -Marker 'ValidatePrimaryActions(Form form, TableLayoutPanel body'

Patch-Literal -Path $ui `
    -Old '        Button? capture = Descendants(panel).OfType<Button>().FirstOrDefault(IsPrimaryCaptureButton);' `
    -New '        Button? capture = Descendants(form).OfType<Button>().FirstOrDefault(IsPrimaryCaptureButton);' `
    -Marker 'Button? capture = Descendants(form).OfType<Button>()'

Patch-Literal -Path $adaptive `
    -Old '            speedHint.Text = $"Fixed {name} · app-specific runtime learning is not applied to native modes yet.";' `
    -New '            speedHint.Text = $"{name} · fixed timing for native capture";' `
    -Marker 'fixed timing for native capture'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 visual polish r2 compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.7 visual polish r2 applied: compact command surface, persistent primary CTA, calmer settings widths and user-facing copy." -ForegroundColor Green
}
