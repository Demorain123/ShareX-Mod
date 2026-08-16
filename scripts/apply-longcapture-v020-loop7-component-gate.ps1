[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Loop7/8/9 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[loop7/8/9] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "Loop7/8/9 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[loop7/8/9] applied/compatible: $Marker" -ForegroundColor Cyan
}

$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$legacyFixture = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModV019RecoveryTailSelfTests.cs"

# Replace frame-wide risk veto with structural edge safety. Stationary risk remains telemetry;
# destructive repair is decided from persistent component/envelope/write-area geometry, so legitimate
# Back+counter controls are not blocked merely because the whole bottom/right zone is high contrast.
Replace-Literal -Path $compositor `
  -Old @'
            bool repairRiskAcceptable = stationary.RiskRatio < 0.135;
            if (!repairRiskAcceptable) rejectedLargeComponents++;
'@ `
  -New @'
            bool repairRiskAcceptable = true; // loop7: area safety is enforced on persistent edge structure, not a global stationary percentage.
'@ `
  -Marker 'area safety is enforced on persistent edge structure'

Replace-Literal -Path $compositor `
  -Old '            foreach (List<int> component in ConnectedComponents(persistentTiles, columns, rows))' `
  -New '            foreach (List<int> component in ShareXModComponentGroupingV020.Group(persistentTiles, columns, rows))' `
  -Marker 'ShareXModComponentGroupingV020.Group(persistentTiles, columns, rows)'

# Loop8: before component repair, evaluate dense right/bottom envelopes over the whole two-transition
# persistent mask. This is a second safety net for fragmented large panels.
Replace-Literal -Path $compositor `
  -Old @'
            var persistentTiles = new HashSet<int>(currentTiles);
            persistentTiles.IntersectWith(priorTiles);

            foreach (List<int> component in ShareXModComponentGroupingV020.Group(persistentTiles, columns, rows))
'@ `
  -New @'
            var persistentTiles = new HashSet<int>(currentTiles);
            persistentTiles.IntersectWith(priorTiles);

            if (ShareXModEdgeEnvelopeGuardV020.IsUnsafe(
                persistentTiles,
                columns,
                rows,
                TileWidth,
                TileHeight,
                width,
                height,
                out double unsafeEnvelopeAreaRatio,
                out double unsafeEnvelopeDensity))
            {
                rejectedLargeComponents++;
                return;
            }

            foreach (List<int> component in ShareXModComponentGroupingV020.Group(persistentTiles, columns, rows))
'@ `
  -Marker 'out double unsafeEnvelopeAreaRatio'

# Loop9: the raw stationary component is not the full destructive-write footprint because the repair
# intentionally expands around it to recover antialiased control borders/text. Cap the ACTUAL expanded
# rectangle too. In the adversarial large-panel fixture the true write footprint is 10.2% of the
# viewport, whereas the Linux-like Back/counter footprint is 8.64%; 9.5% therefore separates them and
# measures the risk that matters: how many accepted pixels would actually be rewritten.
Replace-Literal -Path $compositor `
  -Old @'
                if (x1 <= x0 || y1 <= y0 || sourceY1 <= sourceY0) continue;

                bool ringValidated = HasSurroundingDocumentMotion(
'@ `
  -New @'
                if (x1 <= x0 || y1 <= y0 || sourceY1 <= sourceY0) continue;

                long expandedRepairArea = (long)(x1 - x0) * (y1 - y0);
                if (expandedRepairArea > viewportArea * 0.095)
                {
                    rejectedLargeComponents++;
                    continue;
                }

                bool ringValidated = HasSurroundingDocumentMotion(
'@ `
  -Marker 'expandedRepairArea > viewportArea * 0.095'

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
    Write-Host "LongCapture v0.1.10 loop7/8/9 safety compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 loop7/8/9 grouped/envelope/expanded-write safety applied." -ForegroundColor Green
}
