[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"
if (-not (Test-Path -LiteralPath $automation)) { throw "RC3 automation target missing: $automation" }

$text = [IO.File]::ReadAllText($automation)
$marker = '"ShareX.ScreenCaptureLib.ShareXModV019Rc3RealEvidenceSelfTests",'
if ($text.Contains($marker)) {
    Write-Host "[v0.1.9-rc3] already present: real-evidence suite" -ForegroundColor DarkYellow
}
else {
    $old = '        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",'
    $new = @'
        "ShareX.ScreenCaptureLib.ShareXModV019RecoveryTailSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV019Rc3RealEvidenceSelfTests",
'@
    if (-not $text.Contains($old)) { throw "RC3 anchor missing: v0.1.9 recovery-tail suite was not installed before RC3 hook." }
    if (-not $CheckOnly) {
        $text = $text.Replace($old, $new.TrimEnd("`r", "`n"))
        [IO.File]::WriteAllText($automation, $text, [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.9-rc3] applied/compatible: real-evidence suite" -ForegroundColor Cyan
}

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.9 RC3 real-evidence hook compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.9 RC3 real-evidence hooks applied." -ForegroundColor Green
}
