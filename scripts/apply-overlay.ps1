[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$patchRoot = Join-Path $repoRoot "mod-overlay\patches"
$patches = Get-ChildItem -Path $patchRoot -Filter "*.patch" -File | Sort-Object Name
if ($patches.Count -eq 0) { throw "No overlay patches found in $patchRoot" }

foreach ($patch in $patches) {
    & git -C $repoRoot apply --reverse --check -- $patch.FullName 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[overlay] already applied: $($patch.Name)" -ForegroundColor DarkYellow
        continue
    }

    & git -C $repoRoot apply --check -- $patch.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Overlay compatibility check failed for $($patch.Name). Upstream likely changed near a hook point."
    }

    Write-Host "[overlay] compatible: $($patch.Name)" -ForegroundColor Green

    if (-not $CheckOnly) {
        & git -C $repoRoot apply --whitespace=error-all -- $patch.FullName
        if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($patch.Name)" }
        Write-Host "[overlay] applied: $($patch.Name)" -ForegroundColor Cyan
    }
}

if ($CheckOnly) { Write-Host "Overlay compatibility check passed." -ForegroundColor Green }
