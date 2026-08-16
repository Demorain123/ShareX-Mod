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

# Loop-5: the original viewport is not truly safe to commit when it contains a fixed bottom/right
# control. Keep just the first transition's raw pair until a second transition confirms the same
# screen-coordinate stationary component. Then the initial viewport can be repaired from the first
# scrolled raw frame without guessing underlying pixels. After that, the initial raw pair is freed.
Replace-Literal -Path $compositor `
  -Old '        private int rejectedLargeComponents;' `
  -New @'
        private int rejectedLargeComponents;
        private Bitmap? initialRawBeforeScroll;
        private Bitmap? initialRawAfterFirstScroll;
        private int initialScrollDelta;
        private bool initialRepairPending;
'@ `
  -Marker 'private Bitmap? initialRawBeforeScroll;'

Replace-Literal -Path $compositor `
  -Old @'
        private void RepairProvisionalTail(
            Bitmap result,
            Bitmap previous,
            Bitmap current,
            int currentDelta,
            int previousDelta,
            HashSet<int> currentTiles,
            HashSet<int> priorTiles)
'@ `
  -New @'
        private void RepairProvisionalTail(
            Bitmap result,
            Bitmap previous,
            Bitmap current,
            int currentDelta,
            int previousDelta,
            HashSet<int> currentTiles,
            HashSet<int> priorTiles,
            int forcedResultViewportTop = -1)
'@ `
  -Marker 'int forcedResultViewportTop = -1)'

Replace-Literal -Path $compositor `
  -Old '            int resultViewportTop = result.Height - height;' `
  -New '            int resultViewportTop = forcedResultViewportTop >= 0 ? forcedResultViewportTop : result.Height - height;' `
  -Marker 'forcedResultViewportTop >= 0 ? forcedResultViewportTop'

# Loop-2 review found a short-scroll hazard: a large expanded repair rectangle can map its source
# back into the fixed control itself. Expand enough to cover a control+counter group, but copy only
# narrow source strips that do NOT intersect a two-transition persistent fixed mask. A one-frame
# stationary tile is not enough to veto source pixels because real Linux.do has broad white/right-edge
# regions that can be coincidentally stable for one transition.
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
                    persistentTiles,
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

# A large fixed panel can fragment into several individually-small tile components and bypass a
# per-component 5% area cap. Add a frame-level circuit breaker. The 13.5% threshold is intentionally
# above the maximum 12.86% stationary-risk observed in the user's v0.1.8 Linux.do evidence, while the
# adversarial giant-panel fixture produces roughly 14-18%. When tripped we preserve pixels and only
# report risk; destructive tail repair is disabled for that transition.
#
# The same block also handles the initial viewport: first transition stores the raw pair; a later
# transition with persistent stationary evidence can repair viewport 0 using the first scrolled raw
# frame. This removes the first fixed-control occurrence without weakening committed-body safety.
Replace-Literal -Path $compositor `
  -Old @'
            if (stationary.RiskRatio >= 0.015) stationaryRiskFrames++;

            if (previousAppendDelta > 0 && previousStationaryTiles.Count > 0 && stationary.Tiles.Count > 0)
            {
                RepairProvisionalTail(
                    result,
                    previousRaw,
                    currentRaw,
                    scrollDelta,
                    previousAppendDelta,
                    stationary.Tiles,
                    previousStationaryTiles);
            }
'@ `
  -New @'
            if (stationary.RiskRatio >= 0.015) stationaryRiskFrames++;
            bool repairRiskAcceptable = stationary.RiskRatio < 0.135;
            if (!repairRiskAcceptable) rejectedLargeComponents++;

            if (appendCount == 0 && !initialRepairPending)
            {
                initialRawBeforeScroll?.Dispose();
                initialRawAfterFirstScroll?.Dispose();
                initialRawBeforeScroll = (Bitmap)previousRaw.Clone();
                initialRawAfterFirstScroll = (Bitmap)currentRaw.Clone();
                initialScrollDelta = scrollDelta;
                initialRepairPending = true;
            }
            else if (initialRepairPending && repairRiskAcceptable &&
                     initialRawBeforeScroll != null && initialRawAfterFirstScroll != null &&
                     previousStationaryTiles.Count > 0 && stationary.Tiles.Count > 0)
            {
                RepairProvisionalTail(
                    result,
                    initialRawBeforeScroll,
                    initialRawAfterFirstScroll,
                    initialScrollDelta,
                    initialRawBeforeScroll.Height,
                    stationary.Tiles,
                    previousStationaryTiles,
                    0);
                initialRawBeforeScroll.Dispose();
                initialRawAfterFirstScroll.Dispose();
                initialRawBeforeScroll = null;
                initialRawAfterFirstScroll = null;
                initialRepairPending = false;
            }

            if (repairRiskAcceptable && previousAppendDelta > 0 && previousStationaryTiles.Count > 0 && stationary.Tiles.Count > 0)
            {
                RepairProvisionalTail(
                    result,
                    previousRaw,
                    currentRaw,
                    scrollDelta,
                    previousAppendDelta,
                    stationary.Tiles,
                    previousStationaryTiles);
            }
'@ `
  -Marker 'initialRawBeforeScroll = (Bitmap)previousRaw.Clone();'

Replace-Literal -Path $compositor `
  -Old @'
        public void Dispose()
        {
            previousStationaryTiles.Clear();
            previousAppendDelta = 0;
        }
'@ `
  -New @'
        public void Dispose()
        {
            previousStationaryTiles.Clear();
            previousAppendDelta = 0;
            initialRawBeforeScroll?.Dispose();
            initialRawAfterFirstScroll?.Dispose();
            initialRawBeforeScroll = null;
            initialRawAfterFirstScroll = null;
            initialRepairPending = false;
        }
'@ `
  -Marker 'initialRawBeforeScroll?.Dispose();'

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

# Real-test parity: after two-transition confirmation, the first occurrence must be recoverable too;
# only the newest unresolved/final viewport may retain the fixed control.
Replace-Literal -Path $goalTest `
  -Old '            if (blueBands > 2)' `
  -New '            if (blueBands > 1)' `
  -Marker 'if (blueBands > 1)'

Replace-Literal -Path $goalTest `
  -Old '            if (CountFixedBlueBands(result) > 2)' `
  -New '            if (CountFixedBlueBands(result) > 1)' `
  -Marker 'if (CountFixedBlueBands(result) > 1)'

Replace-Literal -Path $goalTest `
  -Old 'Linux-like fixed control reduced to first/final occurrences' `
  -New 'Linux-like fixed control reduced to the final unresolved occurrence only' `
  -Marker 'Linux-like fixed control reduced to the final unresolved occurrence only'

Replace-Literal -Path $goalTest `
  -Old 'expected<=2 (first/final only)' `
  -New 'expected<=1 (final unresolved tail only)' `
  -Marker 'expected<=1 (final unresolved tail only)'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 goal-loop hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 goal-loop hooks applied." -ForegroundColor Green
}
