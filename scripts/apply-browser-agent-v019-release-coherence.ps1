[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$manifestPath = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\manifest.json"
$workerPath = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$expectedName = "LongCapture Browser Agent v0.1.9"
$expectedVersion = "0.1.9"
$expectedDescription = "Active-tab provider for LongCapture Browser Agent v0.1.9 with rail-aware quality recovery, background-window capture and requested end conditions."

function Read-Manifest {
    Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}

function Assert-CompatibleSource {
    $manifest = Read-Manifest
    $worker = [IO.File]::ReadAllText($workerPath)

    $knownManifestVersions = @("0.1.4", "0.1.8", "0.1.9")
    if ($knownManifestVersions -notcontains [string]$manifest.version) {
        throw "Browser Agent v0.1.9 release-coherence compatibility failed: unexpected extension manifest version '$($manifest.version)'."
    }
    if ($worker -notmatch 'protocolVersion:\s*"0\.1\.(4|8|9)"') {
        throw "Browser Agent v0.1.9 release-coherence compatibility failed: expected protocolVersion anchor was not found."
    }
    if ($worker -notmatch 'LongCapture Browser Agent v0\.1\.(4|8|9) attached to this tab') {
        throw "Browser Agent v0.1.9 release-coherence compatibility failed: expected attached-badge anchor was not found."
    }
}

function Assert-FinalCoherence {
    $manifest = Read-Manifest
    $worker = [IO.File]::ReadAllText($workerPath)

    if ([string]$manifest.name -ne $expectedName) {
        throw "Browser Agent v0.1.9 release-coherence failure: manifest name is '$($manifest.name)'."
    }
    if ([string]$manifest.version -ne $expectedVersion) {
        throw "Browser Agent v0.1.9 release-coherence failure: manifest version is '$($manifest.version)'."
    }
    if (-not $worker.Contains('protocolVersion: "0.1.9"')) {
        throw "Browser Agent v0.1.9 release-coherence failure: service-worker protocolVersion is not 0.1.9."
    }
    if (-not $worker.Contains('LongCapture Browser Agent v0.1.9 attached to this tab')) {
        throw "Browser Agent v0.1.9 release-coherence failure: service-worker attached badge is not v0.1.9."
    }
}

Assert-CompatibleSource
if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.9 release-coherence source compatibility passed." -ForegroundColor Green
    exit 0
}

$manifest = Read-Manifest
$manifest.name = $expectedName
$manifest.version = $expectedVersion
$manifest.description = $expectedDescription
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$worker = [IO.File]::ReadAllText($workerPath)
$worker = [regex]::Replace($worker, 'protocolVersion:\s*"0\.1\.(4|8)"', 'protocolVersion: "0.1.9"')
$worker = [regex]::Replace($worker, 'LongCapture Browser Agent v0\.1\.(4|8) attached to this tab', 'LongCapture Browser Agent v0.1.9 attached to this tab')
[IO.File]::WriteAllText($workerPath, $worker, [Text.UTF8Encoding]::new($true))

Assert-FinalCoherence
Write-Host "Browser Agent v0.1.9 release coherence applied: manifest, protocol and attached-badge versions agree." -ForegroundColor Green
