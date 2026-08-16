[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$resolver = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTransitionResolverV019.cs"
if (-not (Test-Path -LiteralPath $resolver)) { throw "RC6 strong-direct target missing: $resolver" }

$text = [IO.File]::ReadAllText($resolver)
$old = @'
                        if (TryPreferValidatedPriorByInformativeSupport(
                                previousReliable, current, settings, prior, directAnchor.ScrollDelta,
                                out double informativePriorScore,
                                out double informativePriorSupport,
                                out double informativeDirectSupport))
'@
$new = @'
                        // Preserve RC3's high-consensus direct/prior contract. Informative support is
                        // a correction path for weak/noisy near-prior direct anchors (the real RC5
                        // 712px case had agreement=2), not permission to rewrite an agreement>=3
                        // direct anchor that already passed the narrow strong-consensus gate.
                        if (directAnchor.AgreementCount < StrongDirectAgreement &&
                            TryPreferValidatedPriorByInformativeSupport(
                                previousReliable, current, settings, prior, directAnchor.ScrollDelta,
                                out double informativePriorScore,
                                out double informativePriorSupport,
                                out double informativeDirectSupport))
'@
$marker = 'if (directAnchor.AgreementCount < StrongDirectAgreement &&'
if ($text.Contains($marker)) {
    Write-Host "[v0.1.9-rc6] already present: strong-direct consensus ordering" -ForegroundColor DarkYellow
}
else {
    if (-not $text.Contains($old.TrimStart("`r", "`n"))) { throw "RC6 strong-direct ordering anchor missing." }
    if (-not $CheckOnly) {
        $text = $text.Replace($old.TrimStart("`r", "`n"), $new.TrimStart("`r", "`n"))
        [IO.File]::WriteAllText($resolver, $text, [Text.UTF8Encoding]::new($true))
    }
}

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.9 RC6 strong-direct ordering compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.9 RC6 strong-direct ordering applied." -ForegroundColor Green
}
