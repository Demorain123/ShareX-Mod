[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Loop7 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[loop7] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "Loop7 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[loop7] applied/compatible: $Marker" -ForegroundColor Cyan
}

$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$legacyFixture = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModV019RecoveryTailSelfTests.cs"

# Replace frame-wide risk veto with grouped-component area safety. Stationary risk remains telemetry;
# destructive repair is decided per proximity-group, so legitimate Back+counter controls are not
# blocked merely because they occupy a visually high-contrast bottom/right area.
Replace-Literal -Path $compositor `
  -Old @'
            bool repairRiskAcceptable = stationary.RiskRatio < 0.135;
            if (!repairRiskAcceptable) rejectedLargeComponents++;
'@ `
  -New @'
            bool repairRiskAcceptable = true; // loop7: area safety is enforced on gap-grouped persistent components, not a global stationary percentage.
'@ `
  -Marker 'area safety is enforced on gap-grouped persistent components'

Replace-Literal -Path $compositor `
  -Old '            foreach (List<int> component in ConnectedComponents(persistentTiles, columns, rows))' `
  -New '            foreach (List<int> component in ShareXModComponentGroupingV020.Group(persistentTiles, columns, rows))' `
  -Marker 'ShareXModComponentGroupingV020.Group(persistentTiles, columns, rows)'

# v0.1.10 intentionally makes the initial right/bottom edge provisional after two-transition
# persistence confirmation. Update the old v0.1.9 compatibility fixture so it still protects the
# committed document body, but does not forbid this newer, stricter edge-only repair policy.
Replace-Literal -Path $legacyFixture `
  -Old '                for (int x = 0; x < Width; x += 11)' `
  -New '                for (int x = 0; x < (int)(Width * 0.68); x += 11)' `
  -Marker 'for (int x = 0; x < (int)(Width * 0.68); x += 11)'

Replace-Literal -Path $legacyFixture `
  -Old '            if (blueBands > 4)' `
  -New '            if (blueBands > 2)' `
  -Marker 'if (blueBands > 2)'

Replace-Literal -Path $legacyFixture `
  -Old 'safeCeiling=4' `
  -New 'safeCeiling=2' `
  -Marker 'safeCeiling=2'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 loop7 component-gate compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 loop7 component-gate applied." -ForegroundColor Green
}
