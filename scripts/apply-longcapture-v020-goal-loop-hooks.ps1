[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "v0.1.10 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.1.10] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.10 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old,$New)
        [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.10] applied/compatible: $Marker" -ForegroundColor Cyan
}

$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
# ShareX-Mod sources are linked into ShareX.ScreenCaptureLib from mod-overlay by the build target;
# they are not physically copied into ShareX.ScreenCaptureLib. Patch the actual linked source files.
$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$goalTest = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModV020GoalLoopSelfTests.cs"

Replace-Literal -Path $automation `
  -Old '        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",' `
  -New @'
        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV020GoalLoopSelfTests",
'@ `
  -Marker '"ShareX.ScreenCaptureLib.ShareXModV020GoalLoopSelfTests",'

# Loop-2 review found a short-scroll hazard: a large expanded repair rectangle can map its source
# back into the fixed control itself. Expand enough to cover a control+counter group, but copy only
# narrow source strips that do NOT intersect the current stationary mask.
Replace-Literal -Path $compositor `
  -Old '                int marginY = TileHeight * 2;' `
  -New '                int marginY = TileHeight * 3; // v0.1.10: cover separated control/counter subparts while source-safe strips prevent self-copy.' `
  -Marker 'marginY = TileHeight * 3; // v0.1.10'

Replace-Literal -Path $compositor `
  -Old @'
                int repairWidth = x1 - x0;
                int repairHeight = y1 - y0;
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.None;
                    graphics.DrawImage(
                        current,
                        new Rectangle(x0, resultViewportTop + y0, repairWidth, repairHeight),
                        new Rectangle(x0, sourceY0, repairWidth, repairHeight),
                        GraphicsUnit.Pixel);
                }

                tailRepairComponents++;
                tailRepairPixelsApprox += repairWidth * repairHeight;
'@ `
  -New @'
                int repairWidth = x1 - x0;
                int repairHeight = y1 - y0;
                int copiedPixels = ShareXModSafeTailCopyV020.Copy(
                    result,
                    current,
                    currentTiles,
                    columns,
                    resultViewportTop,
                    x0,
                    y0,
                    x1,
                    y1,
                    currentDelta,
                    TileWidth,
                    TileHeight);
                if (copiedPixels <= 0) continue;

                tailRepairComponents++;
                tailRepairPixelsApprox += copiedPixels;
'@ `
  -Marker 'int copiedPixels = ShareXModSafeTailCopyV020.Copy('

# The first loop fixture intentionally had two solid blue rectangles. Real Linux.do has one large
# blue Back button plus a pale counter with small blue glyphs. Keep the counter present, but model its
# shape accurately so the gate counts repeated controls rather than counting every internal glyph row.
Replace-Literal -Path $goalTest `
  -Old '            g.FillRectangle(blue, x + 16, y + 94, 140, 46);' `
  -New @'
            g.FillRectangle(pale, x + 16, y + 94, 140, 46);
            g.FillRectangle(blue, x + 45, y + 105, 30, 7);
            g.FillRectangle(blue, x + 80, y + 122, 40, 7);
'@ `
  -Marker 'g.FillRectangle(blue, x + 45, y + 105, 30, 7);'

Replace-Literal -Path $goalTest `
  -Old '            bool hit = hits >= 8;' `
  -New '            bool hit = hits >= 20; // count a fixed control body, not tiny blue counter glyphs.' `
  -Marker 'bool hit = hits >= 20; // count a fixed control body'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 goal-loop hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 goal-loop hooks applied." -ForegroundColor Green
}
