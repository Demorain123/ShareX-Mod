[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

if ($CheckOnly) {
    & pwsh -NoProfile -File scripts\prepare-browser-agent-v018-final.ps1 -CheckOnly
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & pwsh -NoProfile -File scripts\apply-browser-agent-v019-integrity-proof.ps1 -CheckOnly
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & pwsh -NoProfile -File scripts\apply-browser-agent-v019-integrity-docs.ps1 -CheckOnly
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Browser Agent v0.1.9 overlay compatibility passed." -ForegroundColor Green
    exit 0
}

# v0.1.8 remains a mandatory lower-layer gate. It must be fully eligible before the
# new v0.1.9 proof layer is allowed to change or build anything.
& pwsh -NoProfile -File scripts\prepare-browser-agent-v018-final.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& pwsh -NoProfile -File scripts\apply-browser-agent-v019-integrity-proof.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& pwsh -NoProfile -File scripts\apply-browser-agent-v019-integrity-docs.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Hard pre-build gate: publishing is forbidden below 95/100 or with any critical failure.
& pwsh -NoProfile -File scripts\browser-agent-v019-integrity-proof-score.ps1 -MinimumScore 95
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

git diff --check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Browser Agent v0.1.9 final overlay preparation completed; source is eligible to build." -ForegroundColor Green
