[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )
    if (-not (Test-Path -LiteralPath $Path)) { throw "RC hardening target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[LongCapture-v0.1.3-rc] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "LongCapture v0.1.3 RC compatibility check failed: '$Marker' anchor not found in $Path"
    }
    Write-Host "[LongCapture-v0.1.3-rc] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[LongCapture-v0.1.3-rc] applied: $Marker" -ForegroundColor Cyan
    }
}

$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$program = Join-Path $repoRoot "LongCapture.Standalone\Program.cs"
$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$settle = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModAdaptiveSettleV042.cs"
$isModernV017 = ([IO.File]::ReadAllText($ui)).Contains('ExperienceVersion = "0.1.7"')

# v0.1.3 added Export diagnostics after the original responsive shell. Preserve every auxiliary
# action instead of assuming there is only one non-capture button. v0.1.7 modernizes the same shell,
# so it uses equivalent hardening with its own sizing/styling anchors rather than weakening RC safety.
Replace-Literal -Path $ui `
    -Old @'
        Button? openOutput = actionPanel.Controls.OfType<Button>()
            .FirstOrDefault(x => !ReferenceEquals(x, capture));
        Label? quality = actionPanel.Controls.OfType<Label>().FirstOrDefault();
'@ `
    -New @'
        Button[] auxiliaryButtons = actionPanel.Controls.OfType<Button>()
            .Where(x => !ReferenceEquals(x, capture))
            .ToArray();
        Button? openOutput = auxiliaryButtons
            .FirstOrDefault(x => x.Text.StartsWith("Open output folder", StringComparison.OrdinalIgnoreCase))
            ?? auxiliaryButtons.FirstOrDefault();
        Label? quality = actionPanel.Controls.OfType<Label>().FirstOrDefault();
'@ `
    -Marker 'Button[] auxiliaryButtons = actionPanel.Controls.OfType<Button>()'

if ($isModernV017) {
    Replace-Literal -Path $ui `
        -Old @'
            openOutput.Dock = DockStyle.Top;
            openOutput.AutoSize = true;
            openOutput.MinimumSize = new Size(152, 36);
            openOutput.Margin = new Padding(0, 0, 14, 0);
            StyleButton(openOutput, highContrast, primary: false);

            quality.Dock = DockStyle.Fill;
'@ `
        -New @'
            foreach (Button auxiliary in auxiliaryButtons)
            {
                auxiliary.Dock = DockStyle.None;
                auxiliary.AutoSize = true;
                auxiliary.MinimumSize = new Size(152, 36);
                auxiliary.Margin = new Padding(0, 0, 10, 6);
                StyleButton(auxiliary, highContrast, primary: false);
            }

            quality.Dock = DockStyle.Fill;
'@ `
        -Marker 'foreach (Button auxiliary in auxiliaryButtons)'
} else {
    Replace-Literal -Path $ui `
        -Old @'
            openOutput.Dock = DockStyle.Top;
            openOutput.AutoSize = true;
            openOutput.MinimumSize = new Size(150, 34);
            openOutput.Margin = new Padding(0, 0, 12, 0);

            quality.Dock = DockStyle.Fill;
'@ `
        -New @'
            foreach (Button auxiliary in auxiliaryButtons)
            {
                auxiliary.Dock = DockStyle.None;
                auxiliary.AutoSize = true;
                auxiliary.MinimumSize = new Size(150, 34);
                auxiliary.Margin = new Padding(0, 0, 10, 6);
            }

            quality.Dock = DockStyle.Fill;
'@ `
        -Marker 'foreach (Button auxiliary in auxiliaryButtons)'
}

Replace-Literal -Path $ui `
    -Old @'
            layout.Controls.Add(openOutput, 0, 1);
            layout.Controls.Add(quality, 1, 1);
            actionPanel.Controls.Add(layout);
'@ `
    -New @'
            var auxiliaryPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            foreach (Button auxiliary in auxiliaryButtons)
            {
                auxiliaryPanel.Controls.Add(auxiliary);
            }

            layout.Controls.Add(auxiliaryPanel, 0, 1);
            layout.Controls.Add(quality, 1, 1);
            actionPanel.Controls.Add(layout);
