[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$rc6 = @(
  "scripts\run-v04-hooks.ps1",
  "scripts\apply-v041-post-hooks.ps1",
  "scripts\apply-v042-post-hooks.ps1",
  "scripts\apply-v043-post-hooks.ps1",
  "scripts\apply-v044-post-hooks.ps1",
  "scripts\apply-v050-post-hooks.ps1",
  "scripts\apply-v051-post-hooks.ps1",
  "scripts\apply-v052-post-hooks.ps1",
  "scripts\apply-v061-post-hooks.ps1",
  "scripts\apply-v062-post-hooks.ps1",
  "scripts\apply-v064-post-hooks.ps1",
  "scripts\apply-longcapture-v013-standalone-hooks.ps1",
  "scripts\apply-longcapture-v013-rc-hardening.ps1",
  "scripts\apply-longcapture-v014-core-hooks.ps1",
  "scripts\apply-longcapture-v015-fixed-overlay-hooks.ps1",
  "scripts\apply-longcapture-v016-replay-compositor-hooks.ps1",
  "scripts\apply-longcapture-v017-anchor-continuity-hooks.ps1",
  "scripts\apply-longcapture-v018-precut.ps1",
  "scripts\apply-longcapture-v018-trust-split-hooks.ps1",
  "scripts\apply-longcapture-v018-rc-evidence.ps1",
  "scripts\apply-longcapture-v019-recovery-tail-hooks.ps1",
  "scripts\apply-longcapture-v019-rc3-real-evidence-hooks.ps1",
  "scripts\apply-longcapture-v019-rc4-upward-alias-veto.ps1",
  "scripts\apply-longcapture-v019-rc5-downward-alias-consensus.ps1",
  "scripts\apply-longcapture-v019-rc6-sparse-edge-evidence.ps1",
  "scripts\apply-longcapture-v019-rc6-strong-direct-order.ps1"
)

$browser = @(
  "scripts\apply-browser-agent-v01-poc.ps1",
  "scripts\apply-browser-agent-v012-integration.ps1",
  "scripts\apply-browser-agent-v013-main-ui.ps1",
  "scripts\apply-browser-agent-v013-quality-core.ps1",
  "scripts\apply-browser-agent-v013-core-followup.ps1",
  "scripts\apply-browser-agent-v014-prep.ps1",
  "scripts\apply-browser-agent-v014-cancel-timeline.ps1",
  "scripts\apply-browser-agent-v014-operation-log.ps1",
  "scripts\apply-browser-agent-v015-adaptive-quality.ps1",
  "scripts\apply-browser-agent-v015-global-speed-ui.ps1",
  "scripts\apply-browser-agent-v016-calibration-learning-v2.ps1",
  "scripts\apply-browser-agent-v017-modern-ui.ps1"
)

foreach ($hook in $rc6 + $browser) {
  Write-Host "=== $hook ==="
  if ($CheckOnly) { & pwsh -NoProfile -File $hook -CheckOnly }
  else { & pwsh -NoProfile -File $hook }
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# The v0.1.6 and v0.1.7 release scopes must still qualify BEFORE v0.1.8 changes
# their generated files. This makes regressions visible rather than masking them.
if (-not $CheckOnly) {
  & pwsh -NoProfile -File scripts\browser-agent-v016-quality-score.ps1 -MinimumScore 95
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  & pwsh -NoProfile -File scripts\browser-agent-v017-ui-quality-score.ps1 -MinimumScore 95
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

foreach ($hook in @(
  "scripts\apply-browser-agent-v018-reliability-background-stop.ps1",
  "scripts\apply-browser-agent-v018-result-semantics-r2.ps1",
  "scripts\apply-browser-agent-v018-repair-ledger-r3.ps1"
)) {
  Write-Host "=== $hook ==="
  if ($CheckOnly) { & pwsh -NoProfile -File $hook -CheckOnly }
  else { & pwsh -NoProfile -File $hook }
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $CheckOnly) {
  & pwsh -NoProfile -File scripts\browser-agent-v018-quality-score.ps1 -MinimumScore 95
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  git diff --check
}

Write-Host "Browser Agent v0.1.8 final overlay preparation completed." -ForegroundColor Green
