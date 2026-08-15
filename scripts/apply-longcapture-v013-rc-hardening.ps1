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
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
        Write-Host "[LongCapture-v0.1.3-rc] applied: $Marker" -ForegroundColor Cyan
    }
}

$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$program = Join-Path $repoRoot "LongCapture.Standalone\Program.cs"

# v0.1.3 added Export diagnostics after the original UI hardening was written. Preserve every
# auxiliary action button instead of assuming there can only ever be one non-capture button.
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
    -Marker "Button[] auxiliaryButtons = actionPanel.Controls.OfType<Button>()"

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
    -Marker "foreach (Button auxiliary in auxiliaryButtons)"

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
    -Marker "var auxiliaryPanel = new FlowLayoutPanel"

# Reflow quality text against the complete auxiliary-button group, not whichever button happens
# to be first in Controls order.
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
    -Marker "int auxiliaryWidth = Math.Min(360"

# Validation must prove both output and diagnostics actions survived the responsive rebuild.
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
    -Marker "Export diagnostics/Quality required"

# Do not throw away the validator's reason. A future CI failure must say which control clipped.
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
    -Marker "responsive GUI self-test failed detail="

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.3 RC hardening compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.3 RC hardening hooks applied." -ForegroundColor Green
}