'@ `
    -Marker 'var auxiliaryPanel = new FlowLayoutPanel'

if ($isModernV017) {
    Replace-Literal -Path $ui `
        -Old @'
            Button? openOutput = Descendants(actionPanel).OfType<Button>()
                .FirstOrDefault(x => !IsPrimaryCaptureButton(x));
            Label? quality = Descendants(actionPanel).OfType<Label>().FirstOrDefault();
            if (quality is not null)
            {
                int openWidth = openOutput?.PreferredSize.Width ?? 152;
                int width = Math.Max(220, body.ClientSize.Width - body.Padding.Horizontal - openWidth - 40);
'@ `
        -New @'
            Button[] auxiliaryButtons = Descendants(actionPanel).OfType<Button>()
                .Where(x => !IsPrimaryCaptureButton(x))
                .ToArray();
            Label? quality = Descendants(actionPanel).OfType<Label>().FirstOrDefault();
            if (quality is not null)
            {
                int auxiliaryWidth = Math.Min(360, Math.Max(152, auxiliaryButtons.Sum(x => x.PreferredSize.Width + x.Margin.Horizontal)));
                int width = Math.Max(220, body.ClientSize.Width - body.Padding.Horizontal - auxiliaryWidth - 40);
'@ `
        -Marker 'int auxiliaryWidth = Math.Min(360'

    Replace-Literal -Path $ui `
        -Old @'
    {
        if (body.IsDisposed || form.IsDisposed) return;

        int maxLabel = body.Controls.OfType<Label>()
'@ `
        -New @'
    {
        if (body.IsDisposed || form.IsDisposed) return;
        StandaloneButtonTextFit.Apply(form);

        int maxLabel = body.Controls.OfType<Label>()
'@ `
        -Marker 'StandaloneButtonTextFit.Apply(form);'

    Replace-Literal -Path $ui `
        -Old @'
        Button? open = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Open output folder", StringComparison.OrdinalIgnoreCase));
        Label? quality = Descendants(panel).OfType<Label>().FirstOrDefault();

        if (capture is null || open is null || quality is null)
        {
            detail = "primary actions were not rebuilt into the responsive action layout";
            return false;
        }
        if (capture.Bounds.Height < 52 || open.Bounds.Height < 34)
        {
            detail = "primary action button height is too small";
            return false;
        }
'@ `
        -New @'
        Button? open = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Open output folder", StringComparison.OrdinalIgnoreCase));
        Button? diagnostics = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Export diagnostics", StringComparison.OrdinalIgnoreCase));
        Label? quality = Descendants(panel).OfType<Label>().FirstOrDefault();

        if (capture is null || open is null || diagnostics is null || quality is null)
        {
            detail = "primary actions were not rebuilt into the responsive action layout (Start/Open output/Export diagnostics/Quality required)";
            return false;
        }
        if (capture.Bounds.Height < 52 || open.Bounds.Height < 34 || diagnostics.Bounds.Height < 34)
        {
            detail = "primary action button height is too small";
            return false;
        }
'@ `
        -Marker 'Export diagnostics/Quality required'
} else {
    Replace-Literal -Path $ui `
        -Old @'
            Button? openOutput = Descendants(actionPanel).OfType<Button>()
                .FirstOrDefault(x => !x.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase) &&
                                     !x.Text.StartsWith("Stop capture", StringComparison.OrdinalIgnoreCase));
            Label? quality = Descendants(actionPanel).OfType<Label>().FirstOrDefault();
            if (quality is not null)
            {
                int openWidth = openOutput?.PreferredSize.Width ?? 150;
                int width = Math.Max(260, body.ClientSize.Width - body.Padding.Horizontal - openWidth - 36);
'@ `
        -New @'
            Button[] auxiliaryButtons = Descendants(actionPanel).OfType<Button>()
                .Where(x => !x.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase) &&
                            !x.Text.StartsWith("Stop capture", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Label? quality = Descendants(actionPanel).OfType<Label>().FirstOrDefault();
            if (quality is not null)
            {
                int auxiliaryWidth = Math.Min(360, Math.Max(150, auxiliaryButtons.Sum(x => x.PreferredSize.Width + x.Margin.Horizontal)));
                int width = Math.Max(260, body.ClientSize.Width - body.Padding.Horizontal - auxiliaryWidth - 36);
'@ `
        -Marker 'int auxiliaryWidth = Math.Min(360'

    Replace-Literal -Path $ui `
        -Old @'
    {
        int labelColumn = body.ColumnStyles.Count > 0
'@ `
        -New @'
    {
        StandaloneButtonTextFit.Apply(form);

        int labelColumn = body.ColumnStyles.Count > 0
'@ `
        -Marker 'StandaloneButtonTextFit.Apply(form);'

    Replace-Literal -Path $ui `
        -Old @'
        Button? open = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Open output folder", StringComparison.OrdinalIgnoreCase));
        Label? quality = Descendants(panel).OfType<Label>().FirstOrDefault();

        if (capture is null || open is null || quality is null)
        {
            detail = "primary actions were not rebuilt into the responsive action layout";
            return false;
        }

        if (capture.Bounds.Height < 54 || open.Bounds.Height < 32)
        {
            detail = "primary action button height is too small";
            return false;
        }
'@ `
        -New @'
        Button? open = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Open output folder", StringComparison.OrdinalIgnoreCase));
        Button? diagnostics = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Export diagnostics", StringComparison.OrdinalIgnoreCase));
        Label? quality = Descendants(panel).OfType<Label>().FirstOrDefault();

        if (capture is null || open is null || diagnostics is null || quality is null)
        {
            detail = "primary actions were not rebuilt into the responsive action layout (Start/Open output/Export diagnostics/Quality required)";
            return false;
        }

        if (capture.Bounds.Height < 54 || open.Bounds.Height < 32 || diagnostics.Bounds.Height < 32)
        {
            detail = "primary action button height is too small";
            return false;
        }
'@ `
        -Marker 'Export diagnostics/Quality required'
}

# Do not throw away validator details.
Replace-Literal -Path $program `
    -Old @'
                if (!StandaloneUiPolish.Validate(form, out _)) return 15;
'@ `
    -New @'
                if (!StandaloneUiPolish.Validate(form, out string layoutDetail))
                {
                    LongCaptureLog.Warn($"responsive GUI self-test failed detail={LongCaptureLog.OneLine(layoutDetail)}");
                    return 15;
                }
'@ `
    -Marker 'responsive GUI self-test failed detail='

# Engine hardening retained unchanged.
Replace-Literal -Path $manager `
    -Old @'
                            int modRepairedOverlayTiles = 0;

                            if (modHasAnchor)
                            {
'@ `
    -New @'
                            int modRepairedOverlayTiles = 0;

                            if (modHasAnchor && Result != null)
                            {
                                modRepairedOverlayTiles +=
                                    ShareXModStaticOverlayCleaner.TryRepairPreviousResultTail(
                                        Result,
                                        previousScreenshot,
                                        lastScreenshot,
                                        modAnchor.ScrollDelta);
                            }

                            if (modHasAnchor)
                            {
'@ `
    -Marker 'TryRepairPreviousResultTail('

Replace-Literal -Path $settle `
    -Old '            const int tileHeight = 6;' `
    -New '            const int tileHeight = 3;' `
    -Marker 'const int tileHeight = 3;'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.3 RC hardening compatibility passed (modernV017=$isModernV017)." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.3 RC hardening hooks applied (modernV017=$isModernV017)." -ForegroundColor Green
}
