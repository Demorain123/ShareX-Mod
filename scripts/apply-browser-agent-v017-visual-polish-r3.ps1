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
        Write-Host "[v0.1.7-polish-r3] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.7 polish-r3 anchor missing: $Marker" }
    Write-Host "[v0.1.7-polish-r3] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[v0.1.7-polish-r3] applied: $Marker" -ForegroundColor Cyan
    }
}

# Windows command-bar guidance favors short, importance-ordered labels. Keep the primary capture
# command first and visible, then make the auxiliary commands compact enough to stay on one row at
# the user's ~925px window and our 840px regression viewport.
Patch-Literal -Path $ui `
    -Old @'
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
'@ `
    -New @'
        Button? capture = actionPanel is null ? null : Descendants(actionPanel).OfType<Button>().FirstOrDefault(IsPrimaryCaptureButton);
        if (capture is not null)
        {
            capture.Parent?.Controls.Remove(capture);
            capture.Text = capture.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase)
                ? "Start capture (F8)"
                : capture.Text;
            capture.AccessibleDescription = "Start or stop the current long screenshot capture.";
            capture.Dock = DockStyle.None;
            capture.AutoSize = true;
            capture.MinimumSize = new Size(168, 40);
            capture.Margin = new Padding(0, 0, 8, 6);
            StyleButton(capture, highContrast, primary: true);
            actions.Controls.Add(capture);
            actions.Controls.SetChildIndex(capture, 0);
        }

        Button? foreground = actions.Controls.OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Foreground target", StringComparison.OrdinalIgnoreCase));
        Button? refresh = actions.Controls.OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Refresh targets", StringComparison.OrdinalIgnoreCase));
        Button? logs = actions.Controls.OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Open logs folder", StringComparison.OrdinalIgnoreCase));
        if (foreground is not null)
        {
            foreground.Text = "Foreground (F7)";
            foreground.MinimumSize = new Size(132, 38);
            foreground.AccessibleDescription = "Use the foreground window as the capture target.";
        }
        if (refresh is not null)
        {
            refresh.Text = "Refresh";
            refresh.MinimumSize = new Size(82, 38);
            refresh.AccessibleDescription = "Refresh the available capture targets.";
        }
        if (logs is not null)
        {
            logs.Text = "Logs";
            logs.MinimumSize = new Size(68, 38);
            logs.AccessibleDescription = "Open the LongCapture logs folder.";
        }
'@ `
    -Marker 'capture.Text = capture.Text.StartsWith("Start long capture"'

Patch-Literal -Path $ui `
    -Old @'
    private static bool IsPrimaryCaptureButton(Button button) =>
        button.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase) ||
        button.Text.StartsWith("Stop capture", StringComparison.OrdinalIgnoreCase) ||
'@ `
    -New @'
    private static bool IsPrimaryCaptureButton(Button button) =>
        button.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase) ||
        button.Text.StartsWith("Start capture", StringComparison.OrdinalIgnoreCase) ||
        button.Text.StartsWith("Stop capture", StringComparison.OrdinalIgnoreCase) ||
'@ `
    -Marker 'button.Text.StartsWith("Start capture", StringComparison.OrdinalIgnoreCase)'

Patch-Literal -Path $adaptive `
    -Old '            speedHint.Text = $"{name} · fixed timing for native capture";' `
    -New '            speedHint.Text = "Native mode · fixed timing";' `
    -Marker 'speedHint.Text = "Native mode · fixed timing";'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 visual polish r3 compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.7 visual polish r3 applied: concise one-row commands, accessible descriptions and less redundant copy." -ForegroundColor Green
}
