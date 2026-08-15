[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
if (-not (Test-Path -LiteralPath $manager)) {
    throw "ScrollingCaptureManager.cs not found."
}

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $text = [IO.File]::ReadAllText($manager)

    if ($text.Contains($Marker)) {
        Write-Host "[v0.4.4-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "v0.4.4 compatibility check failed: '$Marker' anchor not found."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.4.4-hook] applied: $Marker" -ForegroundColor Cyan
}

# Begin one correlation context before any component creates its own output directory.
Replace-Literal `
    -Old @'
                ShareXModV04Settings modV04 = ShareXModV04Settings.Load();
                modRobustSession = ShareXModRobustScrollingSession.TryCreate(selectedRectangle, Options);
'@ `
    -New @'
                ShareXModV04Settings modV04 = ShareXModV04Settings.Load();
                var modCaptureSession = ShareXModCaptureSessionContext.Begin(selectedRectangle);
                modRobustSession = ShareXModRobustScrollingSession.TryCreate(selectedRectangle, Options);
'@ `
    -Marker "ShareXModCaptureSessionContext.Begin(selectedRectangle)"

# Finalize repair evidence first, then freeze the common session manifest.
Replace-Literal `
    -Old @'
                    modBoundary?.Complete(modEndReason, status, Result);
                    ShareXModRepairPlanner.TryWriteLatestPlan(modV04);
'@ `
    -New @'
                    modBoundary?.Complete(modEndReason, status, Result);
                    ShareXModRepairPlanner.TryWriteLatestPlan(modV04);
                    modCaptureSession?.Complete(modEndReason, status, Result);
'@ `
    -Marker "modCaptureSession?.Complete(modEndReason, status, Result);"

Replace-Literal `
    -Old @'
                        modQualityGuard?.Dispose();
                        modBoundary?.Dispose();
'@ `
    -New @'
                        modQualityGuard?.Dispose();
                        modBoundary?.Dispose();
                        modCaptureSession?.Dispose();
'@ `
    -Marker "modCaptureSession?.Dispose();"

Write-Host "ShareX-Mod v0.4.4 post hooks applied." -ForegroundColor Green
