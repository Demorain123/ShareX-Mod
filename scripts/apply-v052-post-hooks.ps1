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
        Write-Host "[v0.5.2-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "v0.5.2 compatibility check failed: '$Marker' anchor not found."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.5.2-hook] applied: $Marker" -ForegroundColor Cyan
}

Replace-Literal `
    -Old @'
                        modCaptureSession?.Complete(modEndReason, status, Result);
'@ `
    -New @'
                        ShareXModFinalQualitySummary.TryWrite(modV04, Result);
                        modCaptureSession?.Complete(modEndReason, status, Result);
'@ `
    -Marker "ShareXModFinalQualitySummary.TryWrite(modV04, Result);"

Write-Host "ShareX-Mod v0.5.2 post hooks applied." -ForegroundColor Green
