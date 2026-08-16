[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Loop7-10 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[loop7-10] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "Loop7-10 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[loop7-10] applied/compatible: $Marker" -ForegroundColor Cyan
}

$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$legacyFixture = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModV019RecoveryTailSelfTests.cs"

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
# persistence confirmation. The v0.1.9 fixture is retained for compatibility, but its old pair of
# large solid-blue rectangles was a pre-v0.1.10 stress shape whose expanded write footprint (11.07%)
# now correctly violates the 9.5% destructive-write cap. Modernize it to the real product shape: one
# bottom Back control and a pale counter with tiny blue glyphs. This still exercises tail repair while
# keeping the newer safety cap intact.
Replace-Literal -Path $legacyFixture `
  -Old '                for (int x = 0; x < Width; x += 11)' `
  -New '                for (int x = 0; x < (int)(Width * 0.68); x += 11)' `
  -Marker 'for (int x = 0; x < (int)(Width * 0.68); x += 11)'

Replace-Literal -Path $legacyFixture `
  -Old '            int fixedY = Height - 178;' `
  -New '            int fixedY = Height - 100; // v0.1.10 compatibility: bottom-anchored like the real Back/counter control.' `
  -Marker 'fixedY = Height - 100; // v0.1.10 compatibility'

Replace-Literal -Path $legacyFixture `
  -Old '            g.FillRectangle(blue, fixedX + 12, fixedY + 82, 164, 70);' `
  -New @'
            g.FillRectangle(white, fixedX + 12, fixedY + 58, 164, 42);
            g.FillRectangle(blue, fixedX + 48, fixedY + 72, 36, 8);
'@ `
  -Marker 'g.FillRectangle(blue, fixedX + 48, fixedY + 72, 36, 8);'

Replace-Literal -Path $legacyFixture `
  -Old '            if (blueBands > 4)' `
  -New '            if (blueBands > 2)' `
  -Marker 'if (blueBands > 2)'

Replace-Literal -Path $legacyFixture `
  -Old 'safeCeiling=4' `
  -New 'safeCeiling=2' `
  -Marker 'safeCeiling=2'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 loop7-10 safety compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 loop7-10 grouped/envelope/write-area/legacy-fixture safety applied." -ForegroundColor Green
}
