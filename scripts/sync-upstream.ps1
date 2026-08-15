[CmdletBinding()]
param(
    [string]$UpstreamRemote = "upstream",
    [string]$UpstreamUrl = "https://github.com/ShareX/ShareX.git",
    [string]$UpstreamBranch = "develop",
    [string]$TargetBranch = "develop"
)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$status = & git -C $repoRoot status --porcelain
if ($status) { throw "Working tree is not clean. Commit or stash changes before syncing upstream." }

& git -C $repoRoot remote get-url $UpstreamRemote 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & git -C $repoRoot remote add $UpstreamRemote $UpstreamUrl
    if ($LASTEXITCODE -ne 0) { throw "Failed to add upstream remote." }
}

& git -C $repoRoot fetch $UpstreamRemote $UpstreamBranch
if ($LASTEXITCODE -ne 0) { throw "Failed to fetch upstream." }

& git -C $repoRoot switch $TargetBranch
if ($LASTEXITCODE -ne 0) { throw "Failed to switch to $TargetBranch." }

& git -C $repoRoot merge --ff-only "$UpstreamRemote/$UpstreamBranch"
if ($LASTEXITCODE -ne 0) { throw "Fast-forward sync was not possible. No merge was forced; inspect branch divergence manually." }

Write-Host "Official ShareX base synchronized. Rebase the mod overlay branch onto the new base, then run scripts/apply-overlay.ps1 -CheckOnly." -ForegroundColor Green
