[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$script = Join-Path $PSScriptRoot "apply-browser-agent-v013-main-ui-v2.ps1"
if (-not (Test-Path -LiteralPath $script)) { throw "Missing v0.1.3 UI overlay: $script" }
if ($CheckOnly) {
    & pwsh -NoProfile -File $script -CheckOnly
} else {
    & pwsh -NoProfile -File $script
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
