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
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.7-responsive] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.7 responsive follow-up anchor missing: '$Marker' in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.7-responsive] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.7-responsive] applied: $Marker" -ForegroundColor Cyan
    }
}

$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$adaptiveUi = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentAdaptiveUiV015.cs"

# Default window size should respect the current monitor instead of forcing a desktop-sized
# 1080x820 shell onto a smaller work area. Keep a usable minimum but leave breathing room for
# taskbar/window chrome on 1024x768-class CI/remote desktops.
Replace-Literal -Path $ui `
    -Old @'
            form.MinimumSize = new Size(820, 700);
            if (form.Width < 1080 || form.Height < 820)
            {
                form.Size = new Size(Math.Max(form.Width, 1080), Math.Max(form.Height, 820));
            }
'@ `
    -New @'
            form.MinimumSize = new Size(820, 680);
            Rectangle workArea = Screen.FromControl(form).WorkingArea;
            int preferredWidth = Math.Min(1120, Math.Max(820, workArea.Width - 64));
            int preferredHeight = Math.Min(860, Math.Max(680, workArea.Height - 64));
            if (form.Width < preferredWidth || form.Height < preferredHeight)
            {
                form.Size = new Size(Math.Max(form.Width, preferredWidth), Math.Max(form.Height, preferredHeight));
            }
'@ `
    -Marker 'int preferredWidth = Math.Min(1120'

# Normalize every legacy viewport literal independently rather than requiring two byte-identical
# blocks. Earlier overlay hooks can legitimately rewrite one of the matrices, and the previous
# exact-count gate failed before build even though the UI source itself was compatible.
$viewportMap = [ordered]@{
    'new Size(840, 720)' = 'new Size(840, 680)'
    'new Size(925, 760)' = 'new Size(925, 720)'
    'new Size(1080, 820)' = 'new Size(900, 700)'
    'new Size(1280, 900)' = 'new Size(1000, 740)'
}
$text = [IO.File]::ReadAllText($ui)
$legacyHits = 0
foreach ($oldLiteral in $viewportMap.Keys) {
    $legacyHits += ([regex]::Matches($text, [regex]::Escape($oldLiteral))).Count
}
$alreadyNormalized = $text.Contains('new Size(840, 680)') -and
                     $text.Contains('new Size(900, 700)') -and
                     $text.Contains('new Size(925, 720)') -and
                     $text.Contains('new Size(1000, 740)')
if ($legacyHits -eq 0 -and $alreadyNormalized) {
    Write-Host "[BrowserAgent-v0.1.7-responsive] hosted-desktop viewport matrix already present." -ForegroundColor DarkYellow
} elseif ($legacyHits -eq 0) {
    throw "No legacy or normalized v0.1.7 viewport literals were found."
} elseif (-not $CheckOnly) {
    foreach ($oldLiteral in $viewportMap.Keys) {
        $text = $text.Replace($oldLiteral, $viewportMap[$oldLiteral])
    }
    [IO.File]::WriteAllText($ui, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.7-responsive] normalized hosted-desktop viewport literals (legacyHits=$legacyHits)." -ForegroundColor Cyan
} else {
    Write-Host "[BrowserAgent-v0.1.7-responsive] compatible: hosted-desktop viewport normalization (legacyHits=$legacyHits)." -ForegroundColor Green
}

