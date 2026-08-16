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

Replace-Literal -Path $automation `
  -Old '        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",' `
  -New @'
        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV020GoalLoopSelfTests",
'@ `
  -Marker '"ShareX.ScreenCaptureLib.ShareXModV020GoalLoopSelfTests",'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.10 goal-loop hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.10 goal-loop hooks applied." -ForegroundColor Green
}
