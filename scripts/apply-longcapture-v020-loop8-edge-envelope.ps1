[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Loop8 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[loop8] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "Loop8 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[loop8] applied/compatible: $Marker" -ForegroundColor Cyan
}

$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"

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

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 loop8 edge-envelope compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 loop8 edge-envelope gate applied." -ForegroundColor Green
}
