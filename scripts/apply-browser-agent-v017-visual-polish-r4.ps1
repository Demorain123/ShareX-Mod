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

function Patch-Regex {
    param([string]$Pattern, [string]$Replacement, [string]$Marker)
    $text = [IO.File]::ReadAllText($ui)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.1.7-polish-r4] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    $match = [regex]::Match($text, $Pattern, [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        $hintIndex = $text.IndexOf('capture.Bounds.Height', [StringComparison]::Ordinal)
        $hint = if ($hintIndex -ge 0) {
            $start = [Math]::Max(0, $hintIndex - 100)
            $length = [Math]::Min(360, $text.Length - $start)
            $text.Substring($start, $length).Replace("`r", ' ').Replace("`n", ' ')
        } else { 'capture.Bounds.Height not found' }
        throw "v0.1.7 polish-r4 regex anchor missing: $Marker; nearby=$hint"
    }
    Write-Host "[v0.1.7-polish-r4] compatible regex: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $updated = [regex]::Replace($text, $Pattern, $Replacement, [Text.RegularExpressions.RegexOptions]::Singleline)
        [IO.File]::WriteAllText($ui, $updated, [Text.UTF8Encoding]::new($true))
        Write-Host "[v0.1.7-polish-r4] applied regex: $Marker" -ForegroundColor Cyan
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

# Let secondary commands shrink to their preferred content width, but keep the primary command
# deliberately taller. Microsoft command-bar guidance prioritizes common commands and lets less
# important commands consume less room; we mirror that hierarchy without sacrificing the 840px
# regression viewport.
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
                bool primaryCommand = IsPrimaryCaptureButton(button);
                int minimumHeight = primaryCommand ? 42 : 38;
                button.Dock = DockStyle.None;
                button.AutoSize = true;
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.MinimumSize = new Size(0, minimumHeight);
                button.MaximumSize = Size.Empty;
                button.Margin = new Padding(0, 0, primaryCommand ? 10 : 6, 6);
                Size preferred = button.GetPreferredSize(Size.Empty);
                button.Size = new Size(preferred.Width, Math.Max(minimumHeight, preferred.Height));
            }

            speedLabel.AutoSize = true;
'@ `
    -Marker 'int minimumHeight = primaryCommand ? 42 : 38;'

# The old validator belongs to the former bottom 54px CTA. Earlier overlay chains have used
# slightly different thresholds/formatting, so match the semantic validation block rather than a
# single historical literal. The new compact top CTA still has a strict >=40px runtime hierarchy.
$validationPattern = 'if\s*\(capture\.Bounds\.Height\s*<\s*\d+\s*\|\|\s*open\.Bounds\.Height\s*<\s*34\)\s*\{\s*detail\s*=\s*"primary action button height is too small";\s*return false;\s*\}'
$validationReplacement = @'
if (capture.Bounds.Height < 40 || open.Bounds.Height < 34)
        {
            detail = $"primary action button height is too small: capture={capture.Bounds.Height}px open={open.Bounds.Height}px";
            return false;
        }
'@
Patch-Regex -Pattern $validationPattern -Replacement $validationReplacement -Marker 'capture={capture.Bounds.Height}px open={open.Bounds.Height}px'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 visual polish r4 compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.7 visual polish r4 applied: compact secondary widths, 42px primary hierarchy, concise CTA and geometry-aware validation." -ForegroundColor Green
}
