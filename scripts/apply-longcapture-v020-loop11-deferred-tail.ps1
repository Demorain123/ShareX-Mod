[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Loop11/14 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[loop11/14] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "Loop11/14 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[loop11/14] applied/compatible: $Marker" -ForegroundColor Cyan
}

$compositor = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTrustSplitCompositorV019.cs"
$quality = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModFinalQualitySummary.cs"

Replace-Literal -Path $compositor `
  -Old @'
    public static void ResetLive()
    {
'@ `
  -New @'
    public static ShareXModDeferredTailRepairTelemetry SnapshotLiveDeferredTailTelemetry()
    {
        lock (LiveSync) return liveSession?.SnapshotDeferredTailTelemetry() ?? default;
    }

    public static void ResetLive()
    {
'@ `
  -Marker 'SnapshotLiveDeferredTailTelemetry()'

Replace-Literal -Path $compositor `
  -Old @'
        private int rejectedLargeComponents;
        private Bitmap? initialRawBeforeScroll;
'@ `
  -New @'
        private int rejectedLargeComponents;
        private readonly ShareXModDeferredTailRepairV020 deferredTailRepairs = new();
        private Bitmap? initialRawBeforeScroll;
'@ `
  -Marker 'private readonly ShareXModDeferredTailRepairV020 deferredTailRepairs = new();'

Replace-Literal -Path $compositor `
  -Old @'
            bool repairRiskAcceptable = true; // loop7: area safety is enforced on persistent edge structure, not a global stationary percentage.

            if (appendCount == 0 && !initialRepairPending)
'@ `
  -New @'
            bool repairRiskAcceptable = true; // loop7: area safety is enforced on persistent edge structure, not a global stationary percentage.

            var persistentForDeferred = new HashSet<int>(stationary.Tiles);
            persistentForDeferred.IntersectWith(previousStationaryTiles);
            if (appendCount > 0)
            {
                deferredTailRepairs.AdvanceAndRepair(
                    result,
                    currentRaw,
                    scrollDelta,
                    persistentForDeferred,
                    (currentRaw.Width + TileWidth - 1) / TileWidth,
                    TileWidth,
                    TileHeight);
            }

            if (appendCount == 0 && !initialRepairPending)
'@ `
  -Marker 'deferredTailRepairs.AdvanceAndRepair('

Replace-Literal -Path $compositor `
  -Old @'
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
  -New @'
                ShareXModSafeTailCopyResult copyResult = ShareXModSafeTailCopyV020.CopyDetailed(
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
                if (!copyResult.Complete)
                {
                    deferredTailRepairs.Enqueue(
                        resultViewportTop,
                        x0,
                        y0,
                        x1,
                        y1,
                        currentDelta,
                        copyResult.RequiredPixels,
                        persistentTiles); // v0.1.10 deferred confirmed fixed evidence
                }
                if (copyResult.CopiedPixels <= 0) continue;

                tailRepairComponents++;
                tailRepairPixelsApprox += copyResult.CopiedPixels;
'@ `
  -Marker 'v0.1.10 deferred confirmed fixed evidence'

Replace-Literal -Path $compositor `
  -Old @'
        public ShareXModV019CompositorTelemetry SnapshotTelemetry() => new(
'@ `
  -New @'
        public ShareXModDeferredTailRepairTelemetry SnapshotDeferredTailTelemetry() => deferredTailRepairs.Snapshot();

        public ShareXModV019CompositorTelemetry SnapshotTelemetry() => new(
'@ `
  -Marker 'SnapshotDeferredTailTelemetry() => deferredTailRepairs.Snapshot();'

Replace-Literal -Path $compositor `
  -Old @'
                        policy = "committed-body-immutable-persistent-edge-provisional-tail-repair-v020",
                        telemetry = SnapshotTelemetry()
'@ `
  -New @'
                        policy = "committed-body-immutable-persistent-edge-provisional-tail-repair-v020-multiframe-atomic-persistent-veto",
                        telemetry = SnapshotTelemetry(),
                        deferredTail = SnapshotDeferredTailTelemetry()
'@ `
  -Marker 'deferredTail = SnapshotDeferredTailTelemetry()'

Replace-Literal -Path $quality `
  -Old @'
            ShareXModV019CompositorTelemetry trustSplitV019 = ShareXModTrustSplitCompositorV019.SnapshotLiveTelemetry();
            if (transitionV019.TerminalUnresolved > 0 || trustSplitV019.RejectedAppendCount > 0)
'@ `
  -New @'
            ShareXModV019CompositorTelemetry trustSplitV019 = ShareXModTrustSplitCompositorV019.SnapshotLiveTelemetry();
            ShareXModDeferredTailRepairTelemetry deferredTailV020 = ShareXModTrustSplitCompositorV019.SnapshotLiveDeferredTailTelemetry();
            if (transitionV019.TerminalUnresolved > 0 || trustSplitV019.RejectedAppendCount > 0 ||
                deferredTailV020.Expired > 0 || deferredTailV020.Pending > 1)
'@ `
  -Marker 'ShareXModDeferredTailRepairTelemetry deferredTailV020 ='

Replace-Literal -Path $quality `
  -Old @'
                      trustSplitV019.TailRepairComponents > 0 || trustSplitV019.StationaryRiskFrames > 0) &&
'@ `
  -New @'
                      trustSplitV019.TailRepairComponents > 0 || trustSplitV019.StationaryRiskFrames > 0 ||
                      deferredTailV020.Completed > 0 || deferredTailV020.Pending == 1) &&
'@ `
  -Marker 'deferredTailV020.Completed > 0 || deferredTailV020.Pending == 1'

Replace-Literal -Path $quality `
  -Old @'
                transitionV018 = new
                {
'@ `
  -New @'
                deferredTailV020 = new
                {
                    deferredTailV020.Queued,
                    deferredTailV020.Completed,
                    deferredTailV020.Pending,
                    deferredTailV020.Expired,
                    deferredTailV020.Retried,
                    deferredTailV020.CopiedPixels
                },
                transitionV018 = new
                {
'@ `
  -Marker 'deferredTailV020.Queued'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 loop11/14 deferred-tail compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 loop11/14 multi-frame atomic deferred-tail repair applied." -ForegroundColor Green
}
