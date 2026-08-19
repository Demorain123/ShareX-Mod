[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$selftest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"
$text = [IO.File]::ReadAllText($selftest)
$marker = 'self-test failed v0.1.8 integrity/Perfect repair policy'
if ($text.Contains($marker)) {
    Write-Host "Browser Agent v0.1.8 integrity/Perfect self-test already seeded." -ForegroundColor DarkYellow
    exit 0
}

$old = @'
            if (!RunFrameCodecRoundTrip())
'@
$new = @'
            if (!BrowserAgentIntegrityPolicyV018.SelfTest() ||
                !BrowserAgentAdaptiveProfiles.UnlimitedRepairAttempts(BrowserAgentRepairPrecision.Perfect) ||
                BrowserAgentAdaptiveProfiles.DefaultRepairTimeLimitSeconds(BrowserAgentRepairPrecision.Perfect) < 600)
            {
                LongCaptureLog.Warn("Browser Agent self-test failed v0.1.8 integrity/Perfect repair policy");
                return 68;
            }

            if (!RunFrameCodecRoundTrip())
'@
if (-not $text.Contains($old)) {
    throw "v0.1.8 integrity/Perfect self-test seed anchor missing"
}
if (-not $CheckOnly) {
    [IO.File]::WriteAllText($selftest, $text.Replace($old, $new), [Text.UTF8Encoding]::new($true))
}
Write-Host "Browser Agent v0.1.8 integrity/Perfect self-test seed compatibility passed." -ForegroundColor Green
