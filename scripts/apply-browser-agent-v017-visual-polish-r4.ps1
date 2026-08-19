[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"

function Patch-Literal {
    param([string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($ui)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.1.7-polish-r4] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.7 polish-r4 anchor missing: $Marker" }
    Write-Host "[v0.1.7-polish-r4] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($ui, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[v0.1.7-polish-r4] applied: $Marker" -ForegroundColor Cyan
    }
}

Patch-Literal `
    -Old @'
            capture.Text = capture.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase)
                ? "Start capture (F8)"
                : capture.Text;
'@ `
    -New @'
            capture.Text = capture.Text.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)
                ? capture.Text
                : "Start capture (F8)";
'@ `
    -Marker 'capture.Text = capture.Text.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)'

Patch-Literal `
    -Old @'
            foreach (Button button in actions.Controls.OfType<Button>())
            {
                button.Dock = DockStyle.None;
                button.AutoSize = true;
                button.MinimumSize = new Size(button.MinimumSize.Width, 38);
                button.Margin = new Padding(0, 0, 8, 6);
            }

            speedLabel.AutoSize = true;
'@ `
    -New @'
            foreach (Button button in actions.Controls.OfType<Button>())
            {
                button.Dock = DockStyle.None;
                button.AutoSize = true;
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.MinimumSize = new Size(0, 38);
                button.MaximumSize = Size.Empty;
                button.Margin = new Padding(0, 0, 8, 6);
                Size preferred = button.GetPreferredSize(Size.Empty);
                button.Size = new Size(preferred.Width, Math.Max(38, preferred.Height));
            }

            speedLabel.AutoSize = true;
'@ `
    -Marker 'button.AutoSizeMode = AutoSizeMode.GrowAndShrink;'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 visual polish r4 compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.7 visual polish r4 applied: auxiliary command widths now shrink to preferred content and the primary label is compact." -ForegroundColor Green
}
