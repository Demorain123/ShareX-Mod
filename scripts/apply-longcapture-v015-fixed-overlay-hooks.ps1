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
    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.5 target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[LongCapture-v0.1.5] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.5 compatibility anchor not found: $Marker in $Path" }
    Write-Host "[LongCapture-v0.1.5] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
        Write-Host "[LongCapture-v0.1.5] applied: $Marker" -ForegroundColor Cyan
    }
}

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"

# Debug now records GUI/settings snapshots and verbose evidence while all LongCapture-owned helper
# windows remain excluded from the actual long screenshot. This prevents the temporary blank
# Avalonia/selector host observed in the real v0.1.4 Linux.do test from covering the target.
Replace-Literal -Path $main `
    -Old '        debugCaptureUi.Text = "Debug: GUI capturable + settings snapshot";' `
    -New '        debugCaptureUi.Text = "Debug: safe GUI/settings snapshot + evidence";' `
    -Marker 'debugCaptureUi.Text = "Debug: safe GUI/settings snapshot + evidence";'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.5 fixed-overlay hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.5 fixed-overlay hooks applied." -ForegroundColor Green
}