Replace-Literal -Path $ui `
    -Old '        detail = "v0.1.7 responsive/modern layout checks passed at 840/925/1080/1280 widths";' `
    -New '        detail = "v0.1.7 responsive/modern layout checks passed at 840/900/925/1000 window widths";' `
    -Marker '840/900/925/1000 window widths'

# Make the visual audit itself prove that the four requested widths were truly applied. This
# prevents a hosted desktop's maximum tracking size from silently producing four copies of the
# same viewport while filenames pretend otherwise.
Replace-Literal -Path $ui `
    -Old @'
            Size original = form.Size;
            var captures = new List<object>();
            try
            {
                _ = form.Handle;
                foreach (Size size in new[]
'@ `
    -New @'
            Size original = form.Size;
            FormWindowState originalState = form.WindowState;
            Size originalMaximum = form.MaximumSize;
            var captures = new List<object>();
            var observedClientWidths = new HashSet<int>();
            try
            {
                _ = form.Handle;
                form.WindowState = FormWindowState.Normal;
                form.MaximumSize = Size.Empty;
                foreach (Size size in new[]
'@ `
    -Marker 'var observedClientWidths = new HashSet<int>();'

Replace-Literal -Path $ui `
    -Old @'
                    int width = Math.Max(1, form.ClientSize.Width);
                    int height = Math.Max(1, form.ClientSize.Height);
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                    string name = $"LongCapture-v017-ui-{size.Width}x{size.Height}.png";
                    string path = Path.Combine(outputDirectory, name);
                    bitmap.Save(path, ImageFormat.Png);
                    captures.Add(new { size = $"{size.Width}x{size.Height}", file = name, bytes = new FileInfo(path).Length });
'@ `
    -New @'
                    int width = Math.Max(1, form.ClientSize.Width);
                    int height = Math.Max(1, form.ClientSize.Height);
                    if (size.Width - width > 100 || size.Height - height > 120)
                    {
                        throw new InvalidOperationException(
                            $"visual-audit viewport was constrained unexpectedly: requestedWindow={size.Width}x{size.Height} actualClient={width}x{height}");
                    }
                    observedClientWidths.Add(width);
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                    string name = $"LongCapture-v017-ui-{size.Width}x{size.Height}.png";
                    string path = Path.Combine(outputDirectory, name);
                    bitmap.Save(path, ImageFormat.Png);
                    captures.Add(new
                    {
                        requestedWindow = $"{size.Width}x{size.Height}",
                        actualClient = $"{width}x{height}",
                        file = name,
                        bytes = new FileInfo(path).Length
                    });
'@ `
    -Marker 'visual-audit viewport was constrained unexpectedly'

Replace-Literal -Path $ui `
    -Old @'
                }
            }
            finally
            {
                form.Size = original;
                form.PerformLayout();
            }

            string manifestPath = Path.Combine(outputDirectory, "ui-audit.json");
'@ `
    -New @'
                }
                if (observedClientWidths.Count != 4)
                {
                    throw new InvalidOperationException(
                        $"visual-audit did not exercise four distinct responsive widths: observed={string.Join(",", observedClientWidths.OrderBy(x => x))}");
                }
            }
            finally
            {
                form.MaximumSize = originalMaximum;
                form.WindowState = originalState;
                form.Size = original;
                form.PerformLayout();
            }

            string manifestPath = Path.Combine(outputDirectory, "ui-audit.json");
'@ `
    -Marker 'visual-audit did not exercise four distinct responsive widths'

# Progressive disclosure in the global speed command row: Browser benchmark/calibration/live
# telemetry are useful only in Browser Assisted mode, so do not leave three grey disabled controls
# consuming space and attention in Normal/Smart/Teach/Recipe modes.
Replace-Literal -Path $adaptiveUi `
    -Old @'
        benchmarkButton.Enabled = browserMode;
        calibrationToggle.Enabled = browserMode;
        liveMonitorToggle.Enabled = browserMode;
'@ `
    -New @'
        benchmarkButton.Enabled = browserMode;
        calibrationToggle.Enabled = browserMode;
        liveMonitorToggle.Enabled = browserMode;
        benchmarkButton.Visible = browserMode;
        calibrationToggle.Visible = browserMode;
        liveMonitorToggle.Visible = browserMode;
'@ `
    -Marker 'benchmarkButton.Visible = browserMode;'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 responsive follow-up compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.7 responsive follow-up applied: screen-aware default, real multi-width audit, Browser-only progressive disclosure." -ForegroundColor Green
}
